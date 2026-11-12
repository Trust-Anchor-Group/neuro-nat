using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.Controls;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine.Alerts;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Sniffing;
using Sample = Neuro.Nat.App.Tests.GettingAroundTests.Sample;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The big screen (F11): the Neurons, the open problems and the pages features add, one at a
/// time. It turns by itself, skips empty pages, turns to the news, and answers Left, Right and
/// Space. Renders every page at 1280 and 3840 wide, light and dark, for review.
/// </summary>
public class BigScreenTests
{
	private const string Sto = "sto.lab.tagroot.io";
	private const string Prod = "prod.example.com";

	/// <summary>A page a feature added, as App numbers will be: it says whether it has something to show.</summary>
	private sealed class StandIn(string key, string title) : IBigScreenPage
	{
		public string Key { get; } = key;
		public string Title { get; } = title;
		public bool HasContent { get; set; } = true;
		public object? Content { get; set; }
		public int Started { get; private set; }

		public void Start() => this.Started++;
	}

	private static NeuronEvent Error(Guid account, string neuron, string text)
		=> SniffedEventParser.Parse(new SnifferLine(0, DateTime.Now, SnifferDirection.Error, text), new EventSource(account, neuron))
			?? throw new InvalidOperationException("Not an event.");

	private static void Resolve(EventHub hub)
	{
		foreach (Issue i in hub.Issues())
			hub.SetState(i.Fingerprint, IssueState.Resolved);
	}

	private static string ReviewFolder()
	{
		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;

		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		return folder;
	}

	private static void Capture(TopLevel window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		using WriteableBitmap? frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		using FileStream file = File.Create(Path.Combine(ReviewFolder(), name + ".png"));
		frame!.Save(file, new PngBitmapEncoderOptions());
	}

	// ----- The Neurons page -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task TheNeuronsPageShowsTheNeuronsLargeWithNoControls(string theme)
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
		BigScreenViewModel big = s.Vm.BigScreen;
		big.Now = () => new DateTime(2026, 11, 11, 14, 32, 0);
		big.Start();

		Assert.True(big.IsNeuronsPage);
		Assert.Equal("14:32", big.Clock);
		Assert.Equal("4 of 5 Neurons reporting", big.Summary);
		Assert.Equal("4 of 5 Neurons reporting", big.Headline);
		Assert.Equal("busy", big.SummaryTone);
		Assert.True(big.HasErrors);

		// Every Neuron is a tile, by name, with its state in a word.
		Assert.Equal(["Backup", "Gothenburg edge", "Lab", "Production", "Stockholm edge"], big.Tiles.Select(x => x.Label));
		BigScreenTileViewModel backup = big.Tiles[0];
		Assert.Equal("bad", backup.Tone);
		Assert.Equal("Offline", backup.StateText);
		BigScreenTileViewModel gbg = big.Tiles[1];
		Assert.Equal("busy", gbg.Tone);
		Assert.True(gbg.HighCpu);
		Assert.True(gbg.HasErrors);
		Assert.Equal("Errors in the last hour", gbg.StateText);
		Assert.Equal("ok", big.Tiles[4].Tone);
		Assert.Equal("All well", big.Tiles[4].StateText);

		// The latest warnings and errors, newest first, with the Neuron's name.
		Assert.Equal(8, big.Latest.Count);
		Assert.Equal("Gothenburg edge", big.Latest[0].Neuron);
		Assert.Equal("bad", big.Latest[0].Tone);
		Assert.True(big.Latest.Zip(big.Latest.Skip(1)).All(p => p.First.Event.Timestamp >= p.Second.Event.Timestamp));

		Window window = new() { Width = 1920, Height = 1080, Content = new BigScreenView { DataContext = big } };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		BigScreenView view = Assert.IsType<BigScreenView>(window.Content);
		Assert.Equal(1.5, view.Factor);
		Assert.Empty(view.GetVisualDescendants().OfType<Button>());
		Assert.Empty(view.GetVisualDescendants().OfType<TextBox>());

		// A new error comes in: it is on top the next second.
		s.Monitors.Hub.Add(Error(s.Admin, Prod, "OutOfMemoryException in Waher.Persistence.Files\n\nModule: Waher.Persistence"));
		big.Refresh();
		Assert.Equal("Production", big.Latest[0].Neuron);
		Assert.StartsWith("OutOfMemoryException", big.Latest[0].Title);
		Assert.Equal(8, big.Latest.Count);

		window.Close();
		big.Stop();

		// Nothing at all yet: it says so instead of showing an empty board, and there is only the one page.
		BigScreenViewModel empty = new(new FleetViewModel(new FleetService(new MonitorService(s.Manager, new EventHub())), s.Manager), new EventHub(), added: []);
		empty.Refresh();
		Assert.False(empty.HasTiles);
		Assert.False(empty.HasLatest);
		Assert.Equal("No Neurons yet", empty.Summary);
		Assert.Single(empty.Dots);
		Assert.False(empty.HasDots);
	}

	// ----- Turning -----

	[AvaloniaFact]
	public async Task PagesTurnInOrderEveryTwentySecondsAndEmptyOnesAreSkipped()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		StandIn numbers = new("app-numbers", "App numbers") { HasContent = false };
		BigScreenViewModel big = new(s.Vm.Fleet, s.Monitors.Hub, s.Vm.Settings, [numbers]);
		big.Start();
		Assert.Equal(1, numbers.Started);

		// Neurons, Problems, App numbers: App numbers has nothing yet, so it is not there.
		Assert.Equal(["neurons", "problems", "app-numbers"], big.Pages.Select(p => p.Key));
		Assert.Equal(["neurons", "problems"], big.Dots.Select(p => p.Key));
		Assert.True(big.HasDots);
		Assert.Equal(TimeSpan.FromSeconds(20), big.Dwell);

		Assert.True(big.IsNeuronsPage);
		big.Advance(TimeSpan.FromSeconds(19));
		Assert.True(big.IsNeuronsPage);
		big.Advance(TimeSpan.FromSeconds(1));
		Assert.True(big.IsProblemsPage);
		Assert.True(big.Current.IsCurrent);
		Assert.False(big.Pages[0].IsCurrent);

		// The Problems page: what, where, since when, how many times. The worst at the top of the page.
		Assert.Equal(big.OpenProblems == 1 ? "1 open problem" : $"{big.OpenProblems} open problems", big.Headline);
		BigScreenProblemViewModel socket = big.Problems.Single(p => p.Title.StartsWith("SocketException", StringComparison.Ordinal));
		Assert.Equal("Gothenburg edge", socket.Where);
		Assert.Equal("9", socket.Count);
		Assert.Equal("times", socket.CountUnit);
		Assert.StartsWith("Since ", socket.Since);
		Assert.Equal("bad", socket.Tone);
		Assert.True(big.Problems.Count <= BigScreenViewModel.MaxProblems);

		// Round again: App numbers is skipped while empty.
		big.Advance(TimeSpan.FromSeconds(20));
		Assert.True(big.IsNeuronsPage);

		// Now it has something: it takes its turn after Problems.
		numbers.HasContent = true;
		big.Refresh();
		Assert.Equal(["neurons", "problems", "app-numbers"], big.Dots.Select(p => p.Key));
		big.Advance(TimeSpan.FromSeconds(20));
		Assert.True(big.IsProblemsPage);
		big.Advance(TimeSpan.FromSeconds(20));
		Assert.True(big.IsAddedPage);
		Assert.Equal("App numbers", big.Headline);

		// It empties while on the screen: on to the next page at once.
		numbers.HasContent = false;
		big.Refresh();
		Assert.True(big.IsNeuronsPage);

		// No open problems: the Problems page is skipped, and with one page left there are no dots and no turning.
		Resolve(s.Monitors.Hub);
		big.Refresh();
		Assert.Equal(["neurons"], big.Dots.Select(p => p.Key));
		Assert.False(big.HasDots);
		big.Advance(TimeSpan.FromMinutes(2));
		Assert.True(big.IsNeuronsPage);
		big.Stop();
	}

	[AvaloniaFact]
	public async Task ItTurnsToTheNewsAndStaysALittleLonger()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		BigScreenViewModel big = s.Vm.BigScreen;
		big.Start();
		Assert.True(big.IsNeuronsPage);
		Assert.All(big.Problems, p => Assert.False(p.IsNew));

		// A problem that was not there opens: Problems, with it at the top, marked new, for half as long again.
		big.Advance(TimeSpan.FromSeconds(5));
		s.Monitors.Hub.Add(Error(s.Admin, Prod, "IOException: The disk is full\n\nModule: Waher.Persistence.Files"));
		big.Refresh();
		Assert.True(big.IsProblemsPage);
		Assert.Equal(TimeSpan.FromSeconds(30), big.Left);
		Assert.StartsWith("IOException", big.Problems[0].Title);
		Assert.True(big.Problems[0].IsNew);
		Assert.Equal("Production", big.Problems[0].Where);
		Assert.Equal("1", big.Problems[0].Count);
		Assert.Equal("time", big.Problems[0].CountUnit);

		// The same problem again is not news.
		big.Advance(TimeSpan.FromSeconds(29));
		s.Monitors.Hub.Add(Error(s.Admin, Prod, "IOException: The disk is full\n\nModule: Waher.Persistence.Files\nObject: again"));
		big.Refresh();
		Assert.True(big.IsProblemsPage);
		Assert.Equal(TimeSpan.FromSeconds(1), big.Left);
		big.Advance(TimeSpan.FromSeconds(1));
		Assert.True(big.IsNeuronsPage);
		Assert.All(big.Problems, p => Assert.False(p.IsNew));

		// On Problems, a Neuron that answered stops: back to the Neurons, where its tile is red.
		big.Press(Key.Right);
		Assert.True(big.IsProblemsPage);
		s.Monitors.For(s.Ops)!.Preview(new WatchedNeuron { AccountId = s.Ops, BareJid = Sto, Name = "Stockholm edge", State = WatchState.Offline });
		s.Vm.Fleet.Refresh();
		Assert.True(big.IsNeuronsPage);
		Assert.Equal(TimeSpan.FromSeconds(30), big.Left);
		Assert.Equal("bad", big.Tiles.Single(t => t.BareJid == Sto).Tone);

		// One that was down already when the screen opened is not news.
		big.Stop();
		big.Start();
		big.Press(Key.Right);
		s.Vm.Fleet.Refresh();
		Assert.True(big.IsProblemsPage);
		big.Stop();
	}

	[AvaloniaFact]
	public async Task LeftAndRightTurnAndSpaceHolds()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindow main = GettingAroundTests.Window(s, "Light");
		ShellWindows.Attach(main, s.Vm, () => new MainWindowViewModel(s.Manager, s.Fleet, s.Chat, null, null, s.PubSub, s.Software, primary: s.Vm));

		s.Vm.OpenBigScreenCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		Window window = Assert.IsAssignableFrom<Window>(ShellWindows.BigScreen);
		Assert.Equal("Big screen - NAT", window.Title);
		Assert.Equal(WindowState.FullScreen, window.WindowState);
		BigScreenViewModel big = s.Vm.BigScreen;
		Assert.True(big.IsNeuronsPage);

		window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
		Assert.True(big.IsProblemsPage);
		window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
		Assert.True(big.IsNeuronsPage);
		window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
		Assert.True(big.IsProblemsPage);

		// Space holds the page, however long; Space again lets it go on.
		window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
		Assert.True(big.Held);
		Dispatcher.UIThread.RunJobs();
		Assert.True(GettingAroundTests.Shown<TextBlock>(window, "HeldText").IsEffectivelyVisible);
		big.Advance(TimeSpan.FromMinutes(5));
		Assert.True(big.IsProblemsPage);
		window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
		Assert.False(big.Held);
		big.Advance(TimeSpan.FromSeconds(20));
		Assert.True(big.IsNeuronsPage);

		// Escape leaves, and F11 opens it again on the Neurons page.
		window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.Null(ShellWindows.BigScreen);
		s.Vm.OpenBigScreenCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		Assert.NotNull(ShellWindows.BigScreen);
		Assert.True(big.IsNeuronsPage);
		ShellWindows.BigScreen!.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.Null(ShellWindows.BigScreen);

		// It is found from the search box, by its name.
		s.Vm.Search.Open();
		s.Vm.Search.Query = "big screen";
		Assert.Equal("Big screen", s.Vm.Search.Results[0].Title);
		s.Vm.Search.Close();
		main.Close();
	}

	[AvaloniaFact]
	public async Task PagesSlideOnlyWithMotionOn()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		BigScreenViewModel big = s.Vm.BigScreen;
		big.Start();
		Window window = new() { Width = 1280, Height = 720, Content = new BigScreenView { DataContext = big } };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		BigScreenView view = Assert.IsType<BigScreenView>(window.Content);

		Assert.False(Motion.Enabled);
		big.Press(Key.Right);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(0, view.Slides);
		Assert.True(GettingAroundTests.Shown<Control>(view, "ProblemsPage").IsEffectivelyVisible);

		try
		{
			Motion.Set(true);
			big.Press(Key.Right);
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(1, view.Slides);
			Assert.True(GettingAroundTests.Shown<Control>(view, "NeuronsPage").IsEffectivelyVisible);
		}
		finally
		{
			Motion.Set(false);
		}

		window.Close();
		big.Stop();
	}

	// ----- Settings -----

	[AvaloniaFact]
	public async Task SettingsChooseThePagesAndHowLong()
	{
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Big." + Guid.NewGuid().ToString("N"));
		using Sample s = await GettingAroundTests.SampleAsync(dir);
		using AlertService alerts = new(s.Monitors, new AlertSettings());
		string ui = Path.Combine(dir, "ui.json");
		SettingsViewModel settings = s.Vm.Settings;
		settings.Attach(alerts, new JsonAlertSettingsStore(Path.Combine(dir, "alerts.json")), new JsonUiSettingsStore(ui));
		StandIn numbers = new("app-numbers", "App numbers");
		BigScreenPages.Added.Add(numbers);
		try
		{
			// As it comes: every page, 20 seconds each.
			Assert.Equal(20, settings.BigScreenSeconds);
			Assert.True(settings.IsBigScreen20);
			Assert.Equal(["Problems", "App numbers"], settings.BigScreenChoices.Select(c => c.Title));
			Assert.All(settings.BigScreenChoices, c => Assert.True(c.IsOn));

			BigScreenViewModel big = s.Vm.BigScreen;
			big.Start();
			Assert.Equal(["neurons", "problems", "app-numbers"], big.Dots.Select(p => p.Key));

			// 30 seconds, and no Problems page.
			settings.SetBigScreenSeconds(30);
			settings.BigScreenChoices.Single(c => c.Key == BigScreenPages.Problems).IsOn = false;
			UiSettings saved = new JsonUiSettingsStore(ui).Load();
			Assert.Equal(30, saved.BigScreenSeconds);
			Assert.Equal(["problems"], saved.BigScreenOff);
			Assert.Equal(TimeSpan.FromSeconds(30), big.Dwell);

			// The page on the screen keeps the time it had; the next one gets 30 seconds.
			big.Refresh();
			Assert.Equal(["neurons", "app-numbers"], big.Dots.Select(p => p.Key));
			big.Advance(TimeSpan.FromSeconds(20));
			Assert.True(big.IsAddedPage);
			big.Advance(TimeSpan.FromSeconds(29));
			Assert.True(big.IsAddedPage);
			big.Advance(TimeSpan.FromSeconds(1));
			Assert.True(big.IsNeuronsPage);

			// The Neurons page cannot be turned off, and a time not on offer is not taken.
			settings.SetBigScreenPage(BigScreenPages.Neurons, false);
			Assert.True(settings.IsBigScreenPageOn(BigScreenPages.Neurons));
			settings.SetBigScreenSeconds(7);
			Assert.Equal(30, settings.BigScreenSeconds);
			big.Stop();

			// The next run reads them back.
			SettingsViewModel next = new(s.Chat);
			next.Attach(alerts, new JsonAlertSettingsStore(Path.Combine(dir, "alerts.json")), new JsonUiSettingsStore(ui));
			Assert.Equal(30, next.BigScreenSeconds);
			Assert.False(next.IsBigScreenPageOn(BigScreenPages.Problems));
			Assert.True(next.IsBigScreenPageOn("app-numbers"));

			// Settings shows them.
			s.Vm.ShowSettingsCommand.Execute(null);
			MainWindow window = GettingAroundTests.Window(s, "Light");
			ItemsControl list = GettingAroundTests.Shown<ItemsControl>(window, "BigScreenPagesList");
			Assert.Equal(2, list.GetVisualDescendants().OfType<CheckBox>().Count());
			window.Close();
		}
		finally
		{
			BigScreenPages.Added.Remove(numbers);
		}
	}

	// ----- Pictures -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task EveryPageReadsFromAcrossTheRoom(string theme)
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;

		// A stand-in for App numbers, to show where a feature's page goes.
		static StackPanel NumbersContent()
		{
			StackPanel all = new() { Orientation = Orientation.Horizontal, Spacing = 60 };
			foreach ((string n, string what) in new[] { ("1,284", "People signed in"), ("96", "New today"), ("3", "Crashes") })
			{
				StackPanel one = new();
				one.Children.Add(new TextBlock { Text = n, FontSize = 64, FontWeight = Avalonia.Media.FontWeight.SemiBold });
				one.Children.Add(new TextBlock { Text = what, FontSize = 20 });
				all.Children.Add(one);
			}

			return all;
		}

		StandIn numbers = new("app-numbers", "App numbers");
		BigScreenViewModel big = new(s.Vm.Fleet, s.Monitors.Hub, s.Vm.Settings, [numbers]);
		big.Start();
		string t = theme.ToLowerInvariant();
		foreach ((int width, int height, string news) in new[]
		{
			(1280, 720, "IOException: There is not enough space on the disk"),
			(3840, 2160, "UnauthorizedAccessException: Access to the backup folder is denied"),
		})
		{
			// A problem opens just before: the Problems page shows it marked new.
			big.Press(Key.Left);
			s.Monitors.Hub.Add(Error(s.Admin, Prod, news + "\n\nModule: Waher.Persistence.Files"));
			big.Refresh();
			Assert.True(big.IsProblemsPage);
			Assert.True(big.Problems[0].IsNew);

			numbers.Content = NumbersContent();
			Window window = new() { Width = width, Height = height, Content = new BigScreenView { DataContext = big } };
			window.Show();
			Dispatcher.UIThread.RunJobs();
			BigScreenView view = Assert.IsType<BigScreenView>(window.Content);
			Assert.Equal(width / 1280.0, view.Factor);

			foreach (string page in new[] { "problems", "app-numbers", "neurons" })
			{
				while (big.Current.Key != page)
					big.Press(Key.Right);

				Dispatcher.UIThread.RunJobs();
				Capture(window, $"bigscreen-{page}-{width}-{t}");
			}

			window.Close();
		}

		big.Stop();
	}
}
