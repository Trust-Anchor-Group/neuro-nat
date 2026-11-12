using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Win32;
using Neuro.Nat.App.Controls;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Desktop;
using Neuro.Nat.Desktop.Install;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Links;
using Waher.Networking.XMPP.Concentrator;
using Sample = Neuro.Nat.App.Tests.GettingAroundTests.Sample;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// Links to things: each kind is made in one window and opened in another, and lands on the
/// same thing. What cannot be followed says so in one note. Garbage is never a link and never
/// an exception. The message a second start sends, and the scheme in the registry.
/// </summary>
public class LinkTests
{
	private const string Lab = "lab.example.com";
	private const string Gbg = "gbg.lab.example.com";
	private const string Kim = "kim@lab.example.com";
	private const string OpsRoom = "ops-team@muc.lab.example.com";

	private static MainWindowViewModel Another(Sample s)
	{
		MainWindowViewModel other = new(s.Manager, s.Fleet, s.Chat, null, null, s.PubSub, s.Software, primary: s.Vm);
		other.Sources.RootsStandIn = _ => Tree(other.Sources);
		other.Join();
		other.Fleet.Refresh();
		other.Events.Flush();
		other.Chat.Refresh();
		Dispatcher.UIThread.RunJobs();
		return other;
	}

	private static IReadOnlyList<TreeItemViewModel> Tree(SourcesViewModel s)
	{
		static DataSourceReference Src(string id, string name) => new(id, name, false, DateTime.Now);
		static NodeInformation Node(string id, string src, bool children)
			=> new(id, src, string.Empty, "Waher.Things.Metering.NodeTypes.NodeCollection", "Node", Waher.Things.NodeState.None, id, string.Empty, children, true, false, false, false, false,
				"Root", string.Empty, new DateTime(2026, 11, 9, 19, 40, 46), [], []);

		SourceItemViewModel connections = new(s, null, Src("Connections", "Connections"));
		connections.SetChildren([]);
		SourceItemViewModel metering = new(s, null, Src("MeteringTopology", "Metering Topology"));
		NodeItemViewModel root = new(s, metering, Node("Root", "MeteringTopology", true));
		metering.SetChildren([root]);
		NodeItemViewModel warehouse = new(s, root, Node("Gothenburg warehouse", "MeteringTopology", true));
		root.SetChildren([new NodeItemViewModel(s, root, Node("MQTT bridge", "MeteringTopology", false)), warehouse]);
		warehouse.SetChildren([new NodeItemViewModel(s, warehouse, Node("Meter 11/a", "MeteringTopology", false))]);
		return [connections, metering];
	}

	private static string LinkOf(object item) => Assert.IsType<NatLink>(Links.For(item)).ToString();

	// ----- Each kind: made here, opened in another window, the same thing -----

	[AvaloniaFact]
	public async Task EachKindOpensOnTheSameThingInAnotherWindow()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;
		MainWindowViewModel other = Another(s);

		// A Neuron, on the tab you were on. Named by its domain; the account is not in it.
		vm.OpenNeuron(Gbg, s.Ops, WorkspaceKind.Updates);
		Dispatcher.UIThread.RunJobs();
		string neuron = LinkOf(vm.Page!);
		Assert.Equal("neuro-nat://neuron/gbg.lab.example.com/software", neuron);
		Assert.DoesNotContain(s.Ops.ToString("N"), neuron);
		Assert.Equal(LinkOutcome.Opened, await other.OpenLinkAsync(neuron));
		Assert.Equal(WorkspaceKind.Updates, other.Workspace);
		Assert.Equal(Gbg, other.Page?.BareJid);

		// From the list, a Neuron opens on its Overview.
		FleetRowViewModel labRow = vm.Fleet.Rows.Single(r => r.BareJid == Lab);
		Assert.Equal("neuro-nat://neuron/lab.example.com", LinkOf(labRow));
		Assert.Equal(LinkOutcome.Opened, await other.OpenLinkAsync(LinkOf(labRow)));
		Assert.Equal(WorkspaceKind.Neuron, other.Workspace);
		Assert.Equal(Lab, other.Page?.BareJid);

		// A problem, with its events.
		IssueItemViewModel issue = vm.Events.Issues.First(i => i.Issue.Neurons.Contains(Gbg));
		string problem = LinkOf(issue);
		Assert.Equal("neuro-nat://problem/gbg.lab.example.com/" + issue.Fingerprint.ToLowerInvariant(), problem);
		Assert.Equal(LinkOutcome.Opened, await other.OpenLinkAsync(problem));
		Assert.True(other.IsEvents);
		Assert.Equal(issue.Fingerprint, other.Events.SelectedIssue?.Fingerprint);
		Assert.All(other.Events.Stream, e => Assert.Equal(issue.Fingerprint, e.Fingerprint));

		// An event, selected in the list, at its time.
		NeuronEvent ev = vm.Events.Stream.First(e => e.Source.NeuronJid == Lab);
		NatLink eventLink = Assert.IsType<NatLink>(Links.For(ev));
		Assert.StartsWith("neuro-nat://event/lab.example.com/", eventLink.ToString());
		Assert.Contains("?at=" + ev.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), eventLink.ToString());
		Assert.Equal(LinkOutcome.Opened, await other.OpenLinkAsync(eventLink.ToString()));
		Assert.Equal(WorkspaceKind.NeuronEvents, other.Workspace);
		Assert.Equal(Lab, other.Page?.BareJid);
		Assert.Equal(ev.Sequence, other.NeuronEvents.SelectedEvent?.Sequence);
		Assert.Contains(other.NeuronEvents.SelectedEvent!, other.NeuronEvents.Stream);

		// A node in Sources: the tree opened down to it.
		vm.Sources.RootsStandIn = _ => Tree(vm.Sources);
		vm.OpenNeuron(Gbg, s.Ops, WorkspaceKind.Sources);
		await vm.Sources.LoadTreeCommand.ExecuteAsync(null);
		Dispatcher.UIThread.RunJobs();
		TreeItemViewModel warehouse = vm.Sources.Roots[1].Children[0].Children[1];
		NodeItemViewModel meter = Assert.IsType<NodeItemViewModel>(warehouse.Children[0]);
		string node = LinkOf(meter);
		Assert.Equal("neuro-nat://node/gbg.lab.example.com/MeteringTopology/Root/Gothenburg%20warehouse/Meter%2011%2Fa", node);
		Assert.Equal(LinkOutcome.Opened, await other.OpenLinkAsync(node));
		Assert.Equal(WorkspaceKind.Sources, other.Workspace);
		Assert.Equal(Gbg, other.Page?.BareJid);
		NodeItemViewModel landed = Assert.IsType<NodeItemViewModel>(other.Sources.SelectedItem);
		Assert.Equal("Meter 11/a", landed.Node.NodeId);
		for (TreeItemViewModel? up = landed.Parent; up is not null; up = up.Parent)
			Assert.True(up.IsExpanded, up.Label + " is open");

		// A data source is a place in the tree too.
		Assert.Equal("neuro-nat://node/gbg.lab.example.com/MeteringTopology", LinkOf(vm.Sources.Roots[1]));

		// A conversation, and a room.
		PersonViewModel kim = vm.Chat.Everyone.Single(p => p.BareJid == Kim);
		Assert.Equal("neuro-nat://chat/kim@lab.example.com", LinkOf(kim));
		Assert.Equal(LinkOutcome.Opened, await other.OpenLinkAsync(LinkOf(kim)));
		Assert.True(other.IsChat);
		Assert.Equal(Kim, other.Chat.SelectedPerson?.BareJid);

		PersonViewModel room = vm.Chat.Everyone.Single(p => p.BareJid == OpsRoom);
		Assert.Equal("neuro-nat://room/ops-team@muc.lab.example.com", LinkOf(room));
		Assert.Equal(LinkOutcome.Opened, await other.OpenLinkAsync(LinkOf(room)));
		Assert.True(other.IsChat);
		Assert.True(other.Chat.SelectedPerson?.IsRoom);
		Assert.Equal(OpsRoom, other.Chat.SelectedPerson?.BareJid);

		// Nothing went wrong on the way.
		Assert.DoesNotContain(MainWindowViewModel.NotALink, s.Said);
	}

	// ----- When it cannot be followed -----

	[AvaloniaFact]
	public async Task WhatCannotBeFollowedSaysSoInOneNote()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;
		vm.ShowFleetCommand.Execute(null);

		// None of your accounts is on that Neuron: a note, and nothing opens.
		s.Said.Clear();
		Assert.Equal(LinkOutcome.NotThere, await vm.OpenLinkAsync("neuro-nat://neuron/elsewhere.example.org/events"));
		Assert.Equal(["This is on elsewhere.example.org. None of your accounts is there."], s.Said);
		Assert.True(vm.IsFleet);

		// Started from outside while accounts are still connecting: it waits for the Neuron instead.
		Assert.Equal(LinkOutcome.Waiting, await vm.OpenLinkAsync("neuro-nat://neuron/elsewhere.example.org", patient: true));
		Assert.True(vm.IsFleet);

		// The event is older than what is kept: the Neuron's events at that time, and a word that it is gone.
		NeuronEvent oldest = s.Monitors.Hub.Snapshot().Where(e => e.Source.NeuronJid == Gbg).MinBy(e => e.Timestamp)!;
		DateTime at = oldest.Timestamp.ToUniversalTime().AddMinutes(-30);
		s.Said.Clear();
		Assert.Equal(LinkOutcome.Gone, await vm.OpenLinkAsync($"neuro-nat://event/gbg.lab.example.com/0123456789abcdef?at={at:yyyy-MM-dd'T'HH:mm:ss'Z'}"));
		Assert.Equal([MainWindowViewModel.EventGone], s.Said);
		Assert.Equal(WorkspaceKind.NeuronEvents, vm.Workspace);
		Assert.Equal(Gbg, vm.Page?.BareJid);
		Assert.Equal(oldest.Sequence, vm.NeuronEvents.SelectedEvent?.Sequence);

		// Broken, or not a NAT link: one quiet note, and nothing opens.
		vm.ShowFleetCommand.Execute(null);
		foreach (string broken in new[] { "neuro-nat://neuron/../../windows", "neuro-nat://reboot/gbg.lab.example.com", "https://lab.example.com", "neuro-nat://" })
		{
			s.Said.Clear();
			Assert.Equal(LinkOutcome.Broken, await vm.OpenLinkAsync(broken));
			Assert.Equal([MainWindowViewModel.NotALink], s.Said);
			Assert.True(vm.IsFleet);
		}
	}

	// ----- Copy link, on every menu and on the bar -----

	[AvaloniaFact]
	public async Task CopyLinkIsOnEveryMenuAndOnTheBar()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;

		void Has(IActionSource source, params object[] items)
		{
			IReadOnlyList<ItemAction> actions = ActionMenu.ActionsOf(source, items);
			ItemAction copy = Assert.Single(actions, a => a.Text is "Copy link" or "Copy links");
			Assert.Equal(ActionGroup.Copy, copy.Group);
			Assert.True(copy.InBar);
		}

		FleetRowViewModel row = vm.Fleet.Rows.First();
		Has(vm.Fleet, row);
		Has(vm, vm.NeuronChips.First());
		Has(vm.Events, vm.Events.Issues.First());
		Has(vm.Events, vm.Events.Stream[0]);
		Has(vm.Chat, vm.Chat.Everyone.First(p => p.BareJid == Kim));
		vm.OpenNeuron(Gbg, s.Ops, WorkspaceKind.NeuronConsole);
		Has(vm, vm.Page!);

		// Several selected: one link a line, from the bar.
		NeuronEvent[] two = [vm.Events.Stream[0], vm.Events.Stream[1]];
		ItemAction links = ActionMenu.ActionsOf(vm.Events, two).Single(a => a.Text == "Copy links");
		s.Copied.Clear();
		await links.RunAsync();
		Assert.Equal(string.Join(Environment.NewLine, two.Select(e => NatLink.Event(e).ToString())), Assert.Single(s.Copied));
		Assert.Contains("Copied 2 links", s.Said);

		// What has no link gets no "Copy link".
		Assert.DoesNotContain(ActionMenu.ActionsOf(vm, [vm.Accounts[0]]), a => a.Text == "Copy link");

		// The page's link is the tab you are on.
		s.Copied.Clear();
		await ActionMenu.ActionsOf(vm, [vm.Page!]).Single(a => a.Text == "Copy link").RunAsync();
		Assert.Equal("neuro-nat://neuron/gbg.lab.example.com/console", Assert.Single(s.Copied));
	}

	// ----- In chat and in the search box -----

	[AvaloniaFact]
	public async Task ALinkInChatIsACardAndOpensFromItAndFromSearch()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;
		IssueItemViewModel issue = vm.Events.Issues.First(i => i.Issue.Neurons.Contains(Gbg));
		string problem = Links.For(issue)!.ToString();
		s.Chat.Preview(s.Ops, Kim, "Gbg again: " + problem + ". And neuro-nat://neuron/gbg.lab.example.com/sources", "Kim Lindqvist");
		vm.ShowChat(s.Ops, Kim);
		Dispatcher.UIThread.RunJobs();

		MessageViewModel message = vm.Chat.Messages.Last();
		Assert.True(message.HasCards);
		foreach (string theme in new[] { "Light", "Dark" })
		{
			Avalonia.Controls.Window window = GettingAroundTests.Window(s, theme);
			GettingAroundTests.Capture(window, "links-chat-card-" + theme.ToLowerInvariant());
			window.Close();
		}

		Assert.Equal(2, message.Cards.Count);
		Assert.Equal(issue.Title, message.Cards[0].Title);
		Assert.StartsWith("Open, ", message.Cards[0].State);
		Assert.True(message.Cards[0].IsBad);
		Assert.Equal("Gothenburg edge, Sources", message.Cards[1].Title);
		Assert.True(message.Cards[1].IsOk);

		// A click opens it.
		vm.Chat.OpenLinkCardCommand.Execute(message.Cards[1]);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(WorkspaceKind.Sources, vm.Workspace);
		Assert.Equal(Gbg, vm.Page?.BareJid);

		// Pasted into the search box, a link opens the same way.
		vm.Search.Open();
		vm.Search.Query = problem;
		Dispatcher.UIThread.RunJobs();
		Assert.False(vm.Search.IsOpen);
		Assert.True(vm.IsEvents);
		Assert.Equal(issue.Fingerprint, vm.Events.SelectedIssue?.Fingerprint);

		// A broken one pasted there is a note, and nothing opens.
		vm.Search.Open();
		vm.Search.Query = "neuro-nat://neuron/gbg..x";
		Assert.False(vm.Search.IsOpen);
		Assert.Contains(MainWindowViewModel.NotALink, s.Said);
		Assert.True(vm.IsEvents);

		// Typed by hand, it opens with Enter.
		vm.Search.Open();
		foreach (char c in "neuro-nat://neuron/lab.example.com")
			vm.Search.Query += c;
		Assert.True(vm.Search.IsOpen);
		vm.Search.Run();
		Assert.Equal(WorkspaceKind.Neuron, vm.Workspace);
		Assert.Equal(Lab, vm.Page?.BareJid);
	}

	// ----- Reading links: well-formed ones read back, garbage never does -----

	[Fact]
	public void EveryKindReadsBackAsItWasWritten()
	{
		NeuronEvent e = new()
		{
			Source = new EventSource(Guid.NewGuid(), Gbg),
			Timestamp = new DateTime(2026, 11, 12, 10, 42, 13, 517, DateTimeKind.Utc),
			Message = "Connection reset",
			EventId = "S2S",
		};

		NatLink[] links =
		[
			NatLink.Neuron(Gbg),
			NatLink.Neuron("neuron@" + Lab, "events"),
			NatLink.Problem(Gbg, "0A1B2C3D4E5F6071"),
			NatLink.Event(e),
			NatLink.Node(Gbg, ["MeteringTopology", "Root", "Floor 2/East", "Mätare ✓", "100%"]),
			NatLink.Chat("Kim@Lab.Example.com"),
			NatLink.Room(OpsRoom),
			NatLink.Feature(Gbg, "telemetry", "metric:Nfc.Attempts/platform"),
			NatLink.Feature(Gbg, "telemetry"),
		];

		foreach (NatLink link in links)
		{
			string text = link.ToString();
			Assert.True(NatLink.TryParse(text, out NatLink back), text);
			Assert.Equal(link, back);
			Assert.Equal(text, back.ToString());
			Assert.All(text, c => Assert.InRange(c, '!', '~'));
		}

		Assert.Equal("neuro-nat://neuron/lab.example.com/events", links[1].ToString());
		Assert.Equal("neuro-nat://event/gbg.lab.example.com/" + NatLink.EventId(e) + "?at=2026-11-12T10:42:13Z", links[3].ToString());
		Assert.Equal("neuro-nat://chat/kim@lab.example.com", links[5].ToString());
		Assert.Equal("neuro-nat://feature/gbg.lab.example.com/telemetry/metric%3ANfc.Attempts%2Fplatform", links[7].ToString());
		Assert.Equal("metric:Nfc.Attempts/platform", links[7].Page);
		Assert.Equal("telemetry", links[8].View);
		Assert.Null(links[8].Page);
		Assert.False(NatLink.TryParse("neuro-nat://feature/gbg.lab.example.com", out _));
		Assert.False(NatLink.TryParse("neuro-nat://feature/gbg.lab.example.com/telemetry/a/b", out _));
		Assert.False(NatLink.TryParse("neuro-nat://feature/gbg.lab.example.com/telemetry/x?at=2026-11-12T10:42:13Z", out _));

		// The same event has the same id whichever way it came, at whatever fraction of its second.
		Assert.Equal(NatLink.EventId(e), NatLink.EventId(e with { Timestamp = e.Timestamp.AddMilliseconds(200), FromHistory = true, Sequence = 99 }));
		Assert.NotEqual(NatLink.EventId(e), NatLink.EventId(e with { Message = "Something else" }));

		// Upper case, a slash at the end, and links inside a sentence.
		Assert.True(NatLink.TryParse("NEURO-NAT://Neuron/GBG.lab.example.com/Software/", out NatLink loud));
		Assert.Equal(NatLink.Neuron(Gbg, "software"), loud);
		Assert.Equal([links[0], links[6]], NatLink.FindAll($"See <{links[0]}>, and ({links[6]}). Also {links[0]}."));
	}

	[Fact]
	public void GarbageIsNeverALinkAndNeverAnException()
	{
		string[] garbage =
		[
			null!, "", "   ", "neuro-nat", "neuro-nat:", "neuro-nat://", "neuro-nat:///", "neuro-nat://neuron", "neuro-nat://neuron/",
			"neuro-nat:neuron/lab.example.com", "http://neuron/lab.example.com", "neuro-nat://bogus/lab.example.com", "neuro-nat://reboot/lab.example.com",
			"neuro-nat://neuron/lab..example.com", "neuro-nat://neuron/.lab", "neuro-nat://neuron/-lab.example.com", "neuro-nat://neuron/lab_example.com",
			"neuro-nat://neuron/lab.example.com/../../etc", "neuro-nat://neuron/lab.example.com/reboot", "neuro-nat://neuron/lab.example.com/events/more",
			"neuro-nat://neuron/lab%2Fexample.com", "neuro-nat://neuron/lab example.com", "neuro-nat://neuron/lab.example.com#top",
			"neuro-nat://neuron//lab.example.com", "neuro-nat://neuron/lab.example.com?at=2026-11-12T10:42:13Z",
			"neuro-nat://problem/lab.example.com", "neuro-nat://problem/lab.example.com/xyz", "neuro-nat://problem/lab.example.com/0123456789abcdef0",
			"neuro-nat://event/lab.example.com/0123456789abcdeg", "neuro-nat://event/lab.example.com/0123456789abcdef?at=yesterday",
			"neuro-nat://event/lab.example.com/0123456789abcdef?at=2026-11-12T10:42:13Z&at=2026-11-12T10:42:14Z",
			"neuro-nat://event/lab.example.com/0123456789abcdef?at", "neuro-nat://event/lab.example.com/0123456789abcdef?=x",
			"neuro-nat://node/lab.example.com", "neuro-nat://node/lab.example.com/..", "neuro-nat://node/lab.example.com/Root/%2e%2e",
			"neuro-nat://node/lab.example.com/%ZZ", "neuro-nat://node/lab.example.com/%4", "neuro-nat://node/lab.example.com/%C3%28",
			"neuro-nat://node/lab.example.com/%00", "neuro-nat://node/lab.example.com/a%0Ab", "neuro-nat://node/lab.example.com/a//b",
			"neuro-nat://node/lab.example.com/" + string.Join('/', Enumerable.Repeat("n", NatLink.MaxDepth + 1)),
			"neuro-nat://node/lab.example.com/" + new string('x', 1025),
			"neuro-nat://chat/lab.example.com", "neuro-nat://chat/kim@lab.example.com/phone", "neuro-nat://chat/a@b@lab.example.com",
			"neuro-nat://chat/kim%40lab.example.com%2Fres", "neuro-nat://chat/%3Cscript%3E@lab.example.com", "neuro-nat://room/@muc.lab.example.com",
			"neuro-nat://chat/kim@lab.example.com?at=2026-11-12T10:42:13Z",
			"neuro-nat://neuron/lab.example.com\u0000", "neuro-nat://neuron/läb.example.com", "neuro-nat://neuron/lab.example.com\n/events",
			"neuro-nat://neuron/" + new string('a', NatLink.MaxLength),
		];

		foreach (string g in garbage)
			Assert.False(NatLink.TryParse(g, out _), g);

		// Random text after the scheme: never a crash, and whatever does read is a link that writes itself out the same.
		Random random = new(4);
		const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-._~@%/?&=#:+ \u00e4\u0000\t";
		for (int i = 0; i < 5000; i++)
		{
			char[] tail = new char[random.Next(0, 80)];
			for (int j = 0; j < tail.Length; j++)
				tail[j] = alphabet[random.Next(alphabet.Length)];
			string kind = new[] { "neuron", "problem", "event", "node", "chat", "room", "x" }[random.Next(7)];
			string text = NatLink.Prefix + kind + "/" + new string(tail);
			if (NatLink.TryParse(text, out NatLink link))
				Assert.True(NatLink.TryParse(link.ToString(), out NatLink again) && again == link, text);
		}

		Assert.Empty(NatLink.FindAll(null));
		Assert.Empty(NatLink.FindAll(string.Concat(Enumerable.Repeat("neuro-nat://", 500))));
	}

	// ----- The message a second start sends to the running NAT -----

	[Fact]
	public async Task ThePipeCarriesShowOrALink()
	{
		static async Task<SingleInstance.Request?> Read(byte[] bytes)
		{
			using MemoryStream m = new(bytes);
			using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(5));
			return await SingleInstance.ReadAsync(m, cancel.Token);
		}

		// "Show" stays one byte, as it always was.
		Assert.Equal([SingleInstance.ShowMessage], SingleInstance.Message(null));
		Assert.Equal(new SingleInstance.Request(null), await Read([1]));

		// A link: a 2, its length in two bytes, then the link.
		const string link = "neuro-nat://neuron/gbg.lab.example.com/events";
		byte[] message = SingleInstance.Message(link);
		Assert.Equal(SingleInstance.LinkMessage, message[0]);
		Assert.Equal(link.Length, (message[1] << 8) | message[2]);
		Assert.Equal(link, System.Text.Encoding.ASCII.GetString(message, 3, message.Length - 3));
		Assert.Equal(new SingleInstance.Request(link), await Read(message));

		// What is not a NAT link only asks for the window.
		Assert.Equal([SingleInstance.ShowMessage], SingleInstance.Message("https://example.com"));
		Assert.Equal([SingleInstance.ShowMessage], SingleInstance.Message("neuro-nat://neuron/l\u00e4b"));
		Assert.Equal([SingleInstance.ShowMessage], SingleInstance.Message("neuro-nat://" + new string('a', NatLink.MaxLength)));

		// Anything else is ignored.
		static byte[] With(int length, string text) => [2, (byte)(length >> 8), (byte)length, .. System.Text.Encoding.Latin1.GetBytes(text)];
		Assert.Null(await Read([]));
		Assert.Null(await Read([0]));
		Assert.Null(await Read([3, 1, 2]));
		Assert.Null(await Read([2]));
		Assert.Null(await Read([2, 0]));
		Assert.Null(await Read(With(0, "")));
		Assert.Null(await Read(With(NatLink.MaxLength + 1, link)));
		Assert.Null(await Read(With(link.Length + 5, link)));
		Assert.Null(await Read(With(11, "https://x.y")));
		Assert.Null(await Read(With(14, "neuro-nat://\u00e4\u0001")));
	}

	// ----- The scheme in the registry: a scratch key, never the real Classes key -----

	[Fact]
	public void TheSchemeIsRegisteredAndRemovedForTheUser()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows only: the registry is a Windows feature.");
			return;
		}

		string path = @"Software\Neuro.Nat.Tests\" + Guid.NewGuid().ToString("N");
		try
		{
			using RegistryKey classes = Registry.CurrentUser.CreateSubKey(path, writable: true);
			using (RegistryKey other = classes.CreateSubKey("something-else"))
				other.SetValue(string.Empty, "kept");

			const string exe = @"C:\Users\ops\AppData\Local\NAT\current\NAT.exe";
			Assert.False(WindowsLinkScheme.IsRegistered(classes, exe));
			WindowsLinkScheme.Register(classes, exe);

			using (RegistryKey scheme = classes.OpenSubKey("neuro-nat")!)
			{
				Assert.Equal("URL:NAT link", scheme.GetValue(string.Empty));
				Assert.Equal(string.Empty, scheme.GetValue("URL Protocol"));
				Assert.Equal("\"" + exe + "\",0", scheme.OpenSubKey("DefaultIcon")!.GetValue(string.Empty));
				Assert.Equal("\"" + exe + "\" \"%1\"", scheme.OpenSubKey(@"shell\open\command")!.GetValue(string.Empty));
			}

			Assert.True(WindowsLinkScheme.IsRegistered(classes, exe));

			// Again, as after an update: still one scheme, pointing at NAT.
			WindowsLinkScheme.Register(classes, exe);
			Assert.True(WindowsLinkScheme.IsRegistered(classes, exe));

			// Uninstalling removes it, and only it.
			WindowsLinkScheme.Unregister(classes);
			Assert.Null(classes.OpenSubKey("neuro-nat"));
			Assert.Equal("kept", classes.OpenSubKey("something-else")!.GetValue(string.Empty));
			WindowsLinkScheme.Unregister(classes);
		}
		finally
		{
			Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
		}

		// A link handed over as an argument is found among the others.
		Assert.Equal("neuro-nat://neuron/lab.example.com", Program.LinkIn(["--tray", " neuro-nat://neuron/lab.example.com "]));
		Assert.Null(Program.LinkIn(["--tray", "https://example.com"]));
	}
}
