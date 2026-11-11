using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.AA.App.ViewModels;
using Neuro.AA.App.Views;
using Neuro.AA.Engine;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Attention;
using Neuro.AA.Engine.Chat;
using Neuro.AA.Engine.Devices;
using Neuro.AA.Engine.Events;
using Neuro.AA.Engine.PubSub;
using Neuro.AA.Engine.Sniffing;
using Neuro.AA.Engine.Software;
using Waher.Networking.XMPP;

namespace Neuro.AA.App.Tests;

/// <summary>
/// Getting around: back and forward, one Neuron picked for every screen, links from a thing to
/// where it lives, a search box that finds and does, and several Neurons checked at once.
/// Renders frames for review.
/// </summary>
public class GettingAroundTests
{
	private const string Service = "pubsub.lab.tagroot.io";
	private const string Me = "ops@lab.tagroot.io";
	private const string Lab = "lab.tagroot.io";
	private const string Gbg = "gbg.lab.tagroot.io";
	private const string Sto = "sto.lab.tagroot.io";
	private const string Prod = "prod.example.com";
	private const string Backup = "backup.prod.example.com";
	private const string OpsRoom = "ops-team@muc.lab.tagroot.io";

	internal sealed record Sample(MainWindowViewModel Vm, ChatService Chat, PubSubService PubSub, SoftwareService Software, MonitorService Monitors, AccountManager Manager, FleetService Fleet,
		Guid Ops, Guid Admin, List<string> Said, List<string> Copied, List<string> Asked) : IDisposable
	{
		public void Dispose() => Ui.Said -= this.Said.Add;
	}

	internal static void Capture(TopLevel window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);

		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.AA.slnx")))
			d = d.Parent;
		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		frame!.Save(Path.Combine(folder, name + ".png"));
	}

	internal static T Shown<T>(Visual root, string name)
		where T : Control
		=> root.GetVisualDescendants().OfType<T>().Single(v => v.Name == name && v.IsEffectivelyVisible);

	internal static MainWindow Window(Sample s, string theme, int width = 1440, int height = 900)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		MainWindow window = new() { DataContext = s.Vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		return window;
	}

	private static string[] Texts(IReadOnlyList<ItemAction> actions) => [.. actions.Select(a => a.Text)];

	private static ItemAction Action(IReadOnlyList<ItemAction> actions, string text) => actions.Single(a => a.Text == text);

	/// <summary>One gesture is over: what it changed is a stop now.</summary>
	private static void Step() => Dispatcher.UIThread.RunJobs();

	private static async Task Settle(PubSubViewModel p)
	{
		for (int i = 0; i < 50 && (p.IsBusy || p.IsItemsLoading || p.IsSettingsLoading || p.IsPeopleLoading); i++)
			await Task.Delay(10);
		Dispatcher.UIThread.RunJobs();
	}

	/// <summary>Synthetic sample data: a lab with two edge Neurons, production with a backup that is offline, people, a room and nodes.</summary>
	internal static async Task<Sample> SampleAsync(string? folder = null)
	{
		EngineRuntime.Initialize();
		PubSubViewModel.FadeTime = TimeSpan.Zero;
		string dir = folder ?? Path.Combine(Path.GetTempPath(), "NeuroAA.Around." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts-" + Guid.NewGuid().ToString("N") + ".json")), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = Lab, UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		AccountSession admin = await manager.AddAsync(new AccountProfile { Host = Prod, UserName = "admin", ConnectOnStartup = false, ColorIndex = 3 }, AccountSecret.FromPassword("x"), connect: false);
		Guid o = ops.Profile.Id;
		Guid a = admin.Profile.Id;

		EventHub hub = new();
		MonitorService monitors = new(manager, hub);
		FleetService fleet = new(monitors);
		ChatService chat = new(manager, monitors);
		DevicesService devices = new(manager);
		DateTime now = DateTime.Now;

		void Neuron(Guid account, string jid, string name, double cpu, WatchState state = WatchState.Live)
		{
			monitors.For(account)!.Preview(new WatchedNeuron { AccountId = account, BareJid = jid, Name = name, State = state, HistoryState = HistoryState.Loaded, HistoryCount = 212 });
			if (state == WatchState.Live)
			{
				fleet.Preview(new NeuronHealth
				{
					AccountId = account,
					NeuronJid = jid,
					Software = "Waher.IoTGateway 3.11.1.0",
					Cpu = new SampleSeries(cpu, [Math.Max(0, cpu - 12), Math.Max(0, cpu - 3), cpu], "%"),
					AllocatedMemory = new SampleSeries(640, [590, 602, 640], "MB"),
					FreeMemory = new SampleSeries(5210, [5300, 5280, 5210], "MB"),
					FreeDisk = new SampleSeries(182.4, [183.1, 182.9, 182.4], "GB"),
					ReadAt = now.AddSeconds(-20),
				});
			}
		}

		void Event(int secondsAgo, SnifferDirection d, Guid account, string neuron, string text)
		{
			if (SniffedEventParser.Parse(new SnifferLine(0, now.AddSeconds(-secondsAgo), d, text), new EventSource(account, neuron)) is NeuronEvent e)
				hub.Add(e);
		}

		Neuron(o, Lab, "Lab", 14);
		Neuron(o, Gbg, "Gothenburg edge", 91);
		Neuron(o, Sto, "Stockholm edge", 22);
		Neuron(a, Prod, "Production", 37);
		Neuron(a, Backup, "Backup", 0, WatchState.Offline);

		for (int i = 0; i < 9; i++)
			Event(i * 50 + 15, SnifferDirection.Error, o, Gbg, $"SocketException: Connection reset by peer (lab.tagroot.io:5269)\n\nModule: Waher.Networking.XMPP.Server\nEventId: S2S\nRemoteEndPoint: 10.0.0.{i + 3}:5269");
		for (int i = 0; i < 6; i++)
			Event(i * 70 + 30, SnifferDirection.Error, o, Lab, $"TimeoutException in ConcentratorServer.ReadSensorData (node Meter-{i + 11})\n\nModule: Waher.Things\nObject: Meter-{i + 11}");
		for (int i = 0; i < 3; i++)
			Event(i * 300 + 60, SnifferDirection.Warning, a, Prod, "Certificate for prod.example.com expires in 12 days\n\nModule: Waher.IoTGateway\nEventId: Certificate");

		monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n1", AccountId = o, NeuronJid = Gbg, Received = now.AddMinutes(-3),
			Markdown = "Legal identity application received: [`2f9a1c04-57e1-4c1e@legal.gbg.lab.tagroot.io`](https://gbg.lab.tagroot.io/ValidateLegalId.md?ID=2f9a1c04-57e1-4c1e%40legal.gbg.lab.tagroot.io)\n",
		});
		monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n2", AccountId = o, NeuronJid = Gbg, Received = now.AddMinutes(-34),
			Markdown = "Account requested.\n\n| Account request ||\n|:------|:-------|\n| Name: | Kim Lindqvist |\n| Company: | Example Logistics AB |\n",
		});
		monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n3", AccountId = o, NeuronJid = Lab, Received = now.AddMinutes(-80),
			Markdown = "Legal identity application received: [`77b0e2aa-1d3c-4f0b@legal.lab.tagroot.io`](https://lab.tagroot.io/ValidateLegalId.md?ID=77b0e2aa-1d3c-4f0b%40legal.lab.tagroot.io)\n",
		});

		// People and a room.
		chat.Preview(new Contact(o, "kim@lab.tagroot.io", "Kim Lindqvist", ["On call"], SubscriptionState.Both, true, Availability.Online, null, false));
		chat.Preview(new Contact(o, "robin@gbg.lab.tagroot.io", "Robin Ek", ["On call"], SubscriptionState.Both, true, Availability.Away, "In a meeting", false));
		chat.Preview(new Contact(a, "lead@prod.example.com", "Lee Park", [], SubscriptionState.Both, true, Availability.Online, null, false));
		chat.PreviewRoom(o, OpsRoom, "ops");
		chat.Preview(o, OpsRoom, new RoomOccupant("ops", "Moderator", "Owner", Me + "/aa"));
		chat.Preview(o, OpsRoom, "Deploy window is 14:00 to 15:00 today.", "robin", room: true);
		chat.Preview(o, "kim@lab.tagroot.io", "Morning! Did the backup on gbg finish?", "Kim Lindqvist");
		chat.Preview(o, "robin@gbg.lab.tagroot.io", "gbg is slow again, can you look?", "Robin Ek");
		chat.Preview(a, "lead@prod.example.com", "The certificate on prod runs out soon.", "Lee Park");

		// Nodes on the lab's publish/subscribe service.
		PubSubService pubsub = new(manager);
		PreviewPubSub nodes = new PreviewPubSub(pubsub, Service).Account(o, Me).Account(a, "admin@prod.example.com");
		pubsub.StandIn = nodes;
		nodes.Add("alerts", Me, "Alerts from the lab")
			.Add("announcements", "lead@lab.tagroot.io", "Announcements")
			.Add("build-log", Me, "Build log");
		nodes.Item(Service, "alerts", "a-1", "<alert xmlns='urn:example:alerts' level='warning'><text>Disk on gbg is 91 % full</text></alert>", "robin@gbg.lab.tagroot.io")
			.Person(Service, "alerts", "robin@gbg.lab.tagroot.io", NodeRole.Publisher)
			.Subscriber(Service, "alerts", "kim@lab.tagroot.io", SubscriberState.Subscribed);

		// What the Neurons answer when asked about their software: gbg and prod have an update waiting, sto does not answer.
		List<string> asked = [];
		SoftwareService software = new(monitors, chat)
		{
			CheckStandIn = (_, jid) =>
			{
				asked.Add(jid);
				return jid == Sto ? throw new TimeoutException("The Neuron did not answer.") : ["Checking for new packages.", "Done."];
			},
			ReadStandIn = (account, jid) => new NeuronSoftware
			{
				AccountId = account,
				NeuronJid = jid,
				BuildTime = "2026-10-28 09:14",
				PendingUpdate = jid is Gbg or Prod ? new NeuronNotification { Id = "u-" + jid, AccountId = account, NeuronJid = jid, Received = DateTime.Now, Markdown = "New software is available.", Title = "New software is available" } : null,
				ReadAt = DateTime.Now,
			},
		};

		List<string> said = [];
		Ui.Said += said.Add;
		List<string> copied = [];
		Ui.SetClipboard = text =>
		{
			copied.Add(text);
			return Task.CompletedTask;
		};

		MainWindowViewModel vm = new(manager, fleet, chat, null, devices, pubsub, software);
		await vm.StartAsync();
		vm.Fleet.Refresh();
		vm.Events.Flush();
		vm.Attention.Refresh();
		vm.Chat.Refresh();
		vm.Inbox.Refresh();
		Dispatcher.UIThread.RunJobs();
		return new Sample(vm, chat, pubsub, software, monitors, manager, fleet, o, a, said, copied, asked);
	}

	// ----- Back and forward -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task BackAndForwardWalkThroughWhereYouHaveBeen(string theme)
	{
		using Sample s = await SampleAsync();
		MainWindowViewModel vm = s.Vm;
		string t = theme.ToLowerInvariant();
		vm.ShowAttentionCommand.Execute(null);
		MainWindow window = Window(s, theme);
		Step();

		// A walk through the app: the list, a Neuron, one of its tabs, Events, a conversation.
		vm.ShowFleetCommand.Execute(null);
		Step();
		vm.OpenNeuron(Gbg, s.Ops);
		Step();
		vm.ShowPageTabCommand.Execute(WorkspaceKind.Sources);
		Step();
		vm.ShowEventsCommand.Execute(null);
		Step();
		vm.ShowChat(s.Ops, "kim@lab.tagroot.io");
		Step();
		Assert.True(vm.IsChat);
		Assert.Equal("kim@lab.tagroot.io", vm.Chat.SelectedPerson?.BareJid);
		Assert.True(vm.CanGoBack);
		Assert.False(vm.CanGoForward);
		Assert.Equal("Back to Events (Alt+Left)", vm.BackTip);

		// The list under the Back button, nearest first.
		Assert.Equal(["Events", "Gothenburg edge, Sources", "Gothenburg edge", "The Neurons list", "The Inbox"], Texts(vm.ActionsFor([HistoryMenu.Back]))[..5]);

		// Back, one stop at a time: the screen, and what it showed.
		vm.GoBackCommand.Execute(null);
		Assert.True(vm.IsEvents);
		Assert.Equal("Forward to the chat with Kim Lindqvist (Alt+Right)", vm.ForwardTip);
		vm.GoBackCommand.Execute(null);
		Assert.Equal(WorkspaceKind.Sources, vm.Workspace);
		Assert.Equal(Gbg, vm.Page?.BareJid);
		Capture(window, $"around-back-page-{t}-1440");
		vm.GoBackCommand.Execute(null);
		Assert.Equal(WorkspaceKind.Neuron, vm.Workspace);
		vm.GoBackCommand.Execute(null);
		Assert.True(vm.IsFleet);
		vm.GoBackCommand.Execute(null);
		Assert.True(vm.IsAttention);
		Capture(window, $"around-back-inbox-{t}-1440");

		// Forward again, with the keyboard: Alt+Right.
		window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
		Assert.True(vm.IsFleet);
		window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
		Assert.Equal(WorkspaceKind.Neuron, vm.Workspace);
		Assert.Equal(Gbg, vm.Page?.BareJid);
		window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
		Assert.True(vm.IsFleet);
		Assert.True(vm.CanGoForward);

		// Going somewhere new from the middle drops what was ahead, as in a browser.
		vm.OpenNeuron(Lab, s.Ops, WorkspaceKind.NeuronConsole);
		Step();
		Assert.False(vm.CanGoForward);
		Assert.Equal("Back to the Neurons list (Alt+Left)", vm.BackTip);

		// From one Neuron's page to another's and back: each is its own stop.
		vm.OpenNeuron(Sto, s.Ops);
		Step();
		vm.GoBackCommand.Execute(null);
		Assert.Equal(Lab, vm.Page?.BareJid);
		Assert.Equal(WorkspaceKind.NeuronConsole, vm.Workspace);
		vm.GoForwardCommand.Execute(null);
		Assert.Equal(Sto, vm.Page?.BareJid);

		// The crumb on a Neuron's page goes up to all Neurons. That is a new stop, not a step back.
		Shown<Button>(window, "BackButton");
		vm.BackToNeuronsCommand.Execute(null);
		Step();
		Assert.True(vm.IsFleet);
		Assert.Equal("Back to Stockholm edge (Alt+Left)", vm.BackTip);

		// An account's page remembers which account and which tab.
		vm.ShowAccount(s.Admin);
		Step();
		vm.ShowAccountTabCommand.Execute(WorkspaceKind.AccountStorage);
		Step();
		vm.ShowSettingsCommand.Execute(null);
		Step();
		vm.GoBackCommand.Execute(null);
		Assert.True(vm.IsAccountStorage);
		Assert.Equal(s.Admin, vm.SelectedAccount?.Session.Profile.Id);
		vm.GoBackCommand.Execute(null);
		Assert.True(vm.IsAccounts);

		// Jumping several stops at once from the list.
		await Action(vm.ActionsFor([HistoryMenu.Back]), "The Inbox").RunAsync();
		Assert.True(vm.IsAttention);
		Assert.True(vm.CanGoForward);
		Assert.Equal("Forward to the Neurons list (Alt+Right)", vm.ForwardTip);

		window.Close();
	}

	// ----- One Neuron for every screen -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task TheNeuronPickedAtTheTopFiltersEveryScreen(string theme)
	{
		using Sample s = await SampleAsync();
		MainWindowViewModel vm = s.Vm;
		string t = theme.ToLowerInvariant();
		vm.ShowAttentionCommand.Execute(null);
		MainWindow window = Window(s, theme);

		// The chips show where all Neurons are on screen at once, not on one Neuron's page or in Chat.
		Assert.True(vm.IsLensVisible);
		vm.OpenNeuron(Lab, s.Ops);
		Assert.False(vm.IsLensVisible);
		vm.ShowChatCommand.Execute(null);
		Assert.False(vm.IsLensVisible);
		vm.ShowAttentionCommand.Execute(null);

		int everything = vm.Inbox.Rows.Count;
		Assert.False(vm.Inbox.HasElsewhere);
		Capture(window, $"around-filter-inbox-all-{t}-1440");

		// Pick the Gothenburg edge: the Inbox keeps what is from it and says what it leaves out.
		vm.ToggleNeuronCommand.Execute(vm.NeuronChips.Single(c => c.BareJid == Gbg));
		Assert.Equal(Gbg, vm.LensJid);
		Assert.NotEmpty(vm.Inbox.Rows);
		Assert.True(vm.Inbox.Rows.Count < everything);
		Assert.All(vm.Inbox.Rows, r => Assert.EndsWith(Gbg, r.Jid));
		Assert.Contains(vm.Inbox.Rows, r => r.Jid == "robin@gbg.lab.tagroot.io");
		Assert.Equal($"{everything - vm.Inbox.Rows.Count} more wait from elsewhere", vm.Inbox.ElsewhereText);
		Assert.Equal(everything, vm.Inbox.WaitingCount);
		Capture(window, $"around-filter-inbox-{t}-1440");

		// The same Neuron on the list, in Events, on the map and in the Console.
		vm.ShowFleetCommand.Execute(null);
		Assert.Equal([Gbg], vm.Fleet.Shown.Select(r => r.BareJid));
		Capture(window, $"around-filter-neurons-{t}-1440");

		vm.ShowEventsCommand.Execute(null);
		vm.Events.SeverityFilter = SeverityFilter.All;
		vm.Events.Flush();
		Assert.NotEmpty(vm.Events.Stream);
		Assert.All(vm.Events.Stream, e => Assert.Equal(Gbg, e.Source.NeuronJid));

		vm.ShowConsoleCommand.Execute(null);
		Assert.Equal([Gbg], vm.Console.Targets.Where(x => x.IsChecked).Select(x => x.BareJid));
		Capture(window, $"around-filter-console-{t}-1440");

		// Ticking another Neuron by hand in the Console stands: the chip does not take it back.
		vm.Console.Targets.Single(x => x.BareJid == Sto).IsChecked = true;
		vm.ShowEventsCommand.Execute(null);
		vm.ShowConsoleCommand.Execute(null);
		Assert.Equal([Gbg, Sto], vm.Console.Targets.Where(x => x.IsChecked).Select(x => x.BareJid).Order());

		// "Run a command on these" names its Neurons, and they are the ones ticked.
		vm.ShowConsole([(s.Ops, Lab), (s.Admin, Prod)]);
		Assert.Equal([Lab, Prod], vm.Console.Targets.Where(x => x.IsChecked).Select(x => x.BareJid).Order());

		// A Neuron's own page is not filtered: its "Waiting for you" still lists what is from it.
		vm.OpenNeuron(Lab, s.Ops);
		Assert.NotEmpty(vm.Page!.Waiting);

		// "Show all" in the Inbox is the same as the All Neurons chip.
		vm.ShowAttentionCommand.Execute(null);
		Shown<Button>(window, "ElsewhereButton");
		vm.Inbox.ShowElsewhereCommand.Execute(null);
		Assert.Null(vm.LensJid);
		Assert.True(vm.IsAllNeurons);
		Assert.Equal(everything, vm.Inbox.Rows.Count);
		Assert.False(vm.Inbox.HasElsewhere);
		Assert.Equal(5, vm.Fleet.Shown.Count);

		// An empty list under a filter says why it is empty.
		vm.ToggleNeuronCommand.Execute(vm.NeuronChips.Single(c => c.BareJid == Sto));
		Assert.Empty(vm.Inbox.Rows);
		Assert.Equal("Nothing from Stockholm edge here", vm.Inbox.EmptyTitle);
		Capture(window, $"around-filter-inbox-empty-{t}-1440");

		window.Close();
	}

	[AvaloniaFact]
	public async Task TheNeuronPickedIsRememberedForNextTime()
	{
		string dir = Path.Combine(Path.GetTempPath(), "NeuroAA.Around." + Guid.NewGuid().ToString("N"));
		string file = Path.Combine(dir, "ui.json");

		using (Sample first = await SampleAsync(dir))
		{
			using Neuro.AA.Engine.Alerts.AlertService alerts = new(first.Monitors, new Neuro.AA.Engine.Alerts.AlertSettings());
			first.Vm.Settings.Attach(alerts, new Neuro.AA.Engine.Alerts.JsonAlertSettingsStore(Path.Combine(dir, "alerts.json")), new JsonUiSettingsStore(file));
			first.Vm.ToggleNeuronCommand.Execute(first.Vm.NeuronChips.Single(c => c.BareJid == Gbg));
			Assert.Equal(Gbg, new JsonUiSettingsStore(file).Load().Neuron);
		}

		// The next run: the Neuron is picked again as soon as it is among the Neurons.
		using Sample second = await SampleAsync(dir);
		using Neuro.AA.Engine.Alerts.AlertService again = new(second.Monitors, new Neuro.AA.Engine.Alerts.AlertSettings());
		Assert.Null(second.Vm.LensJid);
		second.Vm.Settings.Attach(again, new Neuro.AA.Engine.Alerts.JsonAlertSettingsStore(Path.Combine(dir, "alerts.json")), new JsonUiSettingsStore(file));
		Assert.Equal(Gbg, second.Vm.LensJid);
		Assert.True(second.Vm.NeuronChips.Single(c => c.BareJid == Gbg).IsSelected);
		Assert.Equal([Gbg], second.Vm.Fleet.Shown.Select(r => r.BareJid));

		// All Neurons is remembered too.
		second.Vm.ShowAllNeuronsCommand.Execute(null);
		Assert.Null(new JsonUiSettingsStore(file).Load().Neuron);
	}

	// ----- Go to -----

	[AvaloniaFact]
	public async Task AThingLeadsToWhereItBelongs()
	{
		using Sample s = await SampleAsync();
		MainWindowViewModel vm = s.Vm;
		MainWindow window = Window(s, "Light");

		// A request to subscribe leads to the node it is about, on its People tab, and to the person.
		s.PubSub.Preview(new NodeRequest(s.Ops, Service, "alerts", "pat@partner.example", DateTime.Now.AddMinutes(-2)));
		vm.ShowAttentionCommand.Execute(null);
		InboxRowViewModel ask = vm.Inbox.Rows.Single(r => r.Kind == InboxRowKind.NodeRequest);
		IReadOnlyList<ItemAction> forAsk = vm.Inbox.ActionsFor([ask]);
		Assert.Contains("The node, and who subscribes", Texts(forAsk));
		Assert.Contains("Chat with them", Texts(forAsk));
		Step();

		await Action(forAsk, "The node, and who subscribes").RunAsync();
		await Settle(vm.PubSub);
		await Settle(vm.PubSub);
		Assert.True(vm.IsPubSub);
		Assert.Equal(Lab, vm.Page?.BareJid);
		Assert.Equal("alerts", vm.PubSub.SelectedNode?.Node);
		Assert.True(vm.PubSub.IsPeople);
		Capture(window, "around-goto-node-light-1440");

		// And back to the Inbox, where the request still waits.
		Step();
		vm.GoBackCommand.Execute(null);
		Assert.True(vm.IsAttention);

		// Someone on a node's lists: chat with them, even when nothing was said before.
		vm.GoForwardCommand.Execute(null);
		await Settle(vm.PubSub);
		NodeSubscriberRow kim = vm.PubSub.Subscribers.Single(x => x.Jid == "kim@lab.tagroot.io");
		await Action(vm.PubSub.ActionsFor([kim]), "Chat with them").RunAsync();
		Assert.True(vm.IsChat);
		Assert.Equal("kim@lab.tagroot.io", vm.Chat.SelectedPerson?.BareJid);

		await Action(forAsk, "Chat with them").RunAsync();
		Assert.True(vm.IsChat);
		Assert.Equal("pat@partner.example", vm.Chat.SelectedPerson?.BareJid);
		Assert.DoesNotContain(s.Said, x => x.StartsWith("No conversation", StringComparison.Ordinal));

		// You are not offered a chat with yourself.
		vm.ShowNode(s.Ops, Service, "alerts", people: true);
		await Settle(vm.PubSub);
		Assert.DoesNotContain("Chat with them", Texts(vm.PubSub.ActionsFor([vm.PubSub.People.Single(x => x.IsMe)])));

		// A node that is not there says so, and stays where it is.
		vm.ShowNode(s.Ops, Service, "no-such-node");
		await Settle(vm.PubSub);
		Assert.Contains(s.Said, x => x.StartsWith("no-such-node is not among the nodes", StringComparison.Ordinal));

		window.Close();
	}

	// ----- The search box -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task TheSearchBoxFindsThingsAndDoesThings(string theme)
	{
		using Sample s = await SampleAsync();
		MainWindowViewModel vm = s.Vm;
		string t = theme.ToLowerInvariant();
		vm.ShowAttentionCommand.Execute(null);
		MainWindow window = Window(s, theme);
		SearchViewModel search = vm.Search;

		SearchItem First(string query)
		{
			search.Open();
			search.Query = query;
			Assert.True(search.HasResults, "Nothing found for " + query);
			return search.Results[0];
		}

		// Nothing typed: the places to go.
		search.Open();
		Assert.All(search.Results, r => Assert.Equal("Screen", r.Kind));
		Assert.Equal("Inbox", search.Results[0].Title);
		search.Close();

		// A person, by name or by address.
		Assert.Equal(("Kim Lindqvist", "Person"), (First("kim").Title, First("kim").Kind));
		Assert.Equal("Robin Ek", First("robin@gbg").Title);
		Capture(window, $"around-search-person-{t}-1440");
		search.Run();
		Assert.True(vm.IsChat);
		Assert.Equal("robin@gbg.lab.tagroot.io", vm.Chat.SelectedPerson?.BareJid);

		// A room.
		Assert.Equal("Room", First("ops-team").Kind);
		search.Run();
		Assert.Equal(OpsRoom, vm.Chat.SelectedPerson?.BareJid);

		// A problem, by what it says.
		SearchItem problem = First("socketexception");
		Assert.Equal("Problem", problem.Kind);
		search.Run();
		Assert.True(vm.IsEvents);
		Assert.Contains("SocketException", vm.Events.SelectedIssue?.Title);

		// A Neuron by its name, and one of its tabs by saying both.
		Assert.Equal(("Gothenburg edge", "Neuron"), (First("goth").Title, First("goth").Kind));
		search.Run();
		Assert.Equal(WorkspaceKind.Neuron, vm.Workspace);
		Assert.Equal(Gbg, vm.Page?.BareJid);
		Assert.Equal(("Sources", "Stockholm edge"), (First("stock sources").Title, First("stock sources").Hint));
		search.Run();
		Assert.Equal(WorkspaceKind.Sources, vm.Workspace);
		Assert.Equal(Sto, vm.Page?.BareJid);

		// Things to do come from the same list as the right-click menu, so they cannot disagree.
		string[] menu = [.. vm.NeuronActions(Gbg, null, WorkspaceKind.Fleet).Where(a => a.IsEnabled).Select(a => a.Text)];
		search.Open();
		search.Query = "gothenburg";
		Assert.Equal(menu, search.Results.Where(r => r.Hint == "Gothenburg edge").Select(r => r.Title));
		Capture(window, $"around-search-neuron-{t}-1440");

		// A risky one is red, one per Neuron, and choosing it still stops before anything happens.
		search.Query = "reboot";
		Assert.Equal(5, search.Results.Count);
		Assert.All(search.Results, r => Assert.True(r.IsDanger));
		Capture(window, $"around-search-reboot-{t}-1440");
		search.Selected = search.Results.Single(r => r.Hint == "Production");
		search.Run();
		Assert.Equal(WorkspaceKind.NeuronConsole, vm.Workspace);
		Assert.Equal(Prod, vm.Page?.BareJid);
		Assert.Equal("Reboot", vm.PageConsole?.CommandText);
		Assert.Empty(vm.PageConsole!.Runs);

		// Copying from the keyboard.
		Assert.Equal("Copy address", First("copy address lab").Title);
		search.Selected = search.Results.Single(r => r.Hint == "Lab");
		search.Run();
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(Lab, s.Copied[^1]);

		// A node, once its service has been looked at.
		vm.OpenNeuron(Lab, s.Ops, WorkspaceKind.PubSub);
		await Settle(vm.PubSub);
		vm.ShowAttentionCommand.Execute(null);
		Assert.Equal(("build-log", "Node"), (First("build").Title, First("build").Kind));
		Assert.Equal("Build log, on " + Service, First("build").Hint);
		search.Run();
		await Settle(vm.PubSub);
		Assert.True(vm.IsPubSub);
		Assert.Equal("build-log", vm.PubSub.SelectedNode?.Node);

		// An account, and what can be done with it.
		Assert.Equal("Account", First("admin@prod").Kind);
		Assert.Contains(search.Results, r => r.Title == "Connect" && r.Kind == "Do");

		// More than fits says so.
		search.Query = "e";
		Assert.Equal(40, search.Results.Count);
		Assert.True(search.HasMore);
		Assert.StartsWith("The first 40 of ", search.MoreText);
		search.Close();

		// Nothing: a hint at what can be asked for.
		search.Open();
		search.Query = "zzzz";
		Assert.False(search.HasResults);
		Capture(window, $"around-search-nothing-{t}-1440");
		search.Close();

		window.Close();
	}

	// ----- Several Neurons at once -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task SeveralNeuronsAreCheckedForUpdatesTogether(string theme)
	{
		using Sample s = await SampleAsync();
		MainWindowViewModel vm = s.Vm;
		string t = theme.ToLowerInvariant();
		vm.ShowFleetCommand.Execute(null);
		MainWindow window = Window(s, theme);

		FleetRowViewModel Row(string jid) => vm.Fleet.Rows.Single(r => r.BareJid == jid);

		// Pick every Neuron: the bar offers the check next to the Console.
		List<FleetRowViewModel> all = [.. vm.Fleet.Shown];
		IReadOnlyList<ItemAction> many = vm.Fleet.ActionsFor([.. all]);
		ItemAction check = Action(many, "Check for updates");
		Assert.True(check.InBar);
		Assert.All(all, r => Assert.False(r.HasUpdateNote));

		ListBox list = Shown<ListBox>(window, "List");
		list.SelectAll();
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"around-updates-picked-{t}-1440");

		await check.RunAsync();
		Dispatcher.UIThread.RunJobs();

		// Each Neuron that is connected was asked, once. The one that is offline was not.
		Assert.Equal([Gbg, Lab, Prod, Sto], s.Asked.Order());
		Assert.Equal(("Update ready", "busy"), (Row(Gbg).UpdateNote, Row(Gbg).UpdateTone));
		Assert.Equal(("Update ready", "busy"), (Row(Prod).UpdateNote, Row(Prod).UpdateTone));
		Assert.Equal(("Up to date", "ok"), (Row(Lab).UpdateNote, Row(Lab).UpdateTone));
		Assert.Equal(("No answer", "bad"), (Row(Sto).UpdateNote, Row(Sto).UpdateTone));
		Assert.Equal(("Not connected", "idle"), (Row(Backup).UpdateNote, Row(Backup).UpdateTone));
		Assert.Equal("2 Neurons have an update waiting. 1 did not answer, 1 is not connected", s.Said[^1]);
		Capture(window, $"around-updates-checked-{t}-1440");

		// Nothing was installed: upgrading stays on one Neuron's Software tab, with its typed confirmation.
		vm.OpenNeuron(Gbg, s.Ops, WorkspaceKind.Updates);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("Update ready", vm.Updates.Selected?.StateLabel);

		// One Neuron alone says its name.
		s.Said.Clear();
		await vm.CheckUpdatesAsync([(s.Ops, Gbg), (s.Ops, Lab)]);
		Assert.Equal("1 Neuron has an update waiting: Gothenburg edge", s.Said[^1]);
		await vm.CheckUpdatesAsync([(s.Ops, Lab)]);
		Assert.Equal("No updates are waiting", s.Said[^1]);

		window.Close();
	}

	// ----- Small windows -----

	[AvaloniaFact]
	public async Task ASmallWindowKeepsBackForwardAndTheSearch()
	{
		using Sample s = await SampleAsync();
		MainWindowViewModel vm = s.Vm;
		vm.ShowAttentionCommand.Execute(null);
		MainWindow window = Window(s, "Light", 760, 600);
		Step();
		vm.OpenNeuron(Gbg, s.Ops, WorkspaceKind.Sources);
		Step();

		Assert.True(Shown<Button>(window, "BackButton").IsEffectivelyEnabled);
		Assert.False(Shown<Button>(window, "ForwardButton").IsEffectivelyEnabled);
		Capture(window, "around-back-page-light-760");

		vm.Search.Open();
		vm.Search.Query = "gothenburg";
		Dispatcher.UIThread.RunJobs();
		Border results = Shown<Border>(window, "SearchFloat");
		Assert.True(results.Bounds.Width <= 760 - 56);
		Capture(window, "around-search-light-760");
		vm.Search.Close();

		vm.ShowAttentionCommand.Execute(null);
		vm.ToggleNeuronCommand.Execute(vm.NeuronChips.Single(c => c.BareJid == Gbg));
		Capture(window, "around-filter-inbox-light-760");

		window.Close();
	}
}
