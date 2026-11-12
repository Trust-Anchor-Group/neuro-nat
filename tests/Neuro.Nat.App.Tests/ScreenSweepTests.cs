using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.Features;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Devices;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.PubSub;
using Neuro.Nat.Engine.Runbooks;
using Neuro.Nat.Engine.Sniffing;
using Neuro.Nat.Engine.Software;
using Neuro.Nat.Engine.Storage;
using Neuro.Nat.Engine.Topology;
using Waher.Networking.XMPP;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The screen sweep: every place and tab, new and old, at 760, 1440 and 2400 wide in both looks,
/// with several accounts and with one, written to .impeccable/review/sweep-*.png. No label, button,
/// column name, tab, count or Neuron name may be cut off; only free text (messages, event texts,
/// notes, paths) may end in an ellipsis.
/// </summary>
public class ScreenSweepTests
{
	private const string Service = "pubsub.lab.tagroot.io";
	private const string Me = "ops@lab.tagroot.io";
	private const string Lab = "lab.tagroot.io";
	private const string Gbg = "gbg.lab.tagroot.io";
	private const string Sto = "sto.lab.tagroot.io";
	private const string EdgeGbg = "edge-gbg.example.com";
	private const string Telemetry = "telemetry.lab.tagroot.io";
	private const string Prod = "prod.example.com";
	private const string Backup = "backup.prod.example.com";
	private const string OpsRoom = "ops-team@muc.lab.tagroot.io";

	private sealed record Sweep(MainWindowViewModel Vm, EventHub Hub, Guid Ops, Guid Admin, bool Several, List<string> Said) : IDisposable
	{
		public void Dispose() => Ui.Said -= this.Said.Add;
	}

	private static void Step() => Dispatcher.UIThread.RunJobs();

	private static async Task Settle(Func<bool>? busy = null)
	{
		Step();
		for (int i = 0; i < 40 && (busy?.Invoke() ?? false); i++)
		{
			await Task.Delay(10);
			Step();
		}

		await Task.Delay(20);
		Step();
	}

	// ----- Sample data -----

	/// <summary>
	/// Synthetic sample data: a lab with edge Neurons (some with a roster name, some known only by
	/// their domain), production with a backup that is offline, people, a room, nodes, notifications,
	/// problems, a runbook and a stand-in app telemetry feature. With <paramref name="several"/> false,
	/// everything comes through one account.
	/// </summary>
	private static async Task<Sweep> SampleAsync(bool several)
	{
		EngineRuntime.Initialize();
		PubSubViewModel.FadeTime = TimeSpan.Zero;
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Sweep." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = Lab, UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		Guid o = ops.Profile.Id;
		Guid a = o;
		if (several)
		{
			AccountSession admin = await manager.AddAsync(new AccountProfile { Host = Prod, UserName = "admin", ConnectOnStartup = false, ColorIndex = 3 }, AccountSecret.FromPassword("x"), connect: false);
			a = admin.Profile.Id;
		}

		EventHub hub = new();
		MonitorService monitors = new(manager, hub);
		FleetService fleet = new(monitors);
		ChatService chat = new(manager, monitors);
		DevicesService devices = new(manager);
		DateTime now = DateTime.Now;

		void Neuron(Guid account, string jid, string? name, double cpu, WatchState state = WatchState.Live, int earlier = 180)
		{
			monitors.For(account)!.Preview(new WatchedNeuron { AccountId = account, BareJid = jid, Name = name, State = state, HistoryState = HistoryState.Loaded, HistoryCount = earlier });
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
		Neuron(o, EdgeGbg, null, 48);
		Neuron(o, Telemetry, null, 9, earlier: 1240);
		Neuron(a, Prod, "Production", 37);
		Neuron(a, Backup, null, 0, WatchState.Offline, 0);

		for (int i = 0; i < 9; i++)
			Event(i * 50 + 15, SnifferDirection.Error, o, Gbg, $"SocketException: Connection reset by peer (lab.tagroot.io:5269)\n\nModule: Waher.Networking.XMPP.Server\nEventId: S2S\nRemoteEndPoint: 10.0.0.{i + 3}:5269");
		for (int i = 0; i < 6; i++)
			Event(i * 70 + 30, SnifferDirection.Error, o, Lab, $"TimeoutException in ConcentratorServer.ReadSensorData (node Meter-{i + 11})\n\nModule: Waher.Things\nObject: Meter-{i + 11}");
		for (int i = 0; i < 4; i++)
			Event(i * 90 + 45, SnifferDirection.Warning, o, EdgeGbg, $"Endpoint 203.0.113.{40 + i} blocked after 5 failed logins\n\nModule: Waher.Security.LoginMonitor\nEventId: Blocked");
		for (int i = 0; i < 3; i++)
			Event(i * 300 + 60, SnifferDirection.Warning, a, Prod, "Certificate for prod.example.com expires in 12 days\n\nModule: Waher.IoTGateway\nEventId: Certificate");
		for (int i = 0; i < 12; i++)
			Event(i * 40 + 5, SnifferDirection.Information, o, i % 2 == 0 ? Lab : EdgeGbg, "12 clients connected, 3 disconnected in the last minute\n\nModule: Waher.Networking.XMPP.Server");

		monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n1", AccountId = o, NeuronJid = Gbg, Received = now.AddMinutes(-3),
			Markdown = "Legal identity application received: [`2f9a1c04-57e1-4c1e@legal.gbg.lab.tagroot.io`](https://gbg.lab.tagroot.io/ValidateLegalId.md?ID=2f9a1c04-57e1-4c1e%40legal.gbg.lab.tagroot.io)\n",
		});
		monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n2", AccountId = o, NeuronJid = EdgeGbg, Received = now.AddMinutes(-34),
			Markdown = "Account requested.\n\n| Account request ||\n|:------|:-------|\n| Name: | Kim Lindqvist |\n| Company: | Example Logistics AB |\n",
		});
		monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n3", AccountId = a, NeuronJid = Prod, Received = now.AddMinutes(-80),
			Markdown = "Legal identity application received: [`77b0e2aa-1d3c-4f0b@legal.prod.example.com`](https://prod.example.com/ValidateLegalId.md?ID=77b0e2aa-1d3c-4f0b%40legal.prod.example.com)\n",
		});

		// People and a room.
		chat.Preview(new Contact(o, "kim@lab.tagroot.io", "Kim Lindqvist", ["On call"], SubscriptionState.Both, true, Availability.Online, null, false));
		chat.Preview(new Contact(o, "robin@gbg.lab.tagroot.io", "Robin Ek", ["On call"], SubscriptionState.Both, true, Availability.Away, "In a meeting until three", false));
		chat.Preview(new Contact(a, "lead@prod.example.com", "Lee Park", [], SubscriptionState.Both, true, Availability.Online, null, false));
		chat.PreviewRoom(o, OpsRoom, "oncall");
		chat.Preview(o, OpsRoom, new RoomOccupant("oncall", "Moderator", "Owner", Me + "/aa"));
		chat.Preview(o, OpsRoom, new RoomOccupant("robin", "Participant", "Member", "robin@gbg.lab.tagroot.io/x"));
		chat.Preview(o, OpsRoom, "Deploy window is 14:00 to 15:00 today.", "robin", room: true);
		chat.Preview(o, OpsRoom, "Gothenburg edge restarts first, then Stockholm.", "oncall", room: true);
		chat.Preview(o, "kim@lab.tagroot.io", "Morning! Did the backup on gbg finish?", "Kim Lindqvist");
		chat.Preview(o, "kim@lab.tagroot.io", "It did, 42 GB in 18 minutes. The certificate on prod is next.", "ops");
		chat.Preview(o, "robin@gbg.lab.tagroot.io", "gbg is slow again, can you look?", "Robin Ek");
		chat.Preview(a, "lead@prod.example.com", "The certificate on prod runs out soon.", "Lee Park");

		// Nodes on the lab's publish/subscribe service.
		PubSubService pubsub = new(manager);
		PreviewPubSub nodes = new PreviewPubSub(pubsub, Service).Account(o, Me);
		if (several)
			nodes.Account(a, "admin@prod.example.com");
		pubsub.StandIn = nodes;
		nodes.Add("alerts", Me, "Alerts from the lab")
			.Add("announcements", "lead@lab.tagroot.io", "Announcements")
			.Add("build-log", Me, "Build log");
		nodes.Item(Service, "alerts", "a-1", "<alert xmlns='urn:example:alerts' level='warning'><text>Disk on gbg is 91 % full</text></alert>", "robin@gbg.lab.tagroot.io")
			.Person(Service, "alerts", "robin@gbg.lab.tagroot.io", NodeRole.Publisher)
			.Subscriber(Service, "alerts", "kim@lab.tagroot.io", SubscriberState.Subscribed);

		SoftwareService software = new(monitors, chat)
		{
			CheckStandIn = (_, jid) => jid == Sto ? throw new TimeoutException("The Neuron did not answer.") : ["Checking for new packages.", "Done."],
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
		Ui.SetClipboard = _ => Task.CompletedTask;

		AppTelemetryStandIn feature = new() { Account = o };
		MainWindowViewModel vm = new(manager, fleet, chat, null, devices, pubsub, software, features: new FeatureSet([feature]));
		vm.Compare.Reader = new CompareTests.StandIn();
		await vm.StartAsync();
		feature.Raise();
		vm.Fleet.Refresh();
		vm.Events.Flush();
		vm.Attention.Refresh();
		vm.Chat.Refresh();
		vm.Inbox.Refresh();

		// Runbooks in the Console: the ready-made ones and one of the team's own.
		InMemoryRunbookStore store = new();
		await store.SaveAsync(new Runbook { Title = "Restart and check", Steps = RunbookFile.ReadSteps("Versions\nNrClients\nReboot\nwait until back\nNrClients") }.WithMarks());
		await vm.Console.Runbooks.UseAsync(store);
		Step();
		return new Sweep(vm, hub, o, a, several, said);
	}

	/// <summary>A stand-in for app telemetry: a tab on the telemetry Neuron, a place in the rail, an Inbox item and a big screen page.</summary>
	private sealed class AppTelemetryStandIn : INatFeature
	{
		public Guid Account { get; set; }

		public string Key => "sweep.telemetry";

		public event EventHandler<FeatureNeuron?>? Changed;

		public void Start(IFeatureHost host)
		{
		}

		public void Raise() => this.Changed?.Invoke(this, null);

		public FeaturePresence PresenceOn(Guid accountId, string neuronJid) => neuronJid == Telemetry ? FeaturePresence.Present : FeaturePresence.Absent;

		public IReadOnlyList<string> SoftwareLines(Guid accountId, string neuronJid) => neuronJid == Telemetry ? ["App telemetry 1.4.0"] : [];

		public IReadOnlyList<FeatureTab> NeuronTabs(Guid accountId, string neuronJid)
			=> neuronJid == Telemetry ? [new FeatureTab("sweep.tab", "App telemetry", Numbers)] : [];

		public IReadOnlyList<FeaturePlace> Places() => [new FeaturePlace("sweep.place", "App telemetry", "Chart", Numbers)];

		public IReadOnlyList<FeatureInboxItem> InboxItems() => [new FeatureInboxItem("nfc", this.Account, Telemetry, "App telemetry", "Fewer NFC reads than usual", DateTime.Now.AddMinutes(-12))];

		public IReadOnlyList<FeatureScreenPage> BigScreenPages() => [new FeatureScreenPage("sweep.screen", "App numbers", Numbers)];

		public IReadOnlyList<IDataTemplate> Templates() => [];

		private static StackPanel Numbers()
		{
			StackPanel all = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 40, Margin = new Thickness(20) };
			foreach ((string n, string what) in new[] { ("1,284", "People signed in"), ("96", "New today"), ("3", "Crashes") })
			{
				StackPanel one = new();
				one.Children.Add(new TextBlock { Text = n, FontSize = 20, FontWeight = FontWeight.SemiBold });
				one.Children.Add(new TextBlock { Text = what, Classes = { "meta" } });
				all.Children.Add(one);
			}

			return all;
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	// ----- The screens -----

	/// <summary>Every place and tab, by a short name, and how to get there.</summary>
	private static IEnumerable<(string Name, Func<Sweep, Task> Go)> Screens(bool several)
	{
		yield return ("inbox", async s =>
		{
			s.Vm.ShowAttentionCommand.Execute(null);
			s.Vm.Inbox.Tab = InboxTab.Everything;
			await Settle();
			s.Vm.Inbox.SelectedRow = s.Vm.Inbox.Rows.FirstOrDefault(r => r.IsNotification);
		});
		yield return ("inbox-messages", async s =>
		{
			s.Vm.Inbox.Tab = InboxTab.Messages;
			await Settle();
			s.Vm.Inbox.SelectedRow = s.Vm.Inbox.Rows.FirstOrDefault();
		});
		yield return ("inbox-requests", s =>
		{
			s.Vm.Inbox.Tab = InboxTab.Requests;
			return Task.CompletedTask;
		});
		yield return ("inbox-neurons", s =>
		{
			s.Vm.Inbox.Tab = InboxTab.Neurons;
			return Task.CompletedTask;
		});
		yield return ("neurons", s =>
		{
			s.Vm.Inbox.Tab = InboxTab.Everything;
			s.Vm.ShowFleetCommand.Execute(null);
			return Task.CompletedTask;
		});
		yield return ("map", async s =>
		{
			s.Vm.ShowTopologyCommand.Execute(null);
			await Settle(() => s.Vm.Topology.IsScanning);
			s.Vm.Topology.Load(Map(s), includesGroups: false);
			s.Vm.Topology.SelectedId = Gbg;
		});
		yield return ("neuron-overview", s =>
		{
			s.Vm.OpenNeuron(Lab, s.Ops);
			return Task.CompletedTask;
		});
		yield return ("neuron-events", s => Tab(s, WorkspaceKind.NeuronEvents));
		yield return ("neuron-sources", s => Tab(s, WorkspaceKind.Sources));
		yield return ("neuron-console", s => Tab(s, WorkspaceKind.NeuronConsole));
		yield return ("neuron-software", s => Tab(s, WorkspaceKind.Updates));
		yield return ("neuron-identity", s => Tab(s, WorkspaceKind.Identities));
		yield return ("neuron-storage", async s =>
		{
			s.Vm.ShowPageTabCommand.Execute(WorkspaceKind.PubSub);
			await Settle(() => s.Vm.PubSub.IsBusy || s.Vm.PubSub.IsItemsLoading || s.Vm.PubSub.IsSettingsLoading || s.Vm.PubSub.IsPeopleLoading);
		});
		yield return ("neuron-domain-overview", s =>
		{
			s.Vm.OpenNeuron(EdgeGbg, s.Ops);
			return Task.CompletedTask;
		});
		yield return ("neuron-telemetry", s =>
		{
			s.Vm.OpenNeuron(Telemetry, s.Ops);
			s.Vm.ShowFeatureTabCommand.Execute("sweep.tab");
			return Task.CompletedTask;
		});
		yield return ("events", s =>
		{
			s.Vm.ShowEventsCommand.Execute(null);
			s.Vm.Events.SelectedIssue = null;
			return Task.CompletedTask;
		});
		yield return ("events-problem", s =>
		{
			s.Vm.Events.SelectedIssue = s.Vm.Events.Issues[0];
			return Task.CompletedTask;
		});
		yield return ("chat-person", async s =>
		{
			s.Vm.Events.SelectedIssue = null;
			s.Vm.ShowChat(s.Ops, "kim@lab.tagroot.io");
			await Settle();
			if (!s.Vm.Chat.IsPanelOpen)
				s.Vm.Chat.TogglePanelCommand.Execute(null);
		});
		yield return ("chat-room", async s =>
		{
			s.Vm.ShowChat(s.Ops, OpsRoom);
			await Settle();
			if (!s.Vm.Chat.IsPanelOpen)
				s.Vm.Chat.TogglePanelCommand.Execute(null);
		});
		yield return ("console", s =>
		{
			s.Vm.Workspace = WorkspaceKind.Console;
			s.Vm.Console.Runbooks.Selected = null;
			return Task.CompletedTask;
		});
		yield return ("console-runbook", s =>
		{
			s.Vm.Console.Runbooks.Selected = s.Vm.Console.Runbooks.Items.First(i => i.Title == "Restart and check");
			return Task.CompletedTask;
		});
		yield return ("compare", async s =>
		{
			s.Vm.Console.Runbooks.Selected = null;
			s.Vm.CompareNeurons((s.Ops, Lab), several ? (s.Admin, Prod) : (s.Ops, EdgeGbg));
			await Settle(() => s.Vm.Compare.IsBusy);
		});
		yield return ("accounts", s =>
		{
			s.Vm.Workspace = WorkspaceKind.Accounts;
			s.Vm.SelectedAccount = s.Vm.Accounts[0];
			return Task.CompletedTask;
		});
		yield return ("account-devices", s =>
		{
			s.Vm.Workspace = WorkspaceKind.Devices;
			return Task.CompletedTask;
		});
		yield return ("account-identities", s =>
		{
			s.Vm.Workspace = WorkspaceKind.AccountIdentities;
			return Task.CompletedTask;
		});
		yield return ("account-storage", async s =>
		{
			s.Vm.Workspace = WorkspaceKind.AccountStorage;
			await Settle(() => s.Vm.PubSub.IsBusy || s.Vm.PubSub.IsItemsLoading);
		});
		yield return ("app-telemetry", s =>
		{
			s.Vm.ShowFeaturePlaceCommand.Execute("sweep.place");
			return Task.CompletedTask;
		});
		yield return ("settings", s =>
		{
			s.Vm.ShowSettingsCommand.Execute(null);
			return Task.CompletedTask;
		});
	}

	/// <summary>The network as the Neurons describe it: the lab with its edges under it, federated with production.</summary>
	private static TopologySnapshot Map(Sweep s)
	{
		List<TopologyService.NeuronAnswer> answers = [];
		void Answer(Guid account, string jid, string? name, string? parent, params string[] federation)
		{
			WatchedNeuron n = new() { AccountId = account, BareJid = jid, Name = name, State = WatchState.Live };
			string p = parent is null ? string.Empty : $"\"{parent}\" -> \"{jid}\" [dir=back];";
			string f = string.Concat(federation.Select(x => $"\"{jid}\" -> \"{x}\";"));
			answers.Add(new(n, jid, DotGraph.Edges(p), DotGraph.Edges(f), [], null));
		}

		Answer(s.Ops, Lab, "Lab", "tagroot.io", "tagroot.io", Prod);
		Answer(s.Ops, Gbg, "Gothenburg edge", Lab, Lab);
		Answer(s.Ops, Sto, "Stockholm edge", Lab, Lab, Gbg);
		Answer(s.Ops, EdgeGbg, null, Gbg, Gbg);
		Answer(s.Ops, Telemetry, null, Lab);
		Answer(s.Admin, Prod, "Production", null, Lab, Backup);
		return TopologyService.Merge(answers, DateTime.Now.AddMinutes(-2));
	}

	private static Task Tab(Sweep s, WorkspaceKind tab)
	{
		s.Vm.ShowPageTabCommand.Execute(tab);
		return Task.CompletedTask;
	}

	/// <summary>The screens where the account would show when there are several: checked again with one.</summary>
	private static readonly HashSet<string> OneAccountScreens = ["inbox", "inbox-messages", "inbox-neurons", "neurons", "map", "neuron-overview", "neuron-software", "neuron-identity", "events", "events-problem", "chat-person", "chat-room", "console-runbook", "compare", "accounts"];

	// ----- The sweep -----

	[AvaloniaTheory]
	[InlineData("Light", 760)]
	[InlineData("Dark", 760)]
	[InlineData("Light", 1440)]
	[InlineData("Dark", 1440)]
	[InlineData("Light", 2400)]
	[InlineData("Dark", 2400)]
	public async Task EveryScreenAtEverySizeWithNothingCutOff(string theme, int width)
	{
		int height = width <= 760 ? 720 : 900;
		string t = theme.ToLowerInvariant();
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		List<string> cut = [];

		foreach (bool several in new[] { true, false })
		{
			using Sweep s = await SampleAsync(several);
			MainWindow window = new() { DataContext = s.Vm, Width = width, Height = height };
			window.Show();
			Step();

			foreach ((string name, Func<Sweep, Task> go) in Screens(several))
			{
				await go(s);
				await Settle();
				if (!several && !OneAccountScreens.Contains(name))
					continue;

				string screen = several ? name : name + "-one-account";
				Save(window, $"sweep-{screen}-{t}-{width}");
				cut.AddRange(CutLabels(window, screen));
				if (!several && name != "accounts")
					cut.AddRange(AccountNamed(window, screen));
			}

			window.Close();

			if (several)
			{
				// The big screen: each page, scaled to the window.
				List<IBigScreenPage> added = [];
				s.Vm.Features.FeedBigScreen(added);
				BigScreenViewModel big = new(s.Vm.Fleet, s.Hub, s.Vm.Settings, added);
				big.Start();
				Window screen = new() { Width = width, Height = height, Content = new BigScreenView { DataContext = big } };
				screen.Show();
				Step();
				HashSet<string> seen = [];
				while (seen.Add(big.Current.Key))
				{
					string page = "bigscreen-" + big.Current.Key.Replace('.', '-');
					Save(screen, $"sweep-{page}-{t}-{width}");
					cut.AddRange(CutLabels(screen, page));
					big.Press(Key.Right);
					Step();
				}

				screen.Close();
				big.Stop();
			}
		}

		// The window that says what NAT saved cannot be opened. It has one size of its own.
		if (width == 1440)
		{
			DatabaseProblemWindow problem = new(new DatabaseProblemViewModel());
			problem.Show();
			Step();
			Save(problem, $"sweep-cannot-open-{t}");
			cut.AddRange(CutLabels(problem, "cannot-open"));
			problem.Close();
		}

		Assert.True(cut.Count == 0, "Cut off at " + width + " " + t + ":\n" + string.Join("\n", cut.Distinct()));
	}

	// ----- What may be cut off -----

	/// <summary>
	/// Free text that may end in an ellipsis when it does not fit: what a person or a Neuron wrote.
	/// Each place by the row it is in (its data) and which of the row's texts; the name of a Neuron
	/// or a person in the same row is never on the list.
	/// </summary>
	private static readonly (string Row, string What, Func<TextBlock, bool> Is)[] FreeText =
	[
		(nameof(NeuronEvent), "an event's message and module in the stream", t => !t.Classes.Contains("colNeuron")),
		(nameof(IssueItemViewModel), "a problem's message, and where and since when", t => t.Classes.Count == 0 || t.Classes.Contains("meta") || t.Name == "IssueTitle"),
		(nameof(InboxRowViewModel), "the last message or the request under a row in the Inbox", t => t.Classes.Contains("meta")),
		(nameof(PersonViewModel), "the last message under a person or a room", t => t.Classes.Contains("meta")),
		(nameof(BigScreenEventViewModel), "a problem's message among the latest on the big screen", t => !t.FontFamily.ToString().Contains("Mono", StringComparison.Ordinal)),
	];

	private static bool IsFreeText(TextBlock t)
		=> t.DataContext?.GetType().Name is string row && FreeText.Any(f => f.Row == row && f.Is(t));

	/// <summary>Every visible text that ends in an ellipsis and is not free text.</summary>
	private static IEnumerable<string> CutLabels(TopLevel window, string screen)
	{
		foreach (TextBlock t in window.GetVisualDescendants().OfType<TextBlock>())
		{
			if (!t.IsEffectivelyVisible || t.TextTrimming == TextTrimming.None || string.IsNullOrWhiteSpace(t.Text) || t.Bounds.Width <= 0)
				continue;
			if (!t.TextLayout.TextLines.Any(l => l.HasCollapsed))
				continue;
			if (IsFreeText(t))
				continue;

			string where = t.GetVisualAncestors().OfType<UserControl>().FirstOrDefault()?.GetType().Name ?? "window";
			string named = t.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => !string.IsNullOrEmpty(c.Name) && !c.Name.StartsWith("PART_", StringComparison.Ordinal))?.Name ?? "-";
			string row = t.DataContext?.GetType().Name ?? "-";
			yield return $"{screen}: \"{t.Text}\" [{string.Join(' ', t.Classes)}] in {where}/{named} ({row}), {t.Bounds.Width:F0} wide";
		}
	}

	/// <summary>With one account, no screen names it.</summary>
	private static IEnumerable<string> AccountNamed(TopLevel window, string screen)
	{
		string[] names = ["Through ops", "through ops", "via ops"];
		foreach (TextBlock t in window.GetVisualDescendants().OfType<TextBlock>())
		{
			if (t.IsEffectivelyVisible && t.Text is string text && (text == "ops" || names.Any(n => text.Contains(n, StringComparison.Ordinal))))
				yield return $"{screen}: the one account is named: \"{text}\"";
		}
	}

	private static void Save(TopLevel window, string name)
	{
		Step();
		using WriteableBitmap? frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		string folder = Path.Combine(RenameTests.RepositoryRoot(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		using FileStream png = File.Create(Path.Combine(folder, name + ".png"));
		frame!.Save(png, new PngBitmapEncoderOptions());
	}
}
