using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Devices;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Sniffing;
using Neuro.Nat.Engine.Topology;
using Waher.Networking.XMPP;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// One page per Neuron, one page per account, and Chat in two halves. Renders frames for
/// review and checks that every tab shows the one Neuron or account it belongs to.
/// </summary>
public class NeuronPageTests
{
	private sealed record Sample(MainWindowViewModel Vm, ChatService Chat, DevicesService Devices, MonitorService Monitors, Guid Ops, Guid Admin);

	private static void Capture(TopLevel window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);

		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;
		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		frame!.Save(Path.Combine(folder, name + ".png"));
	}

	private static T Find<T>(Visual root, Func<T, bool>? where = null)
		where T : Visual
		=> root.GetVisualDescendants().OfType<T>().First(v => v.IsEffectivelyVisible && (where is null || where(v)));

	private static bool Shows<T>(Visual root, Func<T, bool>? where = null)
		where T : Visual
		=> root.GetVisualDescendants().OfType<T>().Any(v => v.IsEffectivelyVisible && (where is null || where(v)));

	private static MainWindow Window(Sample s, string theme, int width = 1440, int height = 900)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		MainWindow window = new() { DataContext = s.Vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		return window;
	}

	/// <summary>Synthetic sample data: a lab with edge Neurons under it, federated with production.</summary>
	private static async Task<Sample> SampleAsync()
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Pages." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.tagroot.io", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		AccountSession admin = await manager.AddAsync(new AccountProfile { Host = "prod.example.com", UserName = "admin", ConnectOnStartup = false, ColorIndex = 3 }, AccountSecret.FromPassword("x"), connect: false);

		EventHub hub = new();
		MonitorService monitors = new(manager, hub);
		FleetService fleet = new(monitors);
		ChatService chat = new(manager, monitors);
		DevicesService devices = new(manager);
		DateTime now = DateTime.Now;

		List<TopologyService.NeuronAnswer> answers = [];
		WatchedNeuron Neuron(AccountSession s, string jid, string name, double cpu, double freeDisk = 182.4, WatchState state = WatchState.Live)
		{
			WatchedNeuron n = new() { AccountId = s.Profile.Id, BareJid = jid, Name = name, State = state, HistoryState = HistoryState.Loaded, HistoryCount = 212 };
			monitors.For(s.Profile.Id)!.Preview(n);
			if (state == WatchState.Live)
			{
				fleet.Preview(new NeuronHealth
				{
					AccountId = s.Profile.Id,
					NeuronJid = jid,
					Software = "Waher.IoTGateway 3.11.1.0",
					Cpu = new SampleSeries(cpu, [Math.Max(0, cpu - 30), Math.Max(0, cpu - 22), Math.Max(0, cpu - 25), Math.Max(0, cpu - 9), Math.Max(0, cpu - 12), Math.Max(0, cpu - 3), cpu], "%"),
					AllocatedMemory = new SampleSeries(640, [590, 602, 615, 611, 628, 640], "MB"),
					FreeMemory = new SampleSeries(5210, [5300, 5280, 5260, 5245, 5230, 5210], "MB"),
					FreeDisk = new SampleSeries(freeDisk, [freeDisk + 0.9, freeDisk + 0.6, freeDisk + 0.4, freeDisk + 0.1, freeDisk], "GB"),
					ReadAt = now.AddSeconds(-20),
				});
			}

			return n;
		}

		void Answer(WatchedNeuron n, string? parent, params string[] federation)
		{
			string p = parent is null ? string.Empty : $"\"{parent}\" -> \"{n.BareJid}\" [dir=back];";
			string f = string.Concat(federation.Select(x => $"\"{n.BareJid}\" -> \"{x}\";"));
			answers.Add(new(n, n.BareJid, DotGraph.Edges(p), DotGraph.Edges(f), [], null));
		}

		void Event(int secondsAgo, SnifferDirection d, AccountSession s, string neuron, string text)
		{
			if (SniffedEventParser.Parse(new SnifferLine(0, now.AddSeconds(-secondsAgo), d, text), new EventSource(s.Profile.Id, neuron)) is NeuronEvent e)
				hub.Add(e);
		}

		WatchedNeuron lab = Neuron(ops, "lab.tagroot.io", "Lab", 14);
		WatchedNeuron gbg = Neuron(ops, "gbg.lab.tagroot.io", "Gothenburg edge", 91);
		WatchedNeuron sto = Neuron(ops, "sto.lab.tagroot.io", "Stockholm edge", 22);
		WatchedNeuron prod = Neuron(admin, "prod.example.com", "Production", 37);
		Neuron(admin, "backup.prod.example.com", "Backup", 0, state: WatchState.Offline);

		Answer(lab, "tagroot.io", "tagroot.io", "prod.example.com");
		Answer(gbg, "lab.tagroot.io", "lab.tagroot.io");
		Answer(sto, "lab.tagroot.io", "lab.tagroot.io", "gbg.lab.tagroot.io");
		Answer(prod, null, "lab.tagroot.io");

		for (int i = 0; i < 9; i++)
			Event(i * 50 + 15, SnifferDirection.Error, ops, "gbg.lab.tagroot.io",
				$"SocketException: Connection reset by peer (lab.tagroot.io:5269)\n\nModule: Waher.Networking.XMPP.Server\nEventId: S2S\nStackTrace:    at Waher.Networking.BinaryTcpClient.ReadAsync()\nRemoteEndPoint: 10.0.0.{i + 3}:5269");
		for (int i = 0; i < 4; i++)
			Event(i * 130 + 40, SnifferDirection.Warning, ops, "gbg.lab.tagroot.io", $"Endpoint 203.0.113.{40 + i} blocked after 5 failed logins\n\nModule: Waher.Security.LoginMonitor\nEventId: Blocked");
		for (int i = 0; i < 3; i++)
			Event(i * 200 + 90, SnifferDirection.Information, ops, "gbg.lab.tagroot.io", $"Backup of database completed in {41 + i} s\n\nModule: Waher.IoTGateway\nEventId: Backup");
		for (int i = 0; i < 6; i++)
			Event(i * 70 + 30, SnifferDirection.Error, ops, "lab.tagroot.io", $"TimeoutException in ConcentratorServer.ReadSensorData (node Meter-{i + 11})\n\nModule: Waher.Things\nObject: Meter-{i + 11}");
		for (int i = 0; i < 3; i++)
			Event(i * 300 + 60, SnifferDirection.Warning, admin, "prod.example.com", "Certificate for prod.example.com expires in 12 days\n\nModule: Waher.IoTGateway\nEventId: Certificate");

		monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n1", AccountId = ops.Profile.Id, NeuronJid = "gbg.lab.tagroot.io", Received = now.AddMinutes(-3),
			Markdown = "Legal identity application received: [`2f9a1c04-57e1-4c1e@legal.gbg.lab.tagroot.io`](https://gbg.lab.tagroot.io/ValidateLegalId.md?ID=2f9a1c04-57e1-4c1e%40legal.gbg.lab.tagroot.io)\n",
		});
		monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n2", AccountId = ops.Profile.Id, NeuronJid = "gbg.lab.tagroot.io", Received = now.AddMinutes(-34),
			Markdown = "Account requested.\n\n| Account request ||\n|:------|:-------|\n| Name: | Kim Lindqvist |\n| Company: | Example Logistics AB |\n",
		});
		monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n3", AccountId = ops.Profile.Id, NeuronJid = "lab.tagroot.io", Received = now.AddMinutes(-80),
			Markdown = "Legal identity application received: [`77b0e2aa-1d3c-4f0b@legal.lab.tagroot.io`](https://lab.tagroot.io/ValidateLegalId.md?ID=77b0e2aa-1d3c-4f0b%40legal.lab.tagroot.io)\n",
		});

		MainWindowViewModel vm = new(manager, fleet, chat, null, devices);
		await vm.StartAsync();
		vm.Topology.Load(TopologyService.Merge(answers, now.AddMinutes(-2)), includesGroups: false);
		vm.Fleet.Refresh();
		vm.Events.Flush();
		vm.Attention.Refresh();
		vm.Inbox.Refresh();
		return new Sample(vm, chat, devices, monitors, ops.Profile.Id, admin.Profile.Id);
	}

	// ----- The Neuron's page -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task ANeuronHasOnePageWithEverythingAboutIt(string theme)
	{
		Sample s = await SampleAsync();
		MainWindowViewModel vm = s.Vm;
		vm.ShowNeuronsCommand.Execute(null);
		MainWindow window = Window(s, theme);
		string t = theme.ToLowerInvariant();
		Assert.True(vm.IsNeuronBoard);
		Assert.False(vm.IsNeuronPage);

		// Opening a Neuron from the list leads to its page, on the Overview.
		FleetRowViewModel row = vm.Fleet.Rows.First(r => r.BareJid == "gbg.lab.tagroot.io");
		vm.Fleet.OpenCommand.Execute(row);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(WorkspaceKind.Neuron, vm.Workspace);
		Assert.Equal(PlaceKind.Neurons, vm.Place);
		Assert.True(vm.IsNeuronPage);
		NeuronPageViewModel page = vm.Page!;
		Assert.Equal("gbg.lab.tagroot.io", page.BareJid);
		Assert.Equal(page.Label, vm.WorkspaceTitle);
		Assert.True(page.IsLive);
		Assert.True(page.HighCpu);
		Assert.Contains("91", page.CpuText);
		Assert.Equal(9, page.ErrorsLastHour);
		Assert.True(page.OpenIssues >= 2);

		// The Overview's lists are this Neuron's only: its issues, its events, what waits for you, its links.
		Assert.NotEmpty(vm.NeuronEvents.Issues);
		Assert.All(vm.NeuronEvents.Stream, e => Assert.Equal("gbg.lab.tagroot.io", e.Source.NeuronJid));
		Assert.Equal(2, page.Waiting.Count);
		Assert.Contains(page.Links, l => l.Id == "lab.tagroot.io" && l.Relation.StartsWith("Its parent", StringComparison.Ordinal));
		Assert.Contains(page.Links, l => l.Id == "sto.lab.tagroot.io");
		Assert.True(Shows<NeuronOverviewView>(window));
		Capture(window, $"neuron-overview-{t}-1440");

		// The events of all Neurons are untouched by the page's own filter.
		Assert.Empty(vm.Events.NeuronFilter);

		// Events: the same screen as the Events place, for this Neuron only.
		vm.ShowPageTabCommand.Execute(WorkspaceKind.NeuronEvents);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.IsNeuronEvents);
		Assert.True(Shows<EventsView>(window, v => ReferenceEquals(v.DataContext, vm.NeuronEvents)));
		Assert.False(Shows<EventsView>(window, v => ReferenceEquals(v.DataContext, vm.Events)));
		Capture(window, $"neuron-events-{t}-1440");

		// An issue on the Overview opens on the Events tab.
		vm.ShowPageTabCommand.Execute(WorkspaceKind.Neuron);
		IssueItemViewModel issue = vm.NeuronEvents.Issues[0];
		vm.OpenPageIssueCommand.Execute(issue);
		Assert.True(vm.IsNeuronEvents);
		Assert.Same(issue, vm.NeuronEvents.SelectedIssue);

		// Console: this Neuron only, with no list of Neurons to pick from.
		vm.ShowPageTabCommand.Execute(WorkspaceKind.NeuronConsole);
		Dispatcher.UIThread.RunJobs();
		ConsoleViewModel console = vm.PageConsole!;
		Assert.True(console.IsPinned);
		Assert.Equal("gbg.lab.tagroot.io", Assert.Single(console.Targets, x => x.IsChecked).BareJid);
		Assert.NotSame(vm.Console, console);
		Capture(window, $"neuron-console-{t}-1440");

		// The console keeps what was typed when you leave the tab and come back.
		console.CommandText = "Print(Now)";
		vm.ShowPageTabCommand.Execute(WorkspaceKind.Updates);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.Updates.IsPinned);
		Assert.Equal("gbg.lab.tagroot.io", vm.Updates.Selected?.NeuronJid);
		Capture(window, $"neuron-software-{t}-1440");
		vm.ShowPageTabCommand.Execute(WorkspaceKind.NeuronConsole);
		Assert.Same(console, vm.PageConsole);
		Assert.Equal("Print(Now)", vm.PageConsole!.CommandText);

		// Sources: pinned to the Neuron, with no list of Neurons to pick from.
		vm.ShowPageTabCommand.Execute(WorkspaceKind.Sources);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.Sources.IsPinned);
		Assert.Equal("gbg.lab.tagroot.io", vm.Sources.SelectedNeuron?.BareJid);
		Assert.False(vm.Sources.HasPinnedNote);
		Capture(window, $"neuron-sources-{t}-1440");

		// Identity: the Neuron's own, not the list of every account's.
		vm.ShowPageTabCommand.Execute(WorkspaceKind.Identities);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.Identities.IsOneNeuron);
		Assert.False(vm.Identities.ShowsList);
		Capture(window, $"neuron-identity-{t}-1440");

		// Publish/subscribe is a tab only on the Neuron that hosts the account.
		Assert.False(page.HostsAccount);
		vm.OpenNeuron("lab.tagroot.io");
		Assert.True(vm.Page!.HostsAccount);
		Assert.Equal(["lab.tagroot.io"], vm.NeuronEvents.NeuronFilter);
		Assert.Single(vm.Page.Waiting);

		// Another Neuron has a console of its own.
		vm.ShowPageTabCommand.Execute(WorkspaceKind.NeuronConsole);
		Assert.NotSame(console, vm.PageConsole);
		Assert.Equal(string.Empty, vm.PageConsole!.CommandText);

		// A link on the Overview leads to the neighbour's page.
		vm.ShowPageTabCommand.Execute(WorkspaceKind.Neuron);
		vm.OpenLinkCommand.Execute(vm.Page.Links.First(l => l.Id == "gbg.lab.tagroot.io"));
		Assert.Equal("gbg.lab.tagroot.io", vm.Page!.BareJid);
		Assert.Equal(WorkspaceKind.Neuron, vm.Workspace);

		// What waits for you opens in the Inbox, on that row.
		vm.OpenWaitingCommand.Execute(vm.Page.Waiting[0]);
		Assert.Equal(WorkspaceKind.Attention, vm.Workspace);
		Assert.Equal(vm.Page.Waiting[0].Key, vm.Inbox.SelectedRow?.Key);

		// The rail's Neurons button returns to the page you were on, and Back leaves it for the list.
		vm.ShowNeuronsCommand.Execute(null);
		Assert.Equal(WorkspaceKind.Neuron, vm.Workspace);
		vm.BackToNeuronsCommand.Execute(null);
		Assert.Equal(WorkspaceKind.Fleet, vm.Workspace);
		Assert.Equal("gbg.lab.tagroot.io", vm.Fleet.SelectedRow?.BareJid);

		// List and map keep the same Neuron picked.
		vm.ShowTopologyCommand.Execute(null);
		Assert.Equal("gbg.lab.tagroot.io", vm.Topology.NeuronAt(vm.Topology.SelectedId!));
		vm.Topology.SelectNeuron("sto.lab.tagroot.io");
		vm.ShowFleetCommand.Execute(null);
		Assert.Equal("sto.lab.tagroot.io", vm.Fleet.SelectedRow?.BareJid);

		// From the map: Open leads to the page.
		vm.ShowTopologyCommand.Execute(null);
		vm.Topology.OpenPageCommand.Execute(null);
		Assert.Equal(WorkspaceKind.Neuron, vm.Workspace);
		Assert.Equal("sto.lab.tagroot.io", vm.Page!.BareJid);
		vm.BackToNeuronsCommand.Execute(null);
		Assert.Equal(WorkspaceKind.Topology, vm.Workspace);
		window.Close();
	}

	[AvaloniaTheory]
	[InlineData(1100, 720)]
	[InlineData(900, 700)]
	[InlineData(780, 640)]
	public async Task TheNeuronPageFitsSmallWindows(int width, int height)
	{
		Sample s = await SampleAsync();
		s.Vm.OpenNeuron("gbg.lab.tagroot.io");
		MainWindow window = Window(s, "Light", width, height);
		Capture(window, $"neuron-overview-light-{width}");

		// Nothing in the top bar is pushed out of the window.
		Avalonia.Controls.TextBox search = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBox>(window, "SearchBox")!;
		Point right = search.TranslatePoint(new Point(search.Bounds.Width, 0), window)!.Value;
		Assert.True(right.X <= width, $"The search box ends at {right.X} in a window {width} wide.");

		s.Vm.ShowPageTabCommand.Execute(WorkspaceKind.NeuronEvents);
		Capture(window, $"neuron-events-light-{width}");
		window.Close();
	}

	[AvaloniaFact]
	public async Task AnOfflineNeuronStillHasAPage()
	{
		Sample s = await SampleAsync();
		s.Vm.OpenNeuron("backup.prod.example.com");
		MainWindow window = Window(s, "Dark");
		NeuronPageViewModel page = s.Vm.Page!;
		Assert.False(page.IsLive);
		Assert.True(page.IsBad);
		Capture(window, "neuron-overview-offline-dark-1440");

		// Its sources cannot be read while it is away, and the tab says so instead of showing another Neuron's.
		s.Vm.ShowPageTabCommand.Execute(WorkspaceKind.Sources);
		Dispatcher.UIThread.RunJobs();
		Assert.Null(s.Vm.Sources.SelectedNeuron);
		Assert.True(s.Vm.Sources.HasPinnedNote);
		Capture(window, "neuron-sources-offline-dark-1440");

		// A Neuron that is not among yours opens nothing, and says so.
		List<string> said = [];
		Ui.Said += said.Add;
		s.Vm.OpenNeuron("nowhere.example.org");
		Assert.Equal("backup.prod.example.com", s.Vm.Page!.BareJid);
		Assert.Contains(said, x => x.Contains("nowhere.example.org", StringComparison.Ordinal));
		window.Close();
	}

	// ----- The account's page -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task AnAccountHasOnePageWithItsDevicesIdentitiesAndStorage(string theme)
	{
		Sample s = await SampleAsync();
		MainWindowViewModel vm = s.Vm;
		string t = theme.ToLowerInvariant();
		s.Devices.Preview(new DeviceQuestion
		{
			Id = "q1", AccountId = s.Ops, Kind = DeviceQuestionKind.Readout, DeviceJid = "thermo-12@lab.tagroot.io", RemoteJid = "dashboard@partner.example",
			Names = ["Temperature", "Humidity"], Received = DateTime.Now.AddMinutes(-15),
		});

		vm.ShowAccountsCommand.Execute(null);
		vm.SelectedAccount = vm.Accounts.First(a => a.Session.Profile.Id == s.Ops);
		MainWindow window = Window(s, theme);
		Assert.Equal(WorkspaceKind.Accounts, vm.Workspace);
		Assert.Equal(PlaceKind.Accounts, vm.Place);

		// Connection: the sign-in, with Change password.
		AccountItemViewModel account = vm.SelectedAccount!;
		account.OpenChangePasswordCommand.Execute(null);
		Assert.True(account.IsChangingPassword);
		Assert.False(account.ChangePasswordCommand.CanExecute(null));
		account.ChangedPassword = "correct horse";
		account.ChangedPasswordAgain = "correct horse battery";
		await account.ChangePasswordCommand.ExecuteAsync(null);
		Assert.Equal("The two passwords are not the same.", account.PasswordError);
		account.ChangedPasswordAgain = "correct horse";
		await account.ChangePasswordCommand.ExecuteAsync(null);
		Assert.StartsWith("Connect the account first", account.PasswordError);
		Assert.True(account.IsChangingPassword);
		Capture(window, $"account-connection-{t}-1440");
		account.CancelChangePasswordCommand.Execute(null);
		Assert.False(account.IsChangingPassword);
		Assert.Equal(string.Empty, account.ChangedPassword);

		// Devices: this account's only, and the tab stays when you pick another account.
		vm.ShowAccountTabCommand.Execute(WorkspaceKind.Devices);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(WorkspaceKind.Devices, vm.Workspace);
		Assert.Equal(PlaceKind.Accounts, vm.Place);
		Assert.True(vm.Devices.IsOneAccount);
		Assert.Equal(s.Ops, vm.Devices.OnlyAccount);
		Capture(window, $"account-devices-{t}-1440");
		vm.SelectedAccount = vm.Accounts.First(a => a.Session.Profile.Id == s.Admin);
		Assert.Equal(WorkspaceKind.Devices, vm.Workspace);
		Assert.Equal(s.Admin, vm.Devices.OnlyAccount);

		// Identities and Storage follow the account the same way.
		vm.ShowAccountTabCommand.Execute(WorkspaceKind.AccountIdentities);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.IsAccountIdentities);
		Assert.True(vm.Identities.IsOneAccount);
		Capture(window, $"account-identities-{t}-1440");
		vm.ShowAccountTabCommand.Execute(WorkspaceKind.AccountStorage);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.IsAccountStorage);
		Assert.True(vm.PubSub.IsPinned);
		Assert.Equal("Storage", vm.PubSub.Title);
		Capture(window, $"account-storage-{t}-1440");

		// The old ways in still lead somewhere sensible.
		vm.ShowDevicesCommand.Execute(null);
		Assert.Equal(WorkspaceKind.Devices, vm.Workspace);
		vm.ShowIdentitiesCommand.Execute(null);
		Assert.Equal(WorkspaceKind.AccountIdentities, vm.Workspace);
		window.Close();
	}

	// ----- Chat: people and rooms -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task ChatKeepsPeopleAndRoomsApartAndNeuronsOut(string theme)
	{
		Sample s = await SampleAsync();
		MainWindowViewModel vm = s.Vm;
		ChatViewModel chat = vm.Chat;
		string t = theme.ToLowerInvariant();

		s.Chat.Preview(new Contact(s.Ops, "kim@lab.tagroot.io", "Kim Lindqvist", [], SubscriptionState.Both, true, Availability.Online, null, false));
		s.Chat.Preview(new Contact(s.Ops, "robin@lab.tagroot.io", "Robin Ek", [], SubscriptionState.Both, true, Availability.Away, "In a meeting", false));
		s.Chat.Preview(new Contact(s.Ops, "sam@partner.example", "Sam Öberg", [], SubscriptionState.Both, false, Availability.Offline, null, false));
		s.Chat.Preview(new Contact(s.Ops, "lab.tagroot.io", string.Empty, [], SubscriptionState.Both, true, Availability.Online, null, true));
		s.Chat.Preview(new Contact(s.Ops, "gbg.lab.tagroot.io", string.Empty, [], SubscriptionState.Both, true, Availability.Online, null, true));
		s.Chat.PreviewRoom(s.Ops, "ops-team@muc.lab.tagroot.io", "ops", "kim", "robin", "sam");
		s.Chat.PreviewRoom(s.Admin, "incident-4471@muc.prod.example.com", "admin", "robin");
		s.Chat.Preview(s.Ops, "kim@lab.tagroot.io", "Morning! Did the backup on gbg finish?", "Kim Lindqvist");
		s.Chat.Preview(s.Ops, "kim@lab.tagroot.io", "I see the 02:00 one failed and nothing after it.", "Kim Lindqvist");
		s.Chat.Preview(s.Ops, "ops-team@muc.lab.tagroot.io", "Deploy window is 14:00 to 15:00 today.", "robin", room: true);
		s.Chat.Preview(s.Ops, "ops-team@muc.lab.tagroot.io", "@ops can you watch gbg while it restarts?", "kim", room: true, mention: true);
		s.Chat.Preview(s.Ops, "ops-team@muc.lab.tagroot.io", "I will take sto.", "sam", room: true);

		vm.ShowChatCommand.Execute(null);
		chat.Refresh();
		MainWindow window = Window(s, theme);

		// People: the people, and no rooms and no quiet Neurons.
		Assert.True(chat.ShowPeople);
		Assert.Equal(["Kim Lindqvist", "Robin Ek", "Sam Öberg"], chat.People.Select(p => p.Label).Order());
		Assert.Equal(2, chat.PeopleUnread);
		Assert.Equal(3, chat.RoomsUnread);
		Capture(window, $"chat-people-{t}-1440");

		// Rooms: the other half, with its own way to add.
		chat.ShowRoomsTabCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		Assert.True(chat.ShowRooms);
		Assert.Equal(2, chat.People.Count);
		Assert.All(chat.People, p => Assert.True(p.IsRoom));
		Assert.Equal("Filter rooms…", chat.FilterHint);
		chat.SelectedPerson = chat.People.First(p => p.BareJid.StartsWith("ops-team", StringComparison.Ordinal));
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"chat-rooms-{t}-1440");
		chat.Refresh();
		Assert.Equal(0, chat.RoomsUnread);

		// Each half remembers the conversation you had open in it.
		chat.ShowPeopleTabCommand.Execute(null);
		Assert.Null(chat.SelectedPerson);
		chat.SelectedPerson = chat.People.First(p => p.Label == "Kim Lindqvist");
		chat.ShowRoomsTabCommand.Execute(null);
		Assert.StartsWith("ops-team", chat.SelectedPerson?.BareJid);
		chat.ShowPeopleTabCommand.Execute(null);
		Assert.Equal("Kim Lindqvist", chat.SelectedPerson?.Label);

		// A Neuron that writes shows up among people while its message is unread.
		s.Chat.Preview(s.Ops, "gbg.lab.tagroot.io", "Backup of database failed: disk full.");
		chat.Refresh();
		Assert.Contains(chat.People, p => p.BareJid == "gbg.lab.tagroot.io" && p.IsNeuron && p.Unread == 1);
		Assert.DoesNotContain(chat.People, p => p.BareJid == "lab.tagroot.io");

		// Typing a Neuron's name finds it, quiet or not.
		chat.FilterText = "lab.tagroot";
		chat.Refresh();
		Assert.Contains(chat.People, p => p.BareJid == "lab.tagroot.io");
		chat.FilterText = "zzz";
		chat.Refresh();
		Assert.Equal("Nothing matches", chat.EmptyTitle);
		chat.FilterText = string.Empty;
		chat.Refresh();

		// Opening a room from somewhere else (a pop-up, the Inbox) goes to the Rooms half.
		Assert.True(chat.Open(s.Admin, "incident-4471@muc.prod.example.com"));
		Assert.True(chat.ShowRooms);
		Assert.StartsWith("incident-4471", chat.SelectedPerson?.BareJid);
		Assert.True(chat.Open(s.Ops, "robin@lab.tagroot.io"));
		Assert.True(chat.ShowPeople);
		Assert.Equal("Robin Ek", chat.SelectedPerson?.Label);

		// A quiet Neuron can still be opened on purpose, and then it is in the list.
		Assert.True(chat.Open(s.Ops, "lab.tagroot.io"));
		Assert.Contains(chat.People, p => p.BareJid == "lab.tagroot.io");
		window.Close();
	}

	// ----- Watch: live sensor values -----

	[Fact]
	public void WatchedReadingsChangeInPlace()
	{
		List<ReadoutRow> shown =
		[
			new("Humidity", "41 %", "Momentary", "2026-11-11 10:00:00", false),
			new("Temperature", "21.4 °C", "Momentary", "2026-11-11 10:00:00", false),
			new("Uptime", "3 d", "Status", "2026-11-11 10:00:00", false),
		];
		ReadoutRow humidity = shown[0];

		SourcesViewModel.Merge(shown,
		[
			new("Humidity", "41 %", "Momentary", "2026-11-11 10:00:00", false),
			new("Temperature", "21.6 °C", "Momentary", "2026-11-11 10:00:05", false),
			new("Uptime", "3 d", "Status", "2026-11-11 10:00:05", false),
		]);

		// The same reading stays the same row; a new value is marked; a new time alone is not a change.
		Assert.Same(humidity, shown[0]);
		Assert.Equal("21.6 °C", shown[1].Value);
		Assert.True(shown[1].IsChanged);
		Assert.False(shown[2].IsChanged);
		Assert.Equal("2026-11-11 10:00:05", shown[2].Time);

		// The next reading with nothing new takes the mark away again, and rows that are gone leave.
		SourcesViewModel.Merge(shown,
		[
			new("Humidity", "41 %", "Momentary", "2026-11-11 10:00:00", false),
			new("Temperature", "21.6 °C", "Momentary", "2026-11-11 10:00:10", false),
		]);
		Assert.Equal(2, shown.Count);
		Assert.False(shown[1].IsChanged);
	}
}
