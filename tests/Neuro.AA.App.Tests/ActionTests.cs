using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.AA.App.Controls;
using Neuro.AA.App.ViewModels;
using Neuro.AA.App.Views;
using Neuro.AA.Engine;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Events;
using Neuro.AA.Engine.Sniffing;

namespace Neuro.AA.App.Tests;

/// <summary>
/// Actions everywhere: the right-click menu, the bar for several selected rows, copy and save,
/// the keyboard, and the rebuilt traffic view. Renders frames for review and checks the
/// behaviour behind them.
/// </summary>
public class ActionTests
{
	private sealed record Sample(MainWindowViewModel Vm, AccountSession Alpha, MonitorService Monitors, FleetService Fleet);

	/// <summary>What the app put on the clipboard, and the files it saved, instead of the real ones.</summary>
	private sealed class Desk
	{
		public string? Clipboard { get; private set; }
		public List<(string Name, string Content)> Saved { get; } = [];
		public List<string> Said { get; } = [];
		public string SaveAs { get; set; } = "csv";

		public Desk()
		{
			Ui.SetClipboard = text =>
			{
				this.Clipboard = text;
				return Task.CompletedTask;
			};
			Ui.SaveFile = (name, formats) =>
			{
				SaveFormat f = formats.FirstOrDefault(x => x.Extension == this.SaveAs) ?? formats[0];
				this.Saved.Add((name + "." + f.Extension, f.Content()));
				return Task.FromResult<string?>(name + "." + f.Extension);
			};
			Ui.Said += this.Said.Add;
		}
	}

	/// <summary>Sniffers hand their lines over on another thread: waits until they have.</summary>
	private static async Task Arrived(StreamSniffer sniffer, int total)
	{
		for (int i = 0; i < 300 && (sniffer.Snapshot().Count == 0 || sniffer.Snapshot()[^1].Sequence < total); i++)
			await Task.Delay(10);
	}

	private static string ReviewFolder()
	{
		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.AA.slnx")))
			d = d.Parent;

		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		return folder;
	}

	private static void Capture(TopLevel window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		frame!.Save(Path.Combine(ReviewFolder(), name + ".png"));
	}

	/// <summary>
	/// Saves the window with its open menu drawn where it opened. A menu is a window of its own,
	/// so the two frames are put together here.
	/// </summary>
	private static void CaptureWithMenu(Window window, ContextMenu menu, Point at, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		TopLevel? popup = TopLevel.GetTopLevel(menu);
		Assert.NotNull(popup);
		var menuFrame = popup!.CaptureRenderedFrame();
		Assert.NotNull(menuFrame);

		PixelSize size = frame!.PixelSize;
		double x = Math.Min(at.X, size.Width - menuFrame!.PixelSize.Width - 8);
		double y = Math.Min(at.Y, size.Height - menuFrame.PixelSize.Height - 8);
		using RenderTargetBitmap both = new(size);
		using (var ctx = both.CreateDrawingContext())
		{
			ctx.DrawImage(frame, new Rect(0, 0, size.Width, size.Height));
			ctx.DrawImage(menuFrame, new Rect(Math.Max(0, x), Math.Max(0, y), menuFrame.PixelSize.Width, menuFrame.PixelSize.Height));
		}

		both.Save(Path.Combine(ReviewFolder(), name + ".png"));
	}

	private static async Task<Sample> SampleAsync()
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "NeuroAA.Actions." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession alpha = await manager.AddAsync(new AccountProfile { Host = "localhost", Domain = "alpha.lab.local", UserName = "admin", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "beta.lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);

		EventHub hub = new();
		MonitorService monitors = new(manager, hub);
		FleetService fleet = new(monitors);

		void Neuron(AccountSession s, string jid, WatchState state, double cpu)
		{
			monitors.For(s.Profile.Id)!.Preview(new WatchedNeuron { AccountId = s.Profile.Id, BareJid = jid, State = state, HistoryState = HistoryState.Loaded, HistoryCount = 212 });
			fleet.Preview(new NeuronHealth
			{
				AccountId = s.Profile.Id,
				NeuronJid = jid,
				Software = "Waher.IoTGateway 3.11.1.0",
				Cpu = new SampleSeries(cpu, [Math.Max(0, cpu - 4), Math.Max(0, cpu - 1), cpu], "%"),
				AllocatedMemory = new SampleSeries(640, [640], "MB"),
				FreeMemory = new SampleSeries(5210, [5210], "MB"),
				FreeDisk = new SampleSeries(182.4, [182.4], "GB"),
				ReadAt = DateTime.Now,
			});
		}

		Neuron(alpha, "alpha@lab.local", WatchState.Live, 14);
		Neuron(alpha, "beta@lab.local", WatchState.Live, 88);
		Neuron(ops, "gamma@lab.local", WatchState.Live, 9);
		Neuron(ops, "delta@prod.example", WatchState.Offline, 0);

		// Synthetic sample data.
		DateTime now = DateTime.Now;
		void Event(int secondsAgo, SnifferDirection d, AccountSession s, string neuron, string text)
		{
			if (SniffedEventParser.Parse(new SnifferLine(0, now.AddSeconds(-secondsAgo), d, text), new EventSource(s.Profile.Id, neuron)) is NeuronEvent e)
				hub.Add(e);
		}

		for (int i = 0; i < 14; i++)
			Event(i * 40 + 5, SnifferDirection.Error, alpha, i % 3 == 0 ? "beta@lab.local" : "alpha@lab.local",
				$"SocketException: Connection reset by peer (beta.lab.local:5269)\n\nModule: Waher.Networking.XMPP.Server\nEventId: S2S\nStackTrace:    at Waher.Networking.BinaryTcpClient.ReadAsync()\n   at Waher.Networking.XMPP.Server.S2S.ServerConnection.ReadLoop()\nRemoteEndPoint: 10.0.0.{i + 3}:5269");
		for (int i = 0; i < 6; i++)
			Event(i * 90 + 20, SnifferDirection.Warning, alpha, "alpha@lab.local", $"Endpoint 203.0.113.{40 + i} blocked after 5 failed logins\n\nModule: Waher.Security.LoginMonitor\nEventId: Blocked");
		for (int i = 0; i < 5; i++)
			Event(i * 120 + 60, SnifferDirection.Error, ops, "gamma@lab.local", $"TimeoutException in ConcentratorServer.ReadSensorData (node Meter-{i + 11})\n\nModule: Waher.Things\nObject: Meter-{i + 11}");

		// Synthetic traffic on the first account.
		StreamSniffer t = alpha.Sniffer;
		t.Information("Connecting to localhost:5222");
		t.TransmitText("<?xml version='1.0'?><stream:stream to='alpha.lab.local' version='1.0' xml:lang='en' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'>");
		t.ReceiveText("<stream:features><starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'><required/></starttls><mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'><mechanism>SCRAM-SHA-256</mechanism><mechanism>SCRAM-SHA-1</mechanism></mechanisms></stream:features>");
		t.TransmitText("<starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>");
		t.ReceiveText("<proceed xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>");
		t.Information("Switching to TLS.");
		t.Warning("Server certificate not validated (trusted by account setting).");
		t.TransmitText("<iq type='get' id='3' xmlns='jabber:client'><query xmlns='jabber:iq:roster'/></iq>");
		t.ReceiveText("<iq type='result' id='3' to='admin@alpha.lab.local/aa'><query xmlns='jabber:iq:roster'><item jid='kim@alpha.lab.local' name='Kim' subscription='both'/><item jid='beta@lab.local' subscription='both'><group>Neurons</group></item></query></iq>");
		for (int i = 0; i < 12; i++)
		{
			t.TransmitText($"<iq type='get' id='{i + 10}' to='beta@lab.local'><getNodes xmlns='urn:ieee:iot:concentrator:1.0' src='MeteringTopology' id='Meter-{i}'/></iq>");
			t.ReceiveText($"<iq type='result' id='{i + 10}' from='beta@lab.local'><nodeInfos xmlns='urn:ieee:iot:concentrator:1.0'><nodeInfo id='Meter-{i}' src='MeteringTopology' state='None' hasChildren='false'/></nodeInfos></iq>");
		}
		t.Error("Unable to parse a stanza from beta@lab.local: unexpected end of file.");

		MainWindowViewModel vm = new(manager, fleet);
		await vm.StartAsync();
		vm.Fleet.Refresh();
		vm.Events.Flush();
		await Task.Delay(250);
		foreach (AccountItemViewModel a in vm.Accounts)
			a.Sniffer.Flush();

		return new Sample(vm, alpha, monitors, fleet);
	}

	private static MainWindow Window(Sample s, string theme, int width = 1440, int height = 900)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		MainWindow window = new() { DataContext = s.Vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		return window;
	}

	private static T Find<T>(Visual root, Func<T, bool>? where = null)
		where T : Visual
		=> root.GetVisualDescendants().OfType<T>().First(v => v.IsEffectivelyVisible && (where is null || where(v)));

	private static Point Center(Visual v, Visual relativeTo)
		=> v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), relativeTo) ?? default;

	private static void RightClick(Window window, Point at)
	{
		window.MouseMove(at);
		window.MouseDown(at, MouseButton.Right);
		window.MouseUp(at, MouseButton.Right);
		Dispatcher.UIThread.RunJobs();
	}

	private static List<string> Texts(ContextMenu menu)
		=> [.. menu.Items.Select(i => i is MenuItem m ? m.Header?.ToString() ?? string.Empty : "-")];

	// ----- The pieces every screen shares -----

	[Fact]
	public void MenusKeepTheirGroupsInTheSameOrder()
	{
		ActionList list = [];
		list.Do("Resolve", () => { });
		list.GoTo("Events", () => { });
		list.Copy("Copy title", () => "x", "the title");
		list.Open("Show its events", () => { });
		list.Filter("Only this Neuron", () => { });

		// However the screen adds them, the menu is Open, Copy, Filter, Go to, Do.
		List<ItemAction> ordered = [.. list.GroupBy(a => a.Group).OrderBy(g => g.Key).SelectMany(g => g)];
		Assert.Equal(["Show its events", "Copy title", "Only this Neuron", "Events", "Resolve"], ordered.Select(a => a.Text));
	}

	[Fact]
	public void TidyXmlAddsOnlyLineBreaksAndSpaces()
	{
		string raw = "<iq type='result' id='3'><query xmlns='jabber:iq:roster'><item jid='kim@lab.local' name='Kim &amp; Co'/><item jid='b@lab.local'><group>Neurons</group></item></query></iq>";
		string? tidy = XmlTidy.Tidy(raw);
		Assert.NotNull(tidy);
		Assert.Equal(
			"<iq type='result' id='3'>\n  <query xmlns='jabber:iq:roster'>\n    <item jid='kim@lab.local' name='Kim &amp; Co'/>\n    <item jid='b@lab.local'>\n      <group>Neurons</group>\n    </item>\n  </query>\n</iq>",
			tidy);

		// Nothing but white space was added, so the tidy text is still the same XML.
		Assert.Equal(raw, string.Concat(tidy!.Split('\n').Select(l => l.TrimStart())));

		// An XMPP stream header is never closed. It is tidied all the same, not refused.
		Assert.Null(XmlTidy.Tidy("Connecting to localhost:5222"));
		Assert.Null(XmlTidy.Tidy("<proceed xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>"));
		Assert.Contains("\n", XmlTidy.Tidy("<stream:stream to='a'><stream:features><bind/></stream:features>")!);

		// A ">" inside an attribute value does not end the tag.
		Assert.Equal("<a t='x>y'>\n  <b/>\n</a>", XmlTidy.Tidy("<a t='x>y'><b/></a>"));
	}

	[Fact]
	public void TablesComeOutOfMarkdownAsCsv()
	{
		string markdown = "Clients right now:\n\n| Client | Since | Note |\n|:--|:--|:--|\n| `kim@lab.local` | 08:12 | says \"hi\", twice |\n| ops@lab.local | 09:40 | a \\| b |\n\nDone.";
		string? csv = Export.MarkdownTablesAsCsv(markdown);
		Assert.NotNull(csv);
		string[] lines = csv!.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal("Client,Since,Note", lines[0]);
		Assert.Equal("kim@lab.local,08:12,\"says \"\"hi\"\", twice\"", lines[1]);
		Assert.Equal("ops@lab.local,09:40,a | b", lines[2]);
		Assert.Null(Export.MarkdownTablesAsCsv("No table here."));
	}

	// ----- Traffic -----

	[AvaloniaFact]
	public async Task TrafficFiltersFindsCopiesAndSaysWhatItDropped()
	{
		Desk desk = new();
		StreamSniffer sniffer = new("test");
		using SnifferViewModel vm = new(sniffer, "admin@alpha.lab.local");

		sniffer.Information("Connecting");
		sniffer.TransmitText("<iq type='get' id='1'><query xmlns='jabber:iq:roster'/></iq>");
		sniffer.ReceiveText("<iq type='result' id='1'><query xmlns='jabber:iq:roster'><item jid='kim@lab.local'/></query></iq>");
		sniffer.Error("Could not parse");
		await Arrived(sniffer, 4);
		vm.Flush();
		Assert.Equal(4, vm.Lines.Count);
		Assert.False(vm.IsEmpty);

		// The four buttons each hide one kind of line.
		vm.ShowInformation = false;
		vm.ShowProblems = false;
		Assert.Equal(2, vm.Lines.Count);
		Assert.True(vm.IsFiltered);
		vm.ShowOnly(TrafficKind.Received);
		Assert.Single(vm.Lines);
		vm.ShowOnly(null);
		Assert.Equal(4, vm.Lines.Count);

		// Find marks every match and steps through them, wrapping around.
		vm.FindText = "roster";
		Assert.Equal("2 matches", vm.FindStatus);
		Assert.Equal(2, vm.Lines.Count(l => l.IsMatch));
		vm.FindNextCommand.Execute(null);
		Assert.Equal("1 of 2", vm.FindStatus);
		Assert.True(vm.Lines[1].IsCurrentMatch);
		vm.FindNextCommand.Execute(null);
		vm.FindNextCommand.Execute(null);
		Assert.Equal("1 of 2", vm.FindStatus);
		vm.FindNextCommand.Execute("back");
		Assert.Equal("2 of 2", vm.FindStatus);
		vm.FindText = "nothing like it";
		Assert.Equal("No match", vm.FindStatus);
		vm.FindText = string.Empty;

		// Copy: one line is just its text; several carry their time and direction.
		vm.Selected = [vm.Lines[1]];
		vm.CopyCommand.Execute(null);
		Assert.Equal("<iq type='get' id='1'><query xmlns='jabber:iq:roster'/></iq>" + Environment.NewLine, desk.Clipboard);
		vm.Selected = [vm.Lines[1], vm.Lines[2]];
		vm.CopyCommand.Execute(null);
		Assert.Contains("\tSENT\t<iq type='get'", desk.Clipboard);
		Assert.Contains("\tGOT\t<iq type='result'", desk.Clipboard);
		Assert.Contains("Copied 2 lines", desk.Said);

		// The right-click menu offers the tidy XML of what is selected.
		IReadOnlyList<ItemAction> actions = vm.ActionsFor([vm.Lines[2]]);
		ItemAction tidy = actions.First(a => a.Text == "Copy as tidy XML");
		await tidy.RunAsync();
		Assert.StartsWith("<iq type='result' id='1'>\n  <query", desk.Clipboard);
		Assert.Contains(actions, a => a.Text == "Show only received" && a.Group == ActionGroup.Filter);

		// Save writes the sniffer file format, which the IoT Gateway tools read.
		desk.SaveAs = "xml";
		vm.Selected = [];
		vm.SaveCommand.Execute(null);
		(string name, string content) = desk.Saved[^1];
		Assert.StartsWith("Traffic-admin@alpha.lab.local", name);
		Assert.Contains("<SnifferOutput xmlns=\"http://waher.se/Schema/SnifferOutput.xsd\">", content);
		Assert.Contains("<Tx timestamp=", content);
		Assert.Contains("<Row>&lt;iq type='get' id='1'&gt;", content);
		Assert.Equal(4, System.Xml.Linq.XDocument.Parse(content).Root!.Elements().Count());

		// The limit is visible: older lines are dropped, and the view says how many.
		for (int i = 0; i < 2500; i++)
			sniffer.TransmitText("<presence id='" + i + "'/>");
		await Arrived(sniffer, 2504);
		vm.Flush();
		Assert.Equal(2000, vm.Lines.Count);
		Assert.Equal(504, vm.Dropped);
		Assert.Contains("504 older lines were dropped", vm.LimitNote);
		vm.SetLimitCommand.Execute(10000);
		for (int i = 0; i < 100; i++)
			sniffer.TransmitText("<presence/>");
		await Arrived(sniffer, 2604);
		vm.Flush();
		Assert.Equal(2100, vm.Lines.Count);

		// Scrolled up: new lines are counted, not chased.
		vm.JumpToLatestCommand.Execute(null);
		vm.IsFollowing = false;
		sniffer.ReceiveText("<message/>");
		sniffer.ReceiveText("<message/>");
		await Arrived(sniffer, 2606);
		vm.Flush();
		Assert.Equal("Jump to latest · 2 new lines", vm.NewText);
		bool jumped = false;
		vm.ScrollToLatest += (_, _) => jumped = true;
		vm.JumpToLatestCommand.Execute(null);
		Assert.True(jumped);
		Assert.False(vm.HasNew);

		// Paused: nothing moves, and nothing is lost.
		vm.IsPaused = true;
		sniffer.ReceiveText("<message id='late'/>");
		await Arrived(sniffer, 2607);
		vm.Flush();
		Assert.Equal(2102, vm.Lines.Count);
		vm.IsPaused = false;
		await Arrived(sniffer, 2607);
		vm.Flush();
		Assert.Equal(2103, vm.Lines.Count);
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task RendersTrafficWithFindAnOpenLineAndASelection(string theme)
	{
		_ = new Desk();
		Sample s = await SampleAsync();
		s.Vm.Workspace = WorkspaceKind.Accounts;
		s.Vm.SelectedAccount = s.Vm.Accounts[0];
		MainWindow window = Window(s, theme);
		string t = theme.ToLowerInvariant();

		SnifferViewModel traffic = s.Vm.Accounts[0].Sniffer;
		Assert.Equal(34, traffic.Lines.Count);
		Capture(window, $"traffic-{t}-1440");

		// Find, with one line opened as tidy XML.
		traffic.FindText = "roster";
		traffic.FindNextCommand.Execute(null);
		traffic.Lines[8].IsOpen = true;
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"traffic-find-{t}-1440");
		Assert.Equal("1 of 2", traffic.FindStatus);
		Assert.Contains("\n    <item jid='kim@alpha.lab.local'", traffic.Lines[8].Shown);

		// Several lines selected: the bar appears with Copy and Save.
		traffic.FindText = string.Empty;
		ListBox list = Find<ListBox>(window, l => l.Classes.Contains("traffic"));
		list.Selection.Clear();
		list.Selection.SelectRange(9, 14);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(6, traffic.Selected.Count);
		ActionBar bar = Find<ActionBar>(window);
		Assert.True(bar.IsVisible);
		Capture(window, $"traffic-selected-{t}-1440");

		// Right-click on a selected line keeps the selection and opens the menu for all of it.
		ListBoxItem row = (ListBoxItem)list.ContainerFromIndex(11)!;
		Point at = Center(row, window);
		RightClick(window, at);
		ContextMenu menu = ActionMenu.Last!;
		Assert.True(menu.IsOpen);
		Assert.Equal(6, traffic.Selected.Count);
		List<string> texts = Texts(menu);
		Assert.Equal("Copy 6 lines", texts[0]);
		Assert.Contains("Copy with times", texts);
		Assert.Contains("Save 6 lines…", texts);
		Assert.Contains("Pause", texts);
		CaptureWithMenu(window, menu, at, $"traffic-menu-{t}-1440");
		menu.Close();

		// Narrow window: the toolbar still fits.
		window.Width = 960;
		window.Height = 680;
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"traffic-{t}-960");
		window.Close();
	}

	// ----- Events -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task EventsCanBeSelectedCopiedSavedAndHidden(string theme)
	{
		Desk desk = new();
		Sample s = await SampleAsync();
		s.Vm.Workspace = WorkspaceKind.Events;
		s.Vm.Events.SeverityFilter = SeverityFilter.All;
		MainWindow window = Window(s, theme);
		string t = theme.ToLowerInvariant();
		EventsViewModel events = s.Vm.Events;
		Assert.Equal(25, events.Stream.Count);

		// Right-click one event: it becomes the selection and the menu is about it.
		ListBox stream = Find<ListBox>(window, l => l.Name == "StreamList");
		ListBoxItem row = (ListBoxItem)stream.ContainerFromIndex(2)!;
		Point at = Center(row, window);
		RightClick(window, at);
		ContextMenu menu = ActionMenu.Last!;
		Assert.True(menu.IsOpen);
		Assert.Same(events.Stream[2], events.SelectedEvent);
		List<string> texts = Texts(menu);
		Assert.Equal("Copy message", texts[0]);
		Assert.Contains("Copy as Markdown", texts);
		Assert.Contains("Save…", texts);
		Assert.Contains("Hide events like this", texts);
		Assert.Contains("Share to chat…", texts);
		Assert.Contains(texts, x => x.StartsWith("Go to "));

		// The groups come in their fixed order, with a line between them.
		Assert.True(texts.IndexOf("Copy message") < texts.IndexOf("Hide events like this"));
		Assert.True(texts.IndexOf("Hide events like this") < texts.FindIndex(x => x.StartsWith("Go to ")));
		Assert.True(texts.FindIndex(x => x.StartsWith("Go to ")) < texts.IndexOf("Share to chat…"));
		Assert.Equal(3, texts.Count(x => x == "-"));
		CaptureWithMenu(window, menu, at, $"events-menu-{t}-1440");
		menu.Close();

		// Ctrl+C on the list copies the message of the selected event, and says so.
		Assert.True(row.Focus());
		window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(events.Stream[2].Message, desk.Clipboard);
		Assert.Equal("Copied the message", s.Vm.Flash);

		// Several events: the bar, Ctrl+C for rows, and Save as a spreadsheet.
		stream.Selection.SelectRange(1, 4);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(4, events.SelectedEvents.Count);
		ActionBar bar = Find<ActionBar>(window, b => b.Name == "StreamBar");
		Assert.True(bar.IsVisible);
		Capture(window, $"events-selected-{t}-1440");

		window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
		Dispatcher.UIThread.RunJobs();
		string[] rows = desk.Clipboard!.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(5, rows.Length);
		Assert.StartsWith("Time\tLevel\tNeuron\tWhere\tMessage", rows[0]);
		Assert.Equal("Copied 4 events", s.Vm.Flash);

		window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
		Dispatcher.UIThread.RunJobs();
		Assert.EndsWith(".csv", desk.Saved[^1].Name);
		Assert.Equal(5, desk.Saved[^1].Content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
		Assert.StartsWith("Saved Events", s.Vm.Flash);

		// Escape clears the selection, and the bar goes.
		window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.False(bar.IsVisible);

		// Hide events like this: every event of that kind leaves the stream until asked back.
		NeuronEvent socket = events.Stream.First(e => e.Message.StartsWith("SocketException"));
		await events.ActionsFor([socket]).First(a => a.Text == "Hide events like this").RunAsync();
		Assert.DoesNotContain(events.Stream, e => e.Fingerprint == socket.Fingerprint);
		Assert.True(events.HasHidden);
		Capture(window, $"events-hidden-{t}-1440");
		events.ShowHiddenCommand.Execute(null);
		Assert.Equal(25, events.Stream.Count);

		// Go to: from an event to its Neuron on another screen.
		ItemAction goTo = events.ActionsFor([socket]).First(a => a.Text.StartsWith("Go to "));
		Assert.Contains(goTo.Children!, a => a.Text == "Console");
		await goTo.Children!.First(a => a.Text == "Its page").RunAsync();
		Assert.Equal(WorkspaceKind.Neuron, s.Vm.Workspace);
		Assert.Equal(socket.Source.NeuronJid, s.Vm.Page?.BareJid);
		Assert.Equal(socket.Source.NeuronJid, s.Vm.Fleet.SelectedRow?.BareJid);
		window.Close();
	}

	[AvaloniaFact]
	public async Task SeveralIssuesTakeOneAnswer()
	{
		_ = new Desk();
		Sample s = await SampleAsync();
		s.Vm.Workspace = WorkspaceKind.Events;
		MainWindow window = Window(s, "Light");
		EventsViewModel events = s.Vm.Events;
		Assert.Equal(3, events.Issues.Count);

		ListBox issues = Find<ListBox>(window, l => l.Name == "IssueList");
		issues.Selection.SelectRange(0, 1);
		Dispatcher.UIThread.RunJobs();
		ActionBar bar = Find<ActionBar>(window, b => b.Name == "IssueBar");
		Assert.True(bar.IsVisible);
		Capture(window, "issues-selected-light-1440");

		List<object> picked = [events.Issues[0], events.Issues[1]];
		IReadOnlyList<ItemAction> actions = events.ActionsFor(picked);
		Assert.Contains(actions, a => a.Text == "Acknowledge" && a.InBar);
		Assert.Contains(actions, a => a.Text == "Mark fixed" && a.InBar);
		await actions.First(a => a.Text == "Mark fixed").RunAsync();
		events.Flush();
		await Task.Delay(1100);
		events.Flush();
		Assert.Single(events.Issues);
		Assert.Equal("2 problems marked fixed", s.Vm.Flash);

		// One click, and easy to do to the wrong rows: the note offers to take it back.
		Assert.True(s.Vm.HasFlashUndo);
		Capture(window, "problems-undo-light-1440");
		s.Vm.UndoFlashCommand.Execute(null);
		events.Flush();
		await Task.Delay(1100);
		events.Flush();
		Assert.Equal(3, events.Issues.Count);
		Assert.Equal("Taken back", s.Vm.Flash);
		Assert.False(s.Vm.HasFlashUndo);
		window.Close();
	}

	// ----- Neurons -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task ANeuronHasTheSameActionsWhereverItIsShown(string theme)
	{
		Desk desk = new();
		Sample s = await SampleAsync();
		s.Vm.Workspace = WorkspaceKind.Fleet;
		MainWindow window = Window(s, theme);
		string t = theme.ToLowerInvariant();
		FleetViewModel fleet = s.Vm.Fleet;
		Assert.Equal(4, fleet.Shown.Count);

		ListBox list = Find<ListBox>(window, l => l.Name == "List" && l.DataContext is FleetViewModel);
		ListBoxItem row = (ListBoxItem)list.ContainerFromIndex(1)!;
		Point at = Center(row, window);
		RightClick(window, at);
		ContextMenu menu = ActionMenu.Last!;
		List<string> texts = Texts(menu);
		Assert.Equal("Open", texts[0]);
		foreach (string expected in new[] { "Copy address", "Copy version", "Copy status summary", "Its events", "Sources", "Console", "Software", "Identity", "Map", "Read its health now", "Check for updates", "Reboot…" })
			Assert.Contains(expected, texts);
		Assert.DoesNotContain("Its page", texts);
		CaptureWithMenu(window, menu, at, $"neurons-menu-{t}-1440");
		menu.Close();

		// The same Neuron on a chip in the top bar and in the Console's list offers the same moves.
		string jid = fleet.Shown[1].BareJid;
		List<string> onRow = [.. fleet.ActionsFor([fleet.Shown[1]]).Where(a => a.Group == ActionGroup.GoTo).Select(a => a.Text)];
		s.Vm.ShowConsoleCommand.Execute(null);
		ConsoleTargetViewModel target = s.Vm.Console.Targets.First(x => x.BareJid == jid);
		List<string> onConsole = [.. s.Vm.Console.ActionsFor([target]).Where(a => a.Group == ActionGroup.GoTo).Select(a => a.Text)];
		Assert.Equal(onRow, onConsole.Where(x => x != "Its page"));
		NeuronChipViewModel chip = s.Vm.NeuronChips.First(c => c.BareJid == jid);
		Assert.Contains(s.Vm.ActionsFor([chip]), a => a.Text == "Copy status summary");

		// Copy status summary gives a few readable lines.
		s.Vm.ShowFleetCommand.Execute(null);
		await fleet.ActionsFor([fleet.Shown[1]]).First(a => a.Text == "Copy status summary").RunAsync();
		Assert.StartsWith(jid + ": ", desk.Clipboard);
		Assert.Contains("Version: Waher.IoTGateway 3.11.1.0", desk.Clipboard);

		// Several Neurons: one command for all of them, from the bar.
		list.Selection.SelectRange(0, 2);
		Dispatcher.UIThread.RunJobs();
		ActionBar bar = Find<ActionBar>(window);
		Assert.True(bar.IsVisible);
		Capture(window, $"neurons-selected-{t}-1440");
		List<object> three = [fleet.Shown[0], fleet.Shown[1], fleet.Shown[2]];
		await fleet.ActionsFor(three).First(a => a.Text == "Run a command on these…").RunAsync();
		Assert.Equal(WorkspaceKind.Console, s.Vm.Workspace);
		Assert.Equal(3, s.Vm.Console.Targets.Count(x => x.IsChecked));

		// Reboot goes through the Neuron's own Console, which asks for the Neuron's name before it runs.
		await s.Vm.NeuronActions(jid).First(a => a.Text == "Reboot…").RunAsync();
		Assert.Equal(WorkspaceKind.NeuronConsole, s.Vm.Workspace);
		Assert.Equal(jid, s.Vm.Page?.BareJid);
		Assert.Equal("Reboot", s.Vm.PageConsole?.CommandText);
		Assert.Single(s.Vm.PageConsole!.Targets, x => x.IsChecked);
		Assert.Equal(jid, s.Vm.PageConsole.Targets.Single(x => x.IsChecked).BareJid);

		// The filter box narrows the list, and a filter that hides a Neuron is cleared to show it.
		s.Vm.ShowFleetCommand.Execute(null);
		fleet.FilterText = "gamma";
		Assert.Single(fleet.Shown);
		Assert.Equal(4, fleet.Rows.Count);
		fleet.Select("alpha@lab.local");
		Assert.Equal(string.Empty, fleet.FilterText);
		Assert.Equal("alpha@lab.local", fleet.SelectedRow?.BareJid);
		window.Close();
	}

	// ----- Keyboard -----

	[AvaloniaFact]
	public async Task TheKeyboardGoesPlacesAndFindsTheFilterBox()
	{
		_ = new Desk();
		Sample s = await SampleAsync();
		MainWindow window = Window(s, "Light");

		window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Control);
		Assert.Equal(WorkspaceKind.Events, s.Vm.Workspace);
		window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Control);
		Assert.Equal(PlaceKind.Neurons, s.Vm.Place);
		window.KeyPressQwerty(PhysicalKey.Digit5, RawInputModifiers.Control);
		Assert.Equal(WorkspaceKind.Console, s.Vm.Workspace);
		window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Control);
		Assert.Equal(WorkspaceKind.Attention, s.Vm.Workspace);
		window.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
		Assert.Equal(WorkspaceKind.Settings, s.Vm.Workspace);
		Dispatcher.UIThread.RunJobs();
		Find<SettingsView>(window).GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
		Dispatcher.UIThread.RunJobs();
		Capture(window, "settings-keyboard-light-1440");

		// Ctrl+F lands in the filter box of the screen in front.
		window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Control);
		Dispatcher.UIThread.RunJobs();
		window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
		window.KeyTextInput("timeout");
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("timeout", s.Vm.Events.SearchText);

		s.Vm.Workspace = WorkspaceKind.Accounts;
		s.Vm.SelectedAccount = s.Vm.Accounts[0];
		Dispatcher.UIThread.RunJobs();
		window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
		window.KeyTextInput("starttls");
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("starttls", s.Vm.Accounts[0].Sniffer.FindText);
		window.Close();
	}

	// ----- Console -----

	[AvaloniaFact]
	public async Task AConsoleAnswerCanBeCopiedAsATableAndRunAgain()
	{
		Desk desk = new();
		Sample s = await SampleAsync();
		ConsoleViewModel console = s.Vm.Console;
		console.SyncTargets();
		ConsoleTargetViewModel target = console.Targets.First(x => x.BareJid == "alpha@lab.local");

		ConsoleRunViewModel run = new("Clients", DateTime.Now);
		ConsoleResultViewModel result = new(new Engine.Chat.ConsoleEntry("t1", "Clients", null, false), target.Label, target.AccountId, target.BareJid) { Run = run };
		result.Markdown = "| Client | Since |\n|:--|:--|\n| kim@lab.local | 08:12 |\n| ops@lab.local | 09:40 |";
		run.Results.Add(result);
		console.Runs.Add(run);

		IReadOnlyList<ItemAction> actions = console.ActionsFor([result]);
		Assert.Equal("Copy answer", actions[0].Text);
		await actions.First(a => a.Text == "Copy table as CSV").RunAsync();
		Assert.StartsWith("Client,Since" + Environment.NewLine + "kim@lab.local,08:12", desk.Clipboard);
		await actions.First(a => a.Text == "Copy table for a spreadsheet").RunAsync();
		Assert.StartsWith("Client\tSince", desk.Clipboard);
		Assert.Contains(actions, a => a.Text.StartsWith("Run again on "));
		Assert.Contains(actions, a => a.Text.StartsWith("Go to ") && a.Children!.Any(c => c.Text == "Sources"));

		await actions.First(a => a.Text == "Put the command back in the box").RunAsync();
		Assert.Equal("Clients", console.CommandText);

		desk.SaveAs = "csv";
		await actions.First(a => a.Text == "Save…").RunAsync();
		Assert.StartsWith("Clients-", desk.Saved[^1].Name);
		Assert.EndsWith(".csv", desk.Saved[^1].Name);
	}
}
