using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.AA.App.Controls;
using Neuro.AA.App.ViewModels;
using Neuro.AA.App.Views;
using Neuro.AA.Engine.Alerts;
using Neuro.AA.Engine.Events;
using Sample = Neuro.AA.App.Tests.GettingAroundTests.Sample;

namespace Neuro.AA.App.Tests;

/// <summary>
/// Sizes and windows: the text size, compact rows, the lines between panes, the order of the
/// Neurons list, screens in windows of their own that come back at start, and the Wall view.
/// Renders frames for review.
/// </summary>
public class SizesAndWindowsTests
{
	private const string Lab = "lab.tagroot.io";
	private const string Gbg = "gbg.lab.tagroot.io";
	private const string Sto = "sto.lab.tagroot.io";
	private const string Prod = "prod.example.com";
	private const string Backup = "backup.prod.example.com";

	private static string Folder() => Path.Combine(Path.GetTempPath(), "NeuroAA.Sizes." + Guid.NewGuid().ToString("N"));

	/// <summary>The sample, with Settings reading and writing the folder's ui.json as the app does.</summary>
	private static async Task<(Sample Sample, AlertService Alerts)> SampleAsync(string dir)
	{
		Sample s = await GettingAroundTests.SampleAsync(dir);
		AlertService alerts = new(s.Monitors, new AlertSettings());
		s.Vm.Settings.Attach(alerts, new JsonAlertSettingsStore(Path.Combine(dir, "alerts.json")), new JsonUiSettingsStore(Path.Combine(dir, "ui.json")));
		Dispatcher.UIThread.RunJobs();
		return (s, alerts);
	}

	private static UiSettings Saved(string dir) => new JsonUiSettingsStore(Path.Combine(dir, "ui.json")).Load();

	private static Border PaneOf(Visual root, string key)
		=> root.GetVisualDescendants().OfType<Border>().Single(b => Pane.GetKey(b) == key && b.IsEffectivelyVisible);

	private static MainWindowViewModel Another(Sample s)
		=> new(s.Manager, s.Fleet, s.Chat, null, null, s.PubSub, s.Software, primary: s.Vm);

	// ----- Text size and rows -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task TheTextSizeScalesTheWholeWindow(string theme)
	{
		string dir = Folder();
		(Sample s, AlertService alerts) = await SampleAsync(dir);
		using Sample _ = s;
		using AlertService __ = alerts;
		MainWindowViewModel vm = s.Vm;
		string t = theme.ToLowerInvariant();
		vm.ShowFleetCommand.Execute(null);
		MainWindow window = GettingAroundTests.Window(s, theme);
		LayoutTransformControl zoom = window.GetVisualDescendants().OfType<LayoutTransformControl>().First(z => z.Name == "Zoom");

		Assert.Equal(100, vm.Settings.TextSize);
		Assert.Null(zoom.LayoutTransform);
		Grid root = window.FindControl<Grid>("Root")!;
		double normalWidth = root.Bounds.Width;
		Assert.Equal(1440, normalWidth, 0.5);

		// Larger: everything scales with the text, so the window lays out as if it were narrower.
		vm.Settings.SetTextSize(130);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.Settings.IsTextLarger);
		Assert.Equal(1.3, Assert.IsType<ScaleTransform>(zoom.LayoutTransform).ScaleX, 3);
		Assert.Equal(1440 / 1.3, root.Bounds.Width, 1.0);
		Assert.Contains("maxw1180", root.Classes);
		Assert.Equal(130, Saved(dir).TextSize);
		GettingAroundTests.Capture(window, $"sizes-text-larger-{t}");

		// Ctrl+minus: one step down, and it says so.
		vm.Settings.StepTextSize(-1);
		Assert.Equal(115, vm.Settings.TextSize);
		Assert.Contains("Text size 115 %", s.Said);

		vm.Settings.SetTextSize(90);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.Settings.IsTextSmall);
		Assert.Equal(1440 / 0.9, root.Bounds.Width, 1.0);
		GettingAroundTests.Capture(window, $"sizes-text-small-{t}");

		// Steps stop at the ends, and a size that is not on offer is not taken.
		vm.Settings.StepTextSize(-1);
		Assert.Equal(90, vm.Settings.TextSize);
		vm.Settings.SetTextSize(250);
		Assert.Equal(90, vm.Settings.TextSize);

		// Compact rows: more of a list fits.
		vm.Settings.SetTextSize(100);
		vm.ShowEventsCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		ListBoxItem row = window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.IsEffectivelyVisible && i.FindAncestorOfType<ListBox>()?.Classes.Contains("rows") == true);
		double tall = row.Bounds.Height;
		GettingAroundTests.Capture(window, $"sizes-rows-comfortable-{t}");

		vm.Settings.CompactRows = true;
		Dispatcher.UIThread.RunJobs();
		Assert.Contains("compact", root.Classes);
		Assert.True(row.Bounds.Height < tall, $"compact {row.Bounds.Height} should be less than {tall}");
		Assert.True(Saved(dir).CompactRows);
		GettingAroundTests.Capture(window, $"sizes-rows-compact-{t}");

		// Settings shows both choices.
		vm.ShowSettingsCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		Button reset = GettingAroundTests.Shown<Button>(window, "ResetLayoutButton");
		reset.BringIntoView();
		Dispatcher.UIThread.RunJobs();
		GettingAroundTests.Capture(window, $"sizes-settings-{t}");
		window.Close();
	}

	// ----- The lines between panes -----

	[AvaloniaFact]
	public async Task TheLineBetweenPanesCanBeDraggedAndIsRemembered()
	{
		string dir = Folder();
		double own;
		{
			(Sample s, AlertService alerts) = await SampleAsync(dir);
			using Sample _ = s;
			using AlertService __ = alerts;
			MainWindowViewModel vm = s.Vm;
			vm.ShowAttentionCommand.Execute(null);
			MainWindow window = GettingAroundTests.Window(s, "Light");

			Border pane = PaneOf(window, "inbox");
			own = pane.Bounds.Width;
			Assert.False(vm.Settings.HasOwnLayout);

			// The line is at the pane's edge towards the rest of the screen, a few pixels to either side.
			Assert.True(Pane.IsOnLine(pane, new Point(own - 2, 200)));
			Assert.True(Pane.IsOnLine(pane, new Point(own + 3, 200)));
			Assert.False(Pane.IsOnLine(pane, new Point(own - 30, 200)));

			Pane.Resize(pane, 560);
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(560, pane.Bounds.Width, 0.5);
			Assert.Equal(560, vm.Settings.Width("inbox"));
			Assert.True(vm.Settings.HasOwnLayout);
			Assert.Equal(560, Saved(dir).Widths["inbox"]);
			GettingAroundTests.Capture(window, "sizes-pane-dragged-light");

			// It cannot be dragged so far that nothing is left of the rest, or so small that the list is gone.
			Pane.Resize(pane, 5000);
			Dispatcher.UIThread.RunJobs();
			Assert.True(pane.Bounds.Width <= window.Bounds.Width - 280, $"{pane.Bounds.Width}");
			Pane.Resize(pane, 20);
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(200, pane.Bounds.Width, 0.5);

			Pane.Resize(pane, 560);
			window.Close();
		}

		// The next run: the pane is as wide as it was left.
		{
			(Sample s, AlertService alerts) = await SampleAsync(dir);
			using Sample _ = s;
			using AlertService __ = alerts;
			MainWindowViewModel vm = s.Vm;
			vm.ShowAttentionCommand.Execute(null);
			MainWindow window = GettingAroundTests.Window(s, "Light");
			Border pane = PaneOf(window, "inbox");
			Assert.Equal(560, pane.Bounds.Width, 0.5);

			// In a window too small for it, it gives way, and comes back when there is room again.
			window.Width = 760;
			Dispatcher.UIThread.RunJobs();
			Assert.True(pane.Bounds.Width <= 760 - 280, $"{pane.Bounds.Width}");
			window.Width = 1440;
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(560, pane.Bounds.Width, 0.5);

			// "Put the layout back" in Settings: every pane has its own width again.
			vm.Settings.ResetLayoutCommand.Execute(null);
			Dispatcher.UIThread.RunJobs();
			Assert.False(vm.Settings.HasOwnLayout);
			Assert.Equal(own, pane.Bounds.Width, 0.5);
			Assert.Empty(Saved(dir).Widths);
			Assert.Contains("The layout is back to how it came", s.Said);
			window.Close();
		}
	}

	// ----- The Neurons list -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task TheNeuronsListSortsByItsColumns(string theme)
	{
		string dir = Folder();
		{
			(Sample s, AlertService alerts) = await SampleAsync(dir);
			using Sample _ = s;
			using AlertService __ = alerts;
			MainWindowViewModel vm = s.Vm;
			vm.ShowFleetCommand.Execute(null);
			MainWindow window = GettingAroundTests.Window(s, theme);

			// As it comes: by name.
			Assert.Null(vm.Fleet.Sort);
			Assert.Equal("↑", vm.Fleet.NameArrow);

			// The busiest first. A Neuron that says nothing is last, whichever way round.
			vm.Fleet.SortByCommand.Execute("cpu");
			Dispatcher.UIThread.RunJobs();
			Assert.Equal([Gbg, Prod, Sto, Lab, Backup], vm.Fleet.Shown.Select(r => r.BareJid));
			Assert.Equal("↓", vm.Fleet.CpuArrow);
			Assert.Equal(string.Empty, vm.Fleet.NameArrow);
			Assert.Equal("cpu-", Saved(dir).NeuronSort);
			GettingAroundTests.Capture(window, $"sizes-neurons-sorted-{theme.ToLowerInvariant()}");

			vm.Fleet.SortByCommand.Execute("cpu");
			Assert.Equal([Lab, Sto, Prod, Gbg, Backup], vm.Fleet.Shown.Select(r => r.BareJid));
			Assert.Equal("↑", vm.Fleet.CpuArrow);

			// Most errors first.
			vm.Fleet.SortByCommand.Execute("errors");
			Assert.Equal([Gbg, Lab], vm.Fleet.Shown.Take(2).Select(r => r.BareJid));
			Assert.Equal("errors-", Saved(dir).NeuronSort);
			window.Close();
		}

		// The next run: the list is in the order it was left in.
		{
			(Sample s, AlertService alerts) = await SampleAsync(dir);
			using Sample _ = s;
			using AlertService __ = alerts;
			s.Vm.Fleet.Refresh();
			Assert.Equal("errors-", s.Vm.Fleet.Sort);
			Assert.Equal([Gbg, Lab], s.Vm.Fleet.Shown.Take(2).Select(r => r.BareJid));

			// "Put the layout back" puts the order back too.
			s.Vm.Settings.ResetLayoutCommand.Execute(null);
			Assert.Null(s.Vm.Fleet.Sort);
			Assert.Null(Saved(dir).NeuronSort);
		}
	}

	// ----- Windows of their own -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task AScreenOpensInAWindowOfItsOwnAndComesBack(string theme)
	{
		string dir = Folder();
		string t = theme.ToLowerInvariant();
		string eventsAddress;
		{
			(Sample s, AlertService alerts) = await SampleAsync(dir);
			using Sample _ = s;
			using AlertService __ = alerts;
			MainWindowViewModel vm = s.Vm;
			vm.ShowEventsCommand.Execute(null);
			vm.ToggleNeuronCommand.Execute(vm.NeuronChips.Single(c => c.BareJid == Gbg));
			MainWindow window = GettingAroundTests.Window(s, theme);
			ShellWindows.Attach(window, vm, () => Another(s));
			Assert.Empty(ShellWindows.Extras);

			// Ctrl+Shift+N: what is showing, filtered as it is, in a window of its own.
			vm.NewWindowCommand.Execute(null);
			Dispatcher.UIThread.RunJobs();
			MainWindow events = Assert.Single(ShellWindows.Extras);
			MainWindowViewModel eventsVm = Assert.IsType<MainWindowViewModel>(events.DataContext);
			Assert.NotSame(vm, eventsVm);
			Assert.True(eventsVm.IsExtra);
			Assert.True(eventsVm.IsEvents);
			Assert.Equal(Gbg, eventsVm.LensJid);
			Assert.Equal("Events · Neuro.AA", eventsVm.WindowTitle);
			Assert.Same(vm.Settings, eventsVm.Settings);
			Assert.False(eventsVm.CanGoBack);
			eventsAddress = eventsVm.Address;
			GettingAroundTests.Capture(events, $"windows-events-{t}");

			// The windows go their own ways: the main one moves on, the other stays.
			vm.ShowAllNeuronsCommand.Execute(null);
			vm.ShowFleetCommand.Execute(null);
			Dispatcher.UIThread.RunJobs();
			Assert.True(eventsVm.IsEvents);
			Assert.Equal(Gbg, eventsVm.LensJid);
			Assert.Null(vm.LensJid);

			// A Neuron's menu opens its page in a new window; a conversation's menu does the same.
			IReadOnlyList<ItemAction> forNeuron = vm.NeuronActions(Prod);
			forNeuron.Single(a => a.Text == "Open in a new window").RunAsync().Wait();
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(2, ShellWindows.Extras.Count);
			MainWindowViewModel prodVm = Assert.IsType<MainWindowViewModel>(ShellWindows.Extras[1].DataContext);
			Assert.True(prodVm.IsNeuronPage);
			Assert.Equal(Prod, prodVm.Page!.BareJid);
			Assert.Equal("Production · Neuro.AA", prodVm.WindowTitle);
			GettingAroundTests.Capture(ShellWindows.Extras[1], $"windows-neuron-{t}");

			vm.ShowChatCommand.Execute(null);
			PersonViewModel kim = vm.Chat.Everyone.Single(p => p.BareJid == "kim@lab.tagroot.io");
			vm.Chat.ActionsFor([kim]).Single(a => a.Text == "Open in a new window").RunAsync().Wait();
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(3, ShellWindows.Extras.Count);
			MainWindowViewModel chatVm = Assert.IsType<MainWindowViewModel>(ShellWindows.Extras[2].DataContext);
			Assert.True(chatVm.IsChat);
			Assert.Equal("kim@lab.tagroot.io", chatVm.Chat.SelectedPerson?.BareJid);
			ShellWindows.Extras[2].Width = 900;
			ShellWindows.Extras[2].Height = 640;
			Dispatcher.UIThread.RunJobs();
			GettingAroundTests.Capture(ShellWindows.Extras[2], $"windows-chat-{t}");

			// Closing one forgets it. Its view model is used again for the next window.
			ShellWindows.Extras[1].Close();
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(2, ShellWindows.Extras.Count);
			Assert.Equal(2, Saved(dir).MoreWindows.Count);
			Assert.Contains(prodVm, ShellWindows.Others);

			vm.ShowConsoleCommand.Execute(null);
			vm.NewWindowCommand.Execute(null);
			Dispatcher.UIThread.RunJobs();
			Assert.Same(prodVm, ShellWindows.Extras[2].DataContext);
			Assert.True(prodVm.IsConsole);
			Assert.False(prodVm.CanGoBack);
			ShellWindows.Extras[2].Close();
			Dispatcher.UIThread.RunJobs();

			// What is remembered: the two that are open, and what each shows.
			ShellWindows.Save();
			UiSettings saved = Saved(dir);
			Assert.Equal([eventsAddress, chatVm.Address], saved.MoreWindows.Select(w => w.Showing));
			Assert.Equal(900, saved.MoreWindows[1].Width);
			Assert.Equal(640, saved.MoreWindows[1].Height);

			// Quitting closes them without forgetting them.
			window.Close();
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(2, Saved(dir).MoreWindows.Count);
		}

		// The next run: the windows come back, showing what they showed.
		{
			(Sample s, AlertService alerts) = await SampleAsync(dir);
			using Sample _ = s;
			using AlertService __ = alerts;
			MainWindow window = new() { DataContext = s.Vm };
			ShellWindows.Attach(window, s.Vm, () => Another(s));
			window.Show();
			Dispatcher.UIThread.RunJobs();

			Assert.Equal(2, ShellWindows.Extras.Count);
			MainWindowViewModel eventsVm = Assert.IsType<MainWindowViewModel>(ShellWindows.Extras[0].DataContext);
			Assert.True(eventsVm.IsEvents);
			Assert.Equal(Gbg, eventsVm.LensJid);
			MainWindowViewModel chatVm = Assert.IsType<MainWindowViewModel>(ShellWindows.Extras[1].DataContext);
			Assert.True(chatVm.IsChat);
			Assert.Equal("kim@lab.tagroot.io", chatVm.Chat.SelectedPerson?.BareJid);
			Assert.Equal(900, ShellWindows.Extras[1].Width);
			Assert.Equal(640, ShellWindows.Extras[1].Height);

			// The main window's own Neuron filter is its own: the other window's did not take it over.
			Assert.Null(s.Vm.LensJid);
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	[AvaloniaFact]
	public async Task AWindowWaitsForWhatIsNotThereYet()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel other = Another(s);
		other.Join();
		Dispatcher.UIThread.RunJobs();

		// At start nothing has connected: the Neuron the window showed is not among the Neurons yet.
		const string late = "late.lab.tagroot.io";
		string address = string.Join('|', WorkspaceKind.NeuronEvents, s.Ops.ToString("N"), late, string.Empty);
		Assert.False(other.GoTo(address));
		Assert.False(other.IsNeuronPage);

		// It shows as soon as the Neuron is there.
		s.Monitors.For(s.Ops)!.Preview(new WatchedNeuron { AccountId = s.Ops, BareJid = late, Name = "Late", State = WatchState.Live, HistoryState = HistoryState.Loaded });
		other.Fleet.Refresh();
		Dispatcher.UIThread.RunJobs();
		Assert.True(other.IsNeuronEvents);
		Assert.Equal(late, other.Page!.BareJid);

		// Going somewhere by hand while it waits: the window stays where you went.
		MainWindowViewModel third = Another(s);
		third.Join();
		Assert.False(third.GoTo(string.Join('|', WorkspaceKind.Neuron, s.Ops.ToString("N"), "never.lab.tagroot.io", string.Empty)));
		third.ShowEventsCommand.Execute(null);
		s.Monitors.For(s.Ops)!.Preview(new WatchedNeuron { AccountId = s.Ops, BareJid = "never.lab.tagroot.io", Name = "Never", State = WatchState.Live, HistoryState = HistoryState.Loaded });
		third.Fleet.Refresh();
		Dispatcher.UIThread.RunJobs();
		Assert.True(third.IsEvents);

		// An address that means nothing is not an error.
		Assert.False(third.GoTo("Nowhere|x|y"));
		Assert.False(third.GoTo(null));
		Assert.True(third.IsEvents);
	}

	[AvaloniaFact]
	public async Task AWindowComesBackWhereItWasOnlyIfThatIsOnAScreen()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindow window = new() { DataContext = s.Vm };

		ShellWindows.Place(window, new WindowPlace { X = 120, Y = 80, Width = 1000, Height = 700 });
		Assert.Equal(1000, window.Width);
		Assert.Equal(700, window.Height);
		Assert.Equal(new PixelPoint(120, 80), window.Position);

		// A monitor that was unplugged does not take the window with it: the size is kept, the place is not.
		MainWindow lost = new() { DataContext = s.Vm };
		ShellWindows.Place(lost, new WindowPlace { X = 9000, Y = 4000, Width = 1100, Height = 720 });
		Assert.Equal(1100, lost.Width);
		Assert.NotEqual(new PixelPoint(9000, 4000), lost.Position);

		// A size smaller than the window can be is not taken.
		MainWindow tiny = new() { DataContext = s.Vm };
		ShellWindows.Place(tiny, new WindowPlace { X = 10, Y = 10, Width = 80, Height = 50, Maximized = true });
		Assert.Equal(1440, tiny.Width);
		Assert.Equal(WindowState.Maximized, tiny.WindowState);
	}

	// ----- The Wall view -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task TheWallShowsTheNeuronsLargeWithNoControls(string theme)
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
		WallViewModel wall = vm.Wall;
		wall.Now = () => new DateTime(2026, 11, 11, 14, 32, 0);
		wall.Start();

		Assert.Equal("14:32", wall.Clock);
		Assert.Equal("4 of 5 Neurons reporting", wall.Summary);
		Assert.Equal("busy", wall.SummaryTone);
		Assert.True(wall.HasErrors);

		// Every Neuron is a tile, by name, with its state in a word.
		Assert.Equal(["Backup", "Gothenburg edge", "Lab", "Production", "Stockholm edge"], wall.Tiles.Select(x => x.Label));
		WallTileViewModel backup = wall.Tiles[0];
		Assert.Equal("bad", backup.Tone);
		Assert.Equal("Offline", backup.StateText);
		WallTileViewModel gbg = wall.Tiles[1];
		Assert.Equal("busy", gbg.Tone);
		Assert.True(gbg.HighCpu);
		Assert.True(gbg.HasErrors);
		Assert.Equal("Errors in the last hour", gbg.StateText);
		Assert.Equal("ok", wall.Tiles[4].Tone);
		Assert.Equal("All well", wall.Tiles[4].StateText);

		// The latest warnings and errors, newest first, with the Neuron's name.
		Assert.Equal(8, wall.Latest.Count);
		Assert.Equal("Gothenburg edge", wall.Latest[0].Neuron);
		Assert.Equal("bad", wall.Latest[0].Tone);
		Assert.True(wall.Latest.Zip(wall.Latest.Skip(1)).All(p => p.First.Event.Timestamp >= p.Second.Event.Timestamp));

		Window window = new() { Width = 1920, Height = 1080, Content = new WallView { DataContext = wall } };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		WallView view = Assert.IsType<WallView>(window.Content);
		Assert.Equal(1.5, view.Factor);
		Assert.Empty(view.GetVisualDescendants().OfType<Button>());
		Assert.Empty(view.GetVisualDescendants().OfType<TextBox>());
		GettingAroundTests.Capture(window, $"wall-{theme.ToLowerInvariant()}");

		// A new error comes in: it is on top the next second.
		if (SniffedEventParser.Parse(new Neuro.AA.Engine.Sniffing.SnifferLine(0, DateTime.Now, Neuro.AA.Engine.Sniffing.SnifferDirection.Error,
			"OutOfMemoryException in Waher.Persistence.Files\n\nModule: Waher.Persistence"), new EventSource(s.Admin, Prod)) is NeuronEvent e)
			s.Monitors.Hub.Add(e);
		wall.Refresh();
		Assert.Equal("Production", wall.Latest[0].Neuron);
		Assert.StartsWith("OutOfMemoryException", wall.Latest[0].Title);
		Assert.Equal(8, wall.Latest.Count);

		// A smaller screen: the same wall, smaller.
		window.Width = 1280;
		window.Height = 720;
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(1, view.Factor);
		GettingAroundTests.Capture(window, $"wall-1280-{theme.ToLowerInvariant()}");

		window.Close();
		wall.Stop();

		// Nothing at all yet: it says so instead of showing an empty board.
		WallViewModel empty = new(new FleetViewModel(new FleetService(new MonitorService(s.Manager, new EventHub())), s.Manager), new EventHub());
		empty.Refresh();
		Assert.False(empty.HasTiles);
		Assert.False(empty.HasLatest);
		Assert.Equal("No Neurons yet", empty.Summary);
	}

	[AvaloniaFact]
	public async Task F11OpensTheWallAndClosesItAgain()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindow window = GettingAroundTests.Window(s, "Light");
		ShellWindows.Attach(window, s.Vm, () => Another(s));

		s.Vm.OpenWallCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		Window wall = Assert.IsAssignableFrom<Window>(ShellWindows.Wall);
		Assert.Equal(WindowState.FullScreen, wall.WindowState);
		Assert.IsType<WallView>(wall.Content);
		Assert.Equal(5, s.Vm.Wall.Tiles.Count);

		// The same key, or the same command, closes it.
		s.Vm.OpenWallCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		Assert.Null(ShellWindows.Wall);

		// It is found from the search box too, as is a new window.
		s.Vm.Search.Open();
		s.Vm.Search.Query = "wall";
		Assert.Equal("Wall view", s.Vm.Search.Results[0].Title);
		s.Vm.Search.Query = "new window";
		Assert.Equal("New window", s.Vm.Search.Results[0].Title);
		s.Vm.Search.Close();
		window.Close();
	}

	// ----- Small and large -----

	[AvaloniaFact]
	public async Task ASmallAndAVeryWideWindowBothWork()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;
		vm.ShowFleetCommand.Execute(null);
		MainWindow window = GettingAroundTests.Window(s, "Light", 760, 600);

		// Small: the new-window button and the Wall tab make room; both stay reachable by key and from the search box.
		Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NewWindowButton").IsEffectivelyVisible);
		GettingAroundTests.Capture(window, "sizes-small-neurons");
		vm.ShowAttentionCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		GettingAroundTests.Capture(window, "sizes-small-inbox");
		window.Close();

		foreach ((string name, Action show) in new (string, Action)[]
		{
			("inbox", () => vm.ShowAttentionCommand.Execute(null)),
			("neurons", () => vm.ShowFleetCommand.Execute(null)),
			("events", () => vm.ShowEventsCommand.Execute(null)),
			("chat", () => vm.ShowChatCommand.Execute(null)),
			("console", () => vm.ShowConsoleCommand.Execute(null)),
			("neuron", () => vm.OpenNeuron(Gbg)),
		})
		{
			show();
			MainWindow wide = GettingAroundTests.Window(s, "Light", 2400, 1200);
			Assert.True(GettingAroundTests.Shown<Button>(wide, "NewWindowButton").IsEffectivelyVisible);
			Assert.Contains("wide", wide.FindControl<Grid>("Root")!.Classes);
			GettingAroundTests.Capture(wide, "sizes-wide-" + name);
			wide.Close();
		}

		// Very wide: lists take some of the room, so the rest does not just stretch.
		vm.ShowAttentionCommand.Execute(null);
		MainWindow medium = GettingAroundTests.Window(s, "Light");
		Assert.DoesNotContain("wide", medium.FindControl<Grid>("Root")!.Classes);
		double listAtMedium = PaneOf(medium, "inbox").Bounds.Width;
		medium.Close();
		MainWindow large = GettingAroundTests.Window(s, "Light", 2400, 1200);
		Assert.True(PaneOf(large, "inbox").Bounds.Width > listAtMedium);

		// And in Chat a third column opens: who you are talking to, beside the conversation.
		vm.ShowChat(s.Ops, "kim@lab.tagroot.io");
		Dispatcher.UIThread.RunJobs();
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.Chat.IsPersonPanel);
		GettingAroundTests.Capture(large, "sizes-wide-chat-open");

		// Closed by hand, it stays closed.
		vm.Chat.TogglePanelCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		Assert.False(vm.Chat.IsPersonPanel);
		vm.ShowChat(s.Ops, "robin@gbg.lab.tagroot.io");
		Dispatcher.UIThread.RunJobs();
		Assert.False(vm.Chat.IsPersonPanel);
		large.Close();

		// At the usual size it opens only when asked for, as before.
		using Sample again = await GettingAroundTests.SampleAsync();
		again.Vm.ShowChat(again.Ops, "kim@lab.tagroot.io");
		MainWindow usual = GettingAroundTests.Window(again, "Light");
		Dispatcher.UIThread.RunJobs();
		Assert.False(again.Vm.Chat.IsPersonPanel);
		usual.Close();
	}
}
