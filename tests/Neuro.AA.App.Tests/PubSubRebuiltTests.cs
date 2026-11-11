using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.AA.App.ViewModels;
using Neuro.AA.App.Views;
using Neuro.AA.Engine;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Chat;
using Neuro.AA.Engine.Devices;
using Neuro.AA.Engine.Events;
using Neuro.AA.Engine.PubSub;

namespace Neuro.AA.App.Tests;

/// <summary>
/// Publish/subscribe, rebuilt: nodes as a tree, items in words, watching, the editor, removing
/// with a way back, settings and people, and what is refused. Renders frames for review.
/// </summary>
public class PubSubRebuiltTests
{
	private const string Service = "pubsub.lab.tagroot.io";
	private const string Me = "ops@lab.tagroot.io";
	private const string Picture = "iVBORw0KGgoAAAANSUhEUgAAABgAAAAYCAIAAABvFaqvAAAAPklEQVR42mPQyDtBFcQwahBFBn39+gsTkWwQVlPwmMVAqim4zBo1aPgYRLV0RM2UPWgyLcFgxuXNUYPoaBAAXoZC5+m6Vd4AAAAASUVORK5CYII=";

	private sealed record Sample(MainWindowViewModel Vm, PubSubService PubSub, PreviewPubSub Nodes, Guid Ops, Guid Admin, List<string> Said, List<string> Copied) : IDisposable
	{
		public PubSubViewModel View => this.Vm.PubSub;

		public void Dispose() => Ui.Said -= this.Said.Add;

		public PubSubNodeRow Node(string name) => this.View.Nodes.Single(n => n.Node == name);

		public async Task OpenAsync(string name)
		{
			this.View.SelectedNode = this.Node(name);
			await Settle(this.View);
		}
	}

	private static async Task Settle(PubSubViewModel p)
	{
		for (int i = 0; i < 50 && (p.IsBusy || p.IsItemsLoading || p.IsSettingsLoading || p.IsPeopleLoading); i++)
			await Task.Delay(10);
		Dispatcher.UIThread.RunJobs();
	}

	private static void Capture(TopLevel window, string name)
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

	private static T Shown<T>(Visual root, string name)
		where T : Control
		=> root.GetVisualDescendants().OfType<T>().Single(v => v.Name == name && v.IsEffectivelyVisible);

	private static MainWindow Window(Sample s, string theme, int width = 1440, int height = 900)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		MainWindow window = new() { DataContext = s.Vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		return window;
	}

	private static string[] Texts(IReadOnlyList<ItemAction> actions) => [.. actions.Select(a => a.Text)];

	private static ItemAction Action(IReadOnlyList<ItemAction> actions, string text) => actions.Single(a => a.Text == text);

	private static string Alert(string level, string text) => $"<alert xmlns='urn:example:alerts' level='{level}'><text>{text}</text></alert>";

	private static string Reading(double temperature, int minute)
		=> "<resp xmlns='urn:ieee:iot:sd:1.0'><nd id='Meter-11'><ts v='2026-11-10T10:" + minute.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ":00Z'>"
			+ "<q n='Temperature' v='" + temperature.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "' u='°C' m='true'/>"
			+ "<q n='Humidity' v='41' u='%' m='true'/><b n='Door open' v='false'/><s n='Firmware' v='2.4.1'/></ts></nd></resp>";

	/// <summary>
	/// A Neuron with a publish/subscribe service: a collection with nodes this account owns, nodes
	/// somebody else owns, a long node, and the account's personal storage.
	/// </summary>
	private static async Task<Sample> SampleAsync()
	{
		EngineRuntime.Initialize();
		PubSubViewModel.FadeTime = TimeSpan.Zero;
		string dir = Path.Combine(Path.GetTempPath(), "NeuroAA.PubSub." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.tagroot.io", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		AccountSession admin = await manager.AddAsync(new AccountProfile { Host = "prod.example.com", UserName = "admin", ConnectOnStartup = false, ColorIndex = 3 }, AccountSecret.FromPassword("x"), connect: false);

		MonitorService monitors = new(manager, new EventHub());
		FleetService fleet = new(monitors);
		ChatService chat = new(manager, monitors);
		DevicesService devices = new(manager);
		Guid o = ops.Profile.Id;
		Guid a = admin.Profile.Id;

		monitors.For(o)!.Preview(new WatchedNeuron { AccountId = o, BareJid = "lab.tagroot.io", Name = "Lab", State = WatchState.Live, HistoryState = HistoryState.Loaded, HistoryCount = 12 });
		fleet.Preview(new NeuronHealth { AccountId = o, NeuronJid = "lab.tagroot.io", Software = "Waher.IoTGateway 3.11.1.0", ReadAt = DateTime.Now.AddSeconds(-20) });

		PubSubService pubsub = new(manager);
		PreviewPubSub nodes = new PreviewPubSub(pubsub, Service).Account(o, Me).Account(a, "admin@prod.example.com");
		pubsub.StandIn = nodes;

		nodes.Add("lab", Me, "Everything from the lab", collection: true)
			.Add("lab/alerts", Me, "Alerts from the lab", parent: "lab")
			.Add("lab/meter-11", Me, "Meter 11 in the plant room", parent: "lab")
			.Add("announcements", "lead@lab.tagroot.io", "Announcements")
			.Add("approvals", "lead@lab.tagroot.io", "Change approvals", settings: new NodeSettings { Access = NodeAccess.Authorize })
			.Add("build-log", Me, "Build log")
			.Add("closed-club", "lead@lab.tagroot.io", settings: new NodeSettings { Access = NodeAccess.Whitelist });

		nodes.Item(Service, "lab/alerts", "a-1", Alert("info", "Backup of the database completed"), "robin@lab.tagroot.io")
			.Item(Service, "lab/alerts", "a-2", Alert("warning", "Disk on gbg is 91 % full"), "robin@lab.tagroot.io")
			.Item(Service, "lab/alerts", "a-3", Alert("error", "sto lost its connection to lab"), Me)
			.Person(Service, "lab/alerts", "robin@lab.tagroot.io", NodeRole.Publisher)
			.Subscriber(Service, "lab/alerts", "kim@lab.tagroot.io", SubscriberState.Subscribed)
			.Subscriber(Service, "lab/alerts", "sam@partner.example", SubscriberState.Pending);

		nodes.Item(Service, "lab/meter-11", "r-1", Reading(21.5, 12), "meter-11@lab.tagroot.io")
			.Item(Service, "lab/meter-11", "r-2", Reading(21.9, 13), "meter-11@lab.tagroot.io");

		nodes.Item(Service, "announcements", "n-1", "<note xmlns='urn:example:notes'>Deploy window is 14:00 to 15:00 today.</note>", "lead@lab.tagroot.io");
		nodes.Item(Service, "closed-club", "c-1", "<secret xmlns='urn:example:notes'/>", "lead@lab.tagroot.io");

		for (int i = 1; i <= 120; i++)
			nodes.Item(Service, "build-log", "build-" + i, $"<build xmlns='urn:example:builds' number='{i}' result='{(i % 17 == 0 ? "failed" : "ok")}'/>", "ci@lab.tagroot.io");

		nodes.AddPersonal(o, "urn:xmpp:avatar:data").Item(Me, "urn:xmpp:avatar:data", "1f3a", $"<data xmlns='urn:xmpp:avatar:data'>{Picture}</data>", Me);
		nodes.AddPersonal(o, "urn:xmpp:avatar:metadata").Item(Me, "urn:xmpp:avatar:metadata", "1f3a", "<metadata xmlns='urn:xmpp:avatar:metadata'><info bytes='119' id='1f3a' type='image/png' width='24' height='24'/></metadata>", Me);
		nodes.AddPersonal(o, "http://jabber.org/protocol/nick").Item(Me, "http://jabber.org/protocol/nick", "current", "<nick xmlns='http://jabber.org/protocol/nick'>Ops on call</nick>", Me);
		nodes.AddPersonal(o, "http://jabber.org/protocol/mood").Item(Me, "http://jabber.org/protocol/mood", "current", "<mood xmlns='http://jabber.org/protocol/mood'><relieved/><text>The deploy went through</text></mood>", Me);
		nodes.AddPersonal(o, "http://jabber.org/protocol/tune").Item(Me, "http://jabber.org/protocol/tune", "current", "<tune xmlns='http://jabber.org/protocol/tune'><artist>Yes</artist><title>Heart of the Sunrise</title><source>Fragile</source><length>686</length></tune>", Me);
		nodes.AddPersonal(o, "http://jabber.org/protocol/geoloc").Item(Me, "http://jabber.org/protocol/geoloc", "current", "<geoloc xmlns='http://jabber.org/protocol/geoloc'><lat>59.3293</lat><lon>18.0686</lon><locality>Stockholm</locality><country>Sweden</country><accuracy>20</accuracy></geoloc>", Me);
		nodes.AddPersonal(o, "urn:xmpp:bookmarks:1").Item(Me, "urn:xmpp:bookmarks:1", "ops-team@muc.lab.tagroot.io", "<conference xmlns='urn:xmpp:bookmarks:1' name='Ops team' autojoin='true'><nick>ops</nick></conference>", Me);

		List<string> said = [];
		Ui.Said += said.Add;
		List<string> copied = [];
		Ui.SetClipboard = text =>
		{
			copied.Add(text);
			return Task.CompletedTask;
		};

		MainWindowViewModel vm = new(manager, fleet, chat, null, devices, pubsub);
		await vm.StartAsync();
		return new Sample(vm, pubsub, nodes, o, a, said, copied);
	}

	/// <summary>Opens the Nodes tab of the lab Neuron's page.</summary>
	private static async Task ShowNodesAsync(Sample s)
	{
		s.Vm.OpenNeuron("lab.tagroot.io", s.Ops, WorkspaceKind.PubSub);
		await Settle(s.View);
		Assert.True(s.Vm.IsPubSub);
		Assert.True(s.View.IsPinned);
		Assert.Equal("Nodes", s.View.Title);
	}

	/// <summary>Opens the lab collection, so the nodes inside it show.</summary>
	private static async Task OpenLabAsync(Sample s)
	{
		await s.View.ToggleExpandCommand.ExecuteAsync(s.Node("lab"));
		Dispatcher.UIThread.RunJobs();
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task NodesAreATreeAndANodeShowsItsItems(string theme)
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, theme);
		PubSubViewModel p = s.View;
		string t = theme.ToLowerInvariant();
		await ShowNodesAsync(s);

		// The top of the tree: collections are closed until opened.
		Assert.Equal(["announcements", "approvals", "build-log", "closed-club", "lab"], p.Nodes.Select(n => n.Node));
		Assert.True(s.Node("lab").IsCollection);
		Assert.Equal("Service " + Service, p.ServiceText);
		Assert.True(p.ShowsPicker);
		Capture(window, $"pubsub-tree-{t}-1440");

		await OpenLabAsync(s);
		Assert.Equal(["announcements", "approvals", "build-log", "closed-club", "lab", "lab/alerts", "lab/meter-11"], p.Nodes.Select(n => n.Node));
		Assert.Equal(1, s.Node("lab/alerts").Depth);

		// Finding a node keeps the collection it is in.
		p.NodeFilter = "meter";
		Assert.Equal(["lab", "lab/meter-11"], p.Nodes.Select(n => n.Node));
		p.NodeFilter = "plant room";
		Assert.Equal(["lab", "lab/meter-11"], p.Nodes.Select(n => n.Node));
		p.NodeFilter = "nothing like this";
		Assert.Empty(p.Nodes);
		Assert.Equal("No node matches.", p.NodesEmptyText);
		p.NodeFilter = string.Empty;

		// A node whose items the app has no words for: the XML, laid out for reading.
		await s.OpenAsync("lab/alerts");
		Assert.True(p.IsItems);
		Assert.Equal(["a-3", "a-2", "a-1"], p.Items.Select(i => i.Title));
		Assert.False(p.Items[0].IsReadable);
		Assert.True(p.Items[0].IsXmlOpen);
		Assert.Contains("<text>sto lost its connection to lab</text>", p.Items[0].ShownXml);
		Assert.Equal("by " + Me, p.Items[0].Meta);
		Assert.Equal("3 items", p.ItemsFooter);
		Assert.False(p.HasMore);
		Assert.Contains("Alerts from the lab", p.NodeMeta);
		Capture(window, $"pubsub-items-xml-{t}-1440");

		// Finding among the items looks at what they say.
		p.ItemFilter = "disk";
		Assert.Equal(["a-2"], p.Items.Select(i => i.Title));
		Assert.Equal("1 item of 3 match", p.ItemsFooter);
		p.ItemFilter = "zzz";
		Assert.Empty(p.Items);
		Assert.StartsWith("No item matches.", p.ItemsEmptyText);
		p.ItemFilter = string.Empty;

		// Sensor data reads as a small table, with the XML one click away.
		await s.OpenAsync("lab/meter-11");
		PubSubItemRow reading = p.Items[0];
		Assert.True(reading.IsReadable);
		Assert.Equal("Sensor data", reading.Kind);
		Assert.StartsWith("4 readings, taken ", reading.Summary);
		Assert.Equal(["Temperature", "Humidity", "Door open", "Firmware"], reading.Fields.Select(f => f.Label));
		Assert.Equal(["21.9 °C", "41 %", "No", "2.4.1"], reading.Fields.Select(f => f.Value));
		Assert.False(reading.IsXmlOpen);
		Capture(window, $"pubsub-items-sensor-{t}-1440");
		reading.ToggleXmlCommand.Execute(null);
		Assert.True(reading.IsXmlOpen);
		Assert.Equal("Hide XML", reading.XmlToggleText);
		Capture(window, $"pubsub-items-sensor-xml-{t}-1440");

		// A collection has no items: it opens on its settings.
		await s.OpenAsync("lab");
		Assert.True(p.IsSettings);
		Assert.False(p.CanPublishHere);
		Assert.Contains("A collection", p.NodeMeta);
	}

	[AvaloniaFact]
	public async Task WatchingShowsWhatIsPublishedAsItHappens()
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, "Light");
		PubSubViewModel p = s.View;
		await ShowNodesAsync(s);
		await OpenLabAsync(s);
		await s.OpenAsync("lab/alerts");
		Assert.False(p.IsWatching);
		Assert.False(p.HasWatched);

		// Not watched: what is published elsewhere does not show until asked for.
		s.Nodes.Arrive(Service, "lab/alerts", "a-4", Alert("info", "Nobody saw this arrive"), "robin@lab.tagroot.io");
		Assert.Equal(3, p.Items.Count);

		await p.ToggleWatchCommand.ExecuteAsync(null);
		Assert.True(p.IsWatching);
		Assert.True(s.Node("lab/alerts").IsWatched);
		Assert.Equal("Watching 1", p.WatchingText);
		Assert.Contains("Watching lab/alerts", s.Said);

		// Watched: it shows at the top, marked new.
		s.Nodes.Arrive(Service, "lab/alerts", "a-5", Alert("warning", "CPU on gbg is at 97 %"), "robin@lab.tagroot.io");
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("a-5", p.Items[0].Title);
		Assert.True(p.Items[0].IsNew);
		Assert.StartsWith("by robin@lab.tagroot.io · ", p.Items[0].Meta);
		Capture(window, "pubsub-watching-light-1440");

		// An item published again under its id takes the place of the old one.
		int before = p.Items.Count;
		s.Nodes.Arrive(Service, "lab/alerts", "a-5", Alert("info", "CPU on gbg is back to normal"), "robin@lab.tagroot.io");
		Assert.Equal(before, p.Items.Count);
		Assert.Contains("back to normal", p.Items[0].PrettyXml);

		// Removed elsewhere: it leaves the list.
		s.Nodes.Leave(Service, "lab/alerts", "a-1");
		Assert.DoesNotContain(p.Items, i => i.Title == "a-1");

		// With another node open, the watched one counts what came.
		await s.OpenAsync("build-log");
		s.Nodes.Arrive(Service, "lab/alerts", "a-6", Alert("info", "One more"), "robin@lab.tagroot.io");
		s.Nodes.Arrive(Service, "lab/alerts", "a-7", Alert("info", "And another"), "robin@lab.tagroot.io");
		Assert.Equal(2, s.Node("lab/alerts").NewCount);
		Assert.Equal("2", s.Node("lab/alerts").NewText);
		Capture(window, "pubsub-watching-count-light-1440");
		await s.OpenAsync("lab/alerts");
		Assert.Equal(0, s.Node("lab/alerts").NewCount);
		Assert.Equal("a-7", p.Items[0].Title);

		// Only what you watch.
		p.WatchingOnly = true;
		Assert.Equal(["lab", "lab/alerts"], p.Nodes.Select(n => n.Node));
		p.WatchingOnly = false;

		// A node whose owner has to say yes first.
		await s.OpenAsync("approvals");
		await p.ToggleWatchCommand.ExecuteAsync(null);
		Assert.False(p.IsWatching);
		Assert.Equal("Asked to watch. One of the node's owners has to say yes first.", p.WatchNote);

		// Stopping.
		await s.OpenAsync("lab/alerts");
		await p.ToggleWatchCommand.ExecuteAsync(null);
		Assert.False(p.IsWatching);
		Assert.False(p.HasWatched);
		Assert.Contains("unwatch lab/alerts", s.Nodes.Calls);
	}

	[AvaloniaFact]
	public async Task ItemsCanBeCopiedPublishedAgainAndRemovedWithAWayBack()
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, "Light");
		PubSubViewModel p = s.View;
		await ShowNodesAsync(s);
		await OpenLabAsync(s);
		await s.OpenAsync("lab/meter-11");

		// One item: copy what it says, its XML or its id; publish it again; remove it.
		PubSubItemRow row = p.Items[0];
		IReadOnlyList<ItemAction> one = p.ActionsFor([row]);
		Assert.Equal(["Copy what it says", "Copy XML", "Copy id", "Copy who published", "Chat with them", "Publish a copy…", "Edit and publish again…", "Remove"], Texts(one));
		await Action(one, "Copy what it says").RunAsync();
		Assert.Contains("Temperature: 21.9 °C", s.Copied[^1]);
		await Action(one, "Copy XML").RunAsync();
		Assert.StartsWith("<resp xmlns=\"urn:ieee:iot:sd:1.0\">", s.Copied[^1]);
		await Action(one, "Copy id").RunAsync();
		Assert.Equal("r-2", s.Copied[^1]);
		Assert.Equal("Copied the id", s.Said[^1]);

		// Publish again: the editor opens on the item, under its id.
		await Action(one, "Edit and publish again…").RunAsync();
		Assert.True(p.IsEditorOpen);
		Assert.Equal("Publish r-2 again", p.EditorTitle);
		Assert.Equal("r-2", p.PublishItemId);
		Assert.Equal(row.PrettyXml, p.PublishXml);
		Capture(window, "pubsub-editor-item-light-1440");
		p.PublishXml = p.PublishXml.Replace("21.9", "22.4", StringComparison.Ordinal);
		await p.PublishCommand.ExecuteAsync(null);
		Assert.False(p.IsEditorOpen);
		Assert.Equal(2, p.Items.Count);
		Assert.Equal("22.4 °C", p.Items[0].Fields[0].Value);
		Assert.True(p.Items[0].IsNew);
		Assert.Equal("Published r-2", s.Said[^1]);

		// A copy gets an id of its own.
		await Action(p.ActionsFor([p.Items[0]]), "Publish a copy…").RunAsync();
		Assert.Equal(string.Empty, p.PublishItemId);
		await p.PublishCommand.ExecuteAsync(null);
		Assert.Equal(3, p.Items.Count);

		// Removing one: it goes, and can be put back under the id it had.
		PubSubItemRow gone = p.Items.Single(i => i.Title == "r-1");
		string xml = gone.Item.PayloadXml;
		await Action(p.ActionsFor([gone]), "Remove").RunAsync();
		Assert.DoesNotContain(p.Items, i => i.Title == "r-1");
		Assert.Equal("Removed r-1.", p.UndoText);
		Assert.True(p.HasUndo);
		Capture(window, "pubsub-removed-undo-light-1440");
		await p.UndoCommand.ExecuteAsync(null);
		Assert.False(p.HasUndo);
		Assert.Equal(xml, p.Items.Single(i => i.Title == "r-1").Item.PayloadXml);
		Assert.Equal("Put r-1 back", s.Said[^1]);

		// Several at once.
		List<PubSubItemRow> two = [.. p.Items.Take(2)];
		IReadOnlyList<ItemAction> many = p.ActionsFor([.. two]);
		Assert.Equal(["Copy XML", "Copy ids", "Remove"], Texts(many));
		Assert.True(Action(many, "Remove").InBar);
		await Action(many, "Copy ids").RunAsync();
		Assert.Equal(string.Join(Environment.NewLine, two.Select(r => r.Title)), s.Copied[^1]);
		await Action(many, "Remove").RunAsync();
		Assert.Single(p.Items);
		Assert.Equal("Removed 2 items.", p.UndoText);
		await p.UndoCommand.ExecuteAsync(null);
		Assert.Equal(3, p.Items.Count);

		// The way back closes when another node is opened.
		await Action(p.ActionsFor([p.Items[0]]), "Remove").RunAsync();
		Assert.True(p.HasUndo);
		await s.OpenAsync("build-log");
		Assert.False(p.HasUndo);

		// Picking several rows shows the bar with the same actions.
		ListBox list = Shown<ListBox>(window, "ItemList");
		list.SelectedItems!.Add(p.Items[0]);
		list.SelectedItems.Add(p.Items[1]);
		list.SelectedItems.Add(p.Items[2]);
		Dispatcher.UIThread.RunJobs();
		Capture(window, "pubsub-items-picked-light-1440");
	}

	[AvaloniaFact]
	public async Task TheEditorPointsAtWhatIsWrong()
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, "Light");
		PubSubViewModel p = s.View;
		await ShowNodesAsync(s);
		await OpenLabAsync(s);
		await s.OpenAsync("lab/alerts");

		p.OpenEditorCommand.Execute(null);
		Assert.True(p.IsEditorOpen);
		Assert.Equal("Publish an item on lab/alerts", p.EditorTitle);
		Assert.False(p.CanPublish);
		Assert.False(p.HasEditorProblem);
		Assert.False(p.HasLast);

		// Wrong while typing: the line and the place are said, and nothing can be sent.
		p.PublishXml = "<alert xmlns='urn:example:alerts' level='info'>\n  <text>Disk is fine again\n</alert>";
		Assert.True(p.HasEditorProblem);
		Assert.False(p.CanPublish);
		Assert.StartsWith("Line 3, place 3: ", p.EditorProblem);
		Assert.Equal(3, p.ProblemLine);
		Assert.Equal(PubSubView.Offset(p.PublishXml, 3, 3), p.PublishXml.LastIndexOf("</alert>", StringComparison.Ordinal) + 2);
		Dispatcher.UIThread.RunJobs();
		Capture(window, "pubsub-editor-problem-light-1440");

		// Sending anyway says why not, and sends nothing.
		await p.PublishCommand.ExecuteAsync(null);
		Assert.StartsWith("This is not XML that can be published.", p.EditorError);
		Assert.DoesNotContain(s.Nodes.Calls, c => c.StartsWith("publish", StringComparison.Ordinal));

		// Right: it goes out, shows at the top, and the editor remembers it for next time.
		p.PublishXml = "<alert xmlns='urn:example:alerts' level='info'>\n  <text>Disk is fine again</text>\n</alert>";
		Assert.False(p.HasEditorProblem);
		Assert.True(p.CanPublish);
		p.PublishItemId = "a-9";
		await p.PublishCommand.ExecuteAsync(null);
		Assert.False(p.IsEditorOpen);
		Assert.Equal("a-9", p.Items[0].Title);
		Assert.True(p.HasLast);

		p.OpenEditorCommand.Execute(null);
		Assert.Equal(string.Empty, p.PublishXml);
		p.UseLastCommand.Execute(null);
		Assert.Contains("Disk is fine again", p.PublishXml);
		p.CloseEditorCommand.Execute(null);

		// A node you may not publish on: the editor stays open and says so.
		await s.OpenAsync("announcements");
		p.OpenEditorCommand.Execute(null);
		p.PublishXml = "<note xmlns='urn:example:notes'>Hello</note>";
		await p.PublishCommand.ExecuteAsync(null);
		Assert.True(p.IsEditorOpen);
		Assert.Equal("The item was not published. You are not allowed to. Ask one of the node's owners.", p.EditorError);
		Capture(window, "pubsub-editor-refused-light-1440");
	}

	[AvaloniaFact]
	public async Task OlderItemsComeAPageAtATime()
	{
		using Sample s = await SampleAsync();
		PubSubViewModel p = s.View;
		MainWindow window = Window(s, "Light");
		await ShowNodesAsync(s);
		await s.OpenAsync("build-log");

		// The latest 50 first: the rest is not even asked for.
		Assert.Equal(50, p.Items.Count);
		Assert.Equal("build-120", p.Items[0].Title);
		Assert.True(p.HasMore);
		Assert.Equal("The latest 50 items. There may be older ones.", p.ItemsFooter);
		Assert.Contains("items build-log 50", s.Nodes.Calls);
		Assert.DoesNotContain("items build-log", s.Nodes.Calls);
		p.Items[0].IsXmlOpen = false;
		PubSubItemRow first = p.Items[0];

		await p.LoadMoreCommand.ExecuteAsync(null);
		Assert.Equal(100, p.Items.Count);
		Assert.Same(first, p.Items[0]);
		Assert.Equal("100 items of 120", p.ItemsFooter);
		Assert.True(p.HasMore);
		Assert.Contains("items build-log", s.Nodes.Calls);

		await p.LoadMoreCommand.ExecuteAsync(null);
		Assert.Equal(120, p.Items.Count);
		Assert.False(p.HasMore);
		Assert.Equal("120 items", p.ItemsFooter);
		Assert.Equal("build-1", p.Items[^1].Title);

		p.ItemFilter = "failed";
		Assert.Equal(7, p.Items.Count);
		Assert.Equal("7 items of 120 match", p.ItemsFooter);
		Capture(window, "pubsub-items-filter-light-1440");
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task SettingsAreInPlainWordsAndRemovingTakesTheNodesName(string theme)
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, theme);
		PubSubViewModel p = s.View;
		string t = theme.ToLowerInvariant();
		await ShowNodesAsync(s);
		await OpenLabAsync(s);
		await s.OpenAsync("lab/alerts");

		p.SetTabCommand.Execute(PubSubTab.Settings);
		await Settle(p);
		Assert.True(p.IsSettings);
		Assert.True(p.HasSettings);
		Assert.Equal("Alerts from the lab", p.Settings.Title);
		Assert.Equal("Anyone", p.Settings.Access.Text);
		Assert.Equal("Owners, and the people named as publishers", p.Settings.Publishers.Text);
		Assert.True(p.Settings.KeepItems);
		Assert.Equal(string.Empty, p.Settings.MaxItems);
		Assert.False(p.Settings.IsChanged);
		Capture(window, $"pubsub-settings-{t}-1440");

		// A number that is not one is said before anything is sent.
		p.Settings.MaxItems = "many";
		await p.SaveSettingsCommand.ExecuteAsync(null);
		Assert.StartsWith("How many items to keep has to be a whole number", p.SettingsError);
		Assert.DoesNotContain("save lab/alerts", s.Nodes.Calls);

		p.Settings.MaxItems = "20";
		p.Settings.Access = p.Settings.AccessChoices.Single(c => c.Value == NodeAccess.Authorize);
		p.Settings.SendLast = p.Settings.LastChoices.Single(c => c.Value == NodeLastItem.Never);
		Assert.True(p.Settings.IsChanged);
		await p.SaveSettingsCommand.ExecuteAsync(null);
		Assert.Null(p.SettingsError);
		Assert.Equal("Saved.", p.SettingsNote);
		Assert.False(p.Settings.IsChanged);
		NodeSettings saved = await s.PubSub.SettingsAsync(s.Ops, s.Node("lab/alerts").Info);
		Assert.Equal((NodeAccess.Authorize, 20, NodeLastItem.Never, "Alerts from the lab"), (saved.Access, saved.MaxItems, saved.SendLast, saved.Title));

		// Removing all items wants the node's name typed.
		p.AskClearCommand.Execute(null);
		Assert.True(p.IsAsking);
		Assert.Equal("Remove all items from lab/alerts?", p.Ask!.Title);
		Assert.True(p.Ask.IsDanger);
		Assert.False(p.Ask.CanConfirm);
		p.Ask.Input = "lab/alert";
		Assert.False(p.Ask.CanConfirm);
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"pubsub-remove-all-ask-{t}-1440");
		await p.ConfirmAskCommand.ExecuteAsync(null);
		Assert.Equal(3, s.Nodes.Count(Service, "lab/alerts"));
		p.Ask.Input = "lab/alerts";
		Assert.True(p.Ask.CanConfirm);
		await p.ConfirmAskCommand.ExecuteAsync(null);
		Assert.False(p.IsAsking);
		Assert.Equal(0, s.Nodes.Count(Service, "lab/alerts"));
		Assert.Equal("Removed all items from lab/alerts", s.Said[^1]);
		p.SetTabCommand.Execute(PubSubTab.Items);
		Assert.Equal("Nothing is published on this node.", p.ItemsEmptyText);

		// Cancel leaves everything as it was.
		p.AskDeleteCommand.Execute(null);
		Assert.Equal("Delete the node lab/alerts?", p.Ask!.Title);
		p.CancelAskCommand.Execute(null);
		Assert.False(p.IsAsking);
		Assert.True(s.Nodes.Has(Service, "lab/alerts"));

		// Deleting takes the name too, and the node leaves the tree.
		p.AskDeleteCommand.Execute(null);
		p.Ask!.Input = "lab/alerts";
		await p.ConfirmAskCommand.ExecuteAsync(null);
		Assert.False(s.Nodes.Has(Service, "lab/alerts"));
		Assert.DoesNotContain(p.Nodes, n => n.Node == "lab/alerts");
		Assert.Null(p.SelectedNode);
		Assert.True(p.ShowsPicker);
		Assert.Equal("Deleted lab/alerts", s.Said[^1]);
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task PeopleCanBeGivenRolesAndSubscribersAnswered(string theme)
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, theme);
		PubSubViewModel p = s.View;
		await ShowNodesAsync(s);
		await OpenLabAsync(s);
		await s.OpenAsync("lab/alerts");

		p.SetTabCommand.Execute(PubSubTab.People);
		await Settle(p);
		Assert.True(p.CanSeePeople);
		Assert.Equal([Me + " (you)", "robin@lab.tagroot.io"], p.People.Select(x => x.Label));
		Assert.Equal(["Owner", "May publish and read"], p.People.Select(x => x.RoleText));

		// Whoever asked comes first.
		Assert.Equal(["sam@partner.example", "kim@lab.tagroot.io"], p.Subscribers.Select(x => x.Jid));
		Assert.True(p.Subscribers[0].IsPending);
		Assert.Equal("Asked to subscribe", p.Subscribers[0].StateText);
		Capture(window, $"pubsub-people-{theme.ToLowerInvariant()}-1440");

		Assert.Equal(["Copy address", "Chat with them", "Allow", "Deny"], Texts(p.ActionsFor([p.Subscribers[0]])));
		await p.AllowSubscriberCommand.ExecuteAsync(p.Subscribers[0]);
		Assert.Equal("Subscribes", p.Subscribers.Single(x => x.Jid == "sam@partner.example").StateText);
		Assert.Equal("Let sam@partner.example subscribe", s.Said[^1]);

		await p.EndSubscriberCommand.ExecuteAsync(p.Subscribers.Single(x => x.Jid == "kim@lab.tagroot.io"));
		Assert.Equal(["sam@partner.example"], p.Subscribers.Select(x => x.Jid));

		// Giving somebody a role: an address is asked for.
		p.NewPersonJid = "lee";
		await p.AddPersonCommand.ExecuteAsync(null);
		Assert.StartsWith("That does not look like an address.", p.PeopleError);
		p.NewPersonJid = "lee@lab.tagroot.io";
		p.NewPersonRole = p.Roles.Single(r => r.Value == NodeRole.Member);
		await p.AddPersonCommand.ExecuteAsync(null);
		Assert.Null(p.PeopleError);
		Assert.Equal(string.Empty, p.NewPersonJid);
		Assert.Equal("May read and subscribe", p.People.Single(x => x.Jid == "lee@lab.tagroot.io").RoleText);

		// Changing and taking away, from the row.
		NodePersonRow robin = p.People.Single(x => x.Jid == "robin@lab.tagroot.io");
		IReadOnlyList<ItemAction> change = p.ActionsFor([robin]);
		Assert.Equal(["Copy address", "Chat with them", "May publish, but not read", "May read and subscribe", "Owner: may do everything", "Blocked: kept out", "Take off the list"], Texts(change));
		await Action(change, "Blocked: kept out").RunAsync();
		Assert.True(p.People.Single(x => x.Jid == "robin@lab.tagroot.io").IsBlocked);
		await Action(p.ActionsFor([p.People.Single(x => x.Jid == "lee@lab.tagroot.io")]), "Take off the list").RunAsync();
		Assert.DoesNotContain(p.People, x => x.Jid == "lee@lab.tagroot.io");

		// The last owner cannot stop being one.
		await Action(p.ActionsFor([p.People.Single(x => x.IsMe)]), "May read and subscribe").RunAsync();
		Assert.Equal("The role was not changed. A node needs at least one owner.", p.PeopleError);
		Assert.Equal("Owner", p.People.Single(x => x.IsMe).RoleText);
	}

	[AvaloniaFact]
	public async Task WhatYouMayNotDoSaysSo()
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, "Light");
		PubSubViewModel p = s.View;
		await ShowNodesAsync(s);

		// Somebody else's node: its items are there, its settings and people are the owners'.
		await s.OpenAsync("announcements");
		Assert.Single(p.Items);
		p.SetTabCommand.Execute(PubSubTab.Settings);
		await Settle(p);
		Assert.False(p.HasSettings);
		Assert.Equal("The node did not give its settings. You are not allowed to. Ask one of the node's owners.", p.SettingsError);
		Capture(window, "pubsub-settings-refused-light-1440");
		p.SetTabCommand.Execute(PubSubTab.People);
		await Settle(p);
		Assert.False(p.CanSeePeople);
		Assert.Equal("The node did not say who may do what. You are not allowed to. Ask one of the node's owners.", p.PeopleError);

		// Removing an item there is refused, and the item stays.
		p.SetTabCommand.Execute(PubSubTab.Items);
		await p.RetractCommand.ExecuteAsync(p.Items[0]);
		Assert.Single(p.Items);
		Assert.Equal("The item was not removed. You are not allowed to. Ask one of the node's owners.", p.ItemsError);
		Assert.False(p.HasUndo);

		// A closed node: not even its items.
		await s.OpenAsync("closed-club");
		Assert.Empty(p.Items);
		Assert.Equal("The node did not give its items. You are not allowed to. Ask one of the node's owners.", p.ItemsError);
		Assert.Equal(string.Empty, p.ItemsEmptyText);
		Capture(window, "pubsub-items-refused-light-1440");
		await p.ToggleWatchCommand.ExecuteAsync(null);
		Assert.False(p.IsWatching);
		Assert.StartsWith("The node did not let you watch it.", p.WatchNote);

		// A service that does not answer: said where the nodes would be, with a way to try again.
		s.Nodes.Fail = what => what == "nodes" ? new TimeoutException() : null;
		await p.ReloadNodesCommand.ExecuteAsync(null);
		Assert.Empty(p.Nodes);
		Assert.Equal("The service did not answer in time.", p.Error);
		Assert.Equal(string.Empty, p.NodesEmptyText);
		Capture(window, "pubsub-nodes-timeout-light-1440");
		s.Nodes.Fail = null;
		await p.ReloadNodesCommand.ExecuteAsync(null);
		Assert.Null(p.Error);
		Assert.Equal(5, p.Nodes.Count);

		// One node failing leaves the others alone.
		s.Nodes.Fail = what => what == "items" ? new InvalidOperationException("The node did not give its items. It is not there any more.") : null;
		await s.OpenAsync("build-log");
		Assert.Equal("The node did not give its items. It is not there any more.", p.ItemsError);
		Assert.Null(p.Error);
		s.Nodes.Fail = null;
		await p.ReloadCommand.ExecuteAsync(null);
		Assert.Null(p.ItemsError);
		Assert.Equal(50, p.Items.Count);
	}

	[AvaloniaFact]
	public async Task ANewNodeStartsFromTheServicesSettings()
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, "Light");
		PubSubViewModel p = s.View;
		s.Nodes.Defaults = new NodeSettings { MaxItems = 10, Publishers = NodePublishers.Subscribers };
		await ShowNodesAsync(s);

		// From a collection, the new node can go inside it.
		await s.OpenAsync("lab");
		await p.OpenCreateCommand.ExecuteAsync(null);
		Assert.True(p.IsCreating);
		Assert.Null(p.SelectedNode);
		Assert.Equal("Put it inside lab", p.NewParentText);
		Assert.True(p.NewInsideParent);
		Assert.Equal("10", p.NewSettings.MaxItems);
		Assert.Equal("Everyone who subscribes", p.NewSettings.Publishers.Text);
		Assert.Null(p.NewNote);

		p.NewNodeName = "lab/doors";
		p.NewSettings.Title = "Doors in the lab";
		p.NewSettings.Access = p.NewSettings.AccessChoices.Single(c => c.Value == NodeAccess.Presence);
		Dispatcher.UIThread.RunJobs();
		Capture(window, "pubsub-new-node-light-1440");
		await p.CreateNodeCommand.ExecuteAsync(null);
		Assert.Null(p.CreateError);
		Assert.False(p.IsCreating);
		Assert.Equal("Made lab/doors", s.Said[^1]);

		// It is in the tree, inside the collection, with what was already there, and open.
		Assert.Equal("lab/doors", p.SelectedNode?.Node);
		Assert.Equal(1, p.SelectedNode!.Depth);
		Assert.Equal(["announcements", "approvals", "build-log", "closed-club", "lab", "lab/alerts", "lab/meter-11", "lab/doors"], p.Nodes.Select(n => n.Node));
		NodeSettings made = await s.PubSub.SettingsAsync(s.Ops, p.SelectedNode.Info);
		Assert.Equal(("Doors in the lab", NodeAccess.Presence, 10, NodePublishers.Subscribers), (made.Title, made.Access, made.MaxItems, made.Publishers));

		// A name that is taken says so and keeps the form.
		await p.OpenCreateCommand.ExecuteAsync(null);
		Assert.False(p.HasNewParent);
		p.NewNodeName = "build-log";
		await p.CreateNodeCommand.ExecuteAsync(null);
		Assert.True(p.IsCreating);
		Assert.Equal("The node was not created. There is one with that name already.", p.CreateError);

		// A collection, at the top.
		p.NewNodeName = "plant";
		p.NewIsCollection = true;
		await p.CreateNodeCommand.ExecuteAsync(null);
		Assert.True(p.SelectedNode!.IsCollection);
		Assert.Equal(0, p.SelectedNode.Depth);

		// A service that will not say what a new node gets: the usual settings, and that is said.
		s.Nodes.Fail = what => what == "settings" ? new InvalidOperationException("no") : null;
		await p.OpenCreateCommand.ExecuteAsync(null);
		Assert.StartsWith("The service did not say what it gives a new node", p.NewNote);
		Assert.Equal(string.Empty, p.NewSettings.MaxItems);
		p.CancelCreateCommand.Execute(null);
		Assert.False(p.IsCreating);
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task PersonalStorageReadsInWords(string theme)
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, theme);
		PubSubViewModel p = s.View;
		string t = theme.ToLowerInvariant();

		s.Vm.SelectedAccount = s.Vm.Accounts.First(a => a.Session.Profile.Id == s.Ops);
		s.Vm.ShowAccountTabCommand.Execute(WorkspaceKind.AccountStorage);
		await Settle(p);
		Assert.True(s.Vm.IsAccountStorage);
		Assert.Equal("Storage", p.Title);
		Assert.True(p.IsPersonal);
		Assert.Equal(7, p.Nodes.Count);
		Assert.False(Window_Shows(window, "NewNodeButton"));

		async Task<PubSubItemRow> Only(string node)
		{
			await s.OpenAsync(node);
			return Assert.Single(p.Items);
		}

		// The usual personal nodes are called what they are, with their real names under.
		Assert.Equal(("Avatar picture", "urn:xmpp:avatar:data"), (s.Node("urn:xmpp:avatar:data").Label, s.Node("urn:xmpp:avatar:data").Sub));
		p.NodeFilter = "nick";
		Assert.Equal(["http://jabber.org/protocol/nick"], p.Nodes.Select(n => n.Node));
		p.NodeFilter = string.Empty;

		PubSubItemRow picture = await Only("urn:xmpp:avatar:data");
		Assert.Equal("Avatar picture", p.NodeTitle);
		Assert.Equal("Avatar picture", picture.Kind);
		Assert.True(picture.HasPicture);
		Assert.Equal("A picture, 119 bytes", picture.Summary);
		Assert.False(picture.IsXmlOpen);
		Capture(window, $"pubsub-storage-picture-{t}-1440");

		PubSubItemRow details = await Only("urn:xmpp:avatar:metadata");
		Assert.Equal(("image/png", "24 × 24 · 119 bytes"), (details.Fields[0].Label, details.Fields[0].Value));

		Assert.Equal(("Nickname", "Ops on call"), ((await Only("http://jabber.org/protocol/nick")).Kind, p.Items[0].Summary));
		Assert.Equal(("Mood", "Relieved: The deploy went through"), ((await Only("http://jabber.org/protocol/mood")).Kind, p.Items[0].Summary));

		PubSubItemRow tune = await Only("http://jabber.org/protocol/tune");
		Assert.Equal("Heart of the Sunrise by Yes", tune.Summary);
		Assert.Equal(["From: Fragile", "Length: 11:26"], tune.Fields.Select(f => f.Label + ": " + f.Value));

		PubSubItemRow place = await Only("http://jabber.org/protocol/geoloc");
		Assert.Equal("Location", place.Kind);
		Assert.Equal("Stockholm (59.3293, 18.0686)", place.Summary);
		Assert.Contains(place.Fields, f => f is { Label: "Accuracy", Value: "20 m" });
		Capture(window, $"pubsub-storage-place-{t}-1440");

		PubSubItemRow room = await Only("urn:xmpp:bookmarks:1");
		Assert.Equal("Ops team, joined when you connect", room.Summary);

		// The Neuron's nodes are a different list: going there leaves storage behind.
		await ShowNodesAsync(s);
		Assert.False(p.IsPersonal);
		Assert.Equal(5, p.Nodes.Count);
		Assert.True(Window_Shows(window, "NewNodeButton"));
	}

	private static bool Window_Shows(Visual root, string name)
		=> root.GetVisualDescendants().OfType<Control>().Any(v => v.Name == name && v.IsEffectivelyVisible);

	[AvaloniaFact]
	public async Task SubscriptionRequestsLandInTheInbox()
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, "Light");
		InboxViewModel inbox = s.Vm.Inbox;

		s.PubSub.Preview(new NodeRequest(s.Ops, Service, "lab/alerts", "pat@partner.example", DateTime.Now.AddMinutes(-2)));
		s.PubSub.Preview(new NodeRequest(s.Ops, Service, "build-log", "lee@lab.tagroot.io", DateTime.Now.AddMinutes(-1)));
		inbox.Refresh();

		List<InboxRowViewModel> rows = [.. inbox.Rows.Where(r => r.Kind == InboxRowKind.NodeRequest)];
		Assert.Equal(2, rows.Count);
		Assert.Equal(2, inbox.RequestCount);
		InboxRowViewModel pat = rows.Single(r => r.Jid == "pat@partner.example");
		Assert.Equal("pat@partner.example wants to subscribe to lab/alerts", pat.Title);
		Assert.Equal("Subscription request", pat.KindLabel);
		Assert.Equal(("Allow", "Deny"), (pat.PrimaryText, pat.SecondaryText));
		Assert.True(pat.IsRequest);

		s.Vm.ShowAttentionCommand.Execute(null);
		inbox.SelectedRow = pat;
		Dispatcher.UIThread.RunJobs();
		Capture(window, "pubsub-inbox-request-light-1440");

		// Allow: the row leaves, and the answer goes out when it can no longer be taken back.
		await inbox.PrimaryCommand.ExecuteAsync(pat);
		Assert.Equal("Allowed pat@partner.example.", inbox.UndoText);
		Assert.DoesNotContain(inbox.Rows, r => r.Jid == "pat@partner.example");
		await inbox.CommitAsync();
		Assert.Contains("subscriber lab/alerts pat@partner.example Subscribed", s.Nodes.Calls);
		Assert.Single(s.PubSub.Requests);

		// Deny, taken back: nothing is sent and the request is still there.
		InboxRowViewModel lee = inbox.Rows.Single(r => r.Jid == "lee@lab.tagroot.io");
		await inbox.SecondaryCommand.ExecuteAsync(lee);
		Assert.Equal("Denied lee@lab.tagroot.io.", inbox.UndoText);
		inbox.UndoCommand.Execute(null);
		inbox.Refresh();
		Assert.Contains(inbox.Rows, r => r.Jid == "lee@lab.tagroot.io");
		Assert.DoesNotContain(s.Nodes.Calls, c => c.Contains("lee@lab.tagroot.io", StringComparison.Ordinal));

		await inbox.SecondaryCommand.ExecuteAsync(inbox.Rows.Single(r => r.Jid == "lee@lab.tagroot.io"));
		await inbox.CommitAsync();
		Assert.Contains("subscriber build-log lee@lab.tagroot.io None", s.Nodes.Calls);
		Assert.Empty(s.PubSub.Requests);
	}

	[AvaloniaFact]
	public async Task ASmallWindowKeepsTheNodeUsable()
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, "Light", 900, 640);
		PubSubViewModel p = s.View;
		await ShowNodesAsync(s);
		await OpenLabAsync(s);
		await s.OpenAsync("lab/meter-11");
		Dispatcher.UIThread.RunJobs();

		Assert.True(Window_Shows(window, "PublishButton"));
		Assert.True(Window_Shows(window, "WatchButton"));
		Assert.True(Window_Shows(window, "MoreButton"));
		Assert.True(Shown<ListBox>(window, "ItemList").Bounds.Width >= 380);
		Capture(window, "pubsub-items-sensor-light-900");

		p.OpenEditorCommand.Execute(null);
		p.PublishXml = Reading(22.0, 14);
		p.TidyCommand.Execute(null);
		Assert.Equal(10, p.PublishXml.Split('\n').Length);
		Dispatcher.UIThread.RunJobs();
		Assert.True(Window_Shows(window, "SendButton"));
		Capture(window, "pubsub-editor-light-900");
	}
}
