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
	private const string Sto = "sto.lab.example.com";
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
		Assert.True(big.PageProblems.Count <= BigScreenViewModel.ProblemsPerPage);

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

	// ----- Many Neurons, many problems -----

	private static readonly string[] Places = ["Malmo", "Uppsala", "Lund", "Umea", "Kiruna", "Visby", "Orebro", "Vasteras", "Linkoping", "Jonkoping",
		"Norrkoping", "Gavle", "Sundsvall", "Karlstad", "Vaxjo", "Halmstad", "Kalmar", "Falun", "Lulea", "Helsingborg"];

	private static readonly string[] Roles = ["edge", "lab", "backup", "broker", "store", "gateway"];

	private static readonly string[] Troubles =
	[
		"SocketException: Connection reset by peer",
		"TimeoutException in ConcentratorServer.ReadSensorData",
		"IOException: There is not enough space on the disk",
		"UnauthorizedAccessException: Access to the backup folder is denied",
		"OutOfMemoryException in Waher.Persistence.Files",
		"InvalidOperationException: The certificate has expired",
		"HttpRequestException: The remote name could not be resolved",
		"ArgumentNullException: Value cannot be null in MarketplaceProcessor",
		"KeyNotFoundException: The contract template was not found",
		"NotSupportedException: The payment provider does not answer",
		"TaskCanceledException: The push notification was not delivered",
		"FormatException: The meter reading is not a number",
		"ObjectDisposedException: The ledger was closed while writing",
	];

	private sealed record Board(BigScreenViewModel Big, FleetViewModel Fleet, MonitorService Monitors, EventHub Hub, Guid Account);

	/// <summary>A fleet of its own: <paramref name="count"/> Neurons by place and role. With trouble, some are offline, busy or failing.</summary>
	private static Board Many(Sample s, int count, int problems = 0, bool trouble = false)
	{
		EventHub hub = new();
		MonitorService monitors = new(s.Manager, hub);
		FleetService service = new(monitors);
		DateTime now = DateTime.Now;
		for (int i = 0; i < count; i++)
		{
			string jid = $"n{i:000}.fleet.example";
			WatchState state = trouble && i % 17 == 5 ? WatchState.Offline : trouble && i % 23 == 7 ? WatchState.Waiting : WatchState.Live;
			monitors.For(s.Ops)!.Preview(new WatchedNeuron { AccountId = s.Ops, BareJid = jid, Name = $"{Places[i % Places.Length]} {Roles[i / Places.Length % Roles.Length]}", State = state, HistoryState = HistoryState.Loaded });
			if (state == WatchState.Live)
			{
				double cpu = trouble && i % 13 == 3 ? 94 : 6 + (i * 7 % 50);
				service.Preview(new NeuronHealth
				{
					AccountId = s.Ops,
					NeuronJid = jid,
					Cpu = new SampleSeries(cpu, [cpu], "%"),
					AllocatedMemory = new SampleSeries(640, [640], "MB"),
					FreeMemory = new SampleSeries(5210, [5210], "MB"),
					FreeDisk = new SampleSeries(182.4, [182.4], "GB"),
					ReadAt = now.AddSeconds(-20),
				});
			}
		}

		for (int i = 0; i < problems; i++)
			hub.Add(Error(s.Ops, $"n{i * 7 % count:000}.fleet.example", Troubles[i] + "\n\nModule: Waher.IoTGateway"));

		FleetViewModel fleet = new(service, s.Manager);
		fleet.Refresh();
		return new(new BigScreenViewModel(fleet, hub, null, []), fleet, monitors, hub, s.Ops);
	}

	[AvaloniaFact]
	public void NoTileIsEverSmallerThanReadsFromAcrossTheRoom()
	{
		foreach (Size area in new[] { BigScreenTileLayout.DrawnArea, new Size(802, 280), new Size(802, 1900), new Size(1600, 550) })
		{
			for (int n = 1; n <= 300; n++)
			{
				BigScreenTileLayout layout = BigScreenTileLayout.For(n, area);
				Assert.InRange(layout.Columns, 1, BigScreenTileLayout.MaxColumns);
				Assert.True(layout.Columns * layout.Rows >= layout.PerPage);
				Assert.True(layout.PerPage * layout.Pages >= n);
				Assert.True((layout.Pages - 1) * layout.PerPage < n);
				Assert.True(layout.Scale >= BigScreenTileLayout.MinScale || layout.PerPage == 1, $"{n} Neurons on {area}: {layout}");
				double width = layout.Small ? BigScreenTileLayout.SmallWidth : BigScreenTileLayout.TileWidth;
				double height = layout.Small ? BigScreenTileLayout.SmallHeight : BigScreenTileLayout.TileHeight;
				Assert.True(layout.Columns * width * layout.Scale <= area.Width + 0.5);
				Assert.True(layout.Rows * height * layout.Scale <= area.Height + 0.5 || layout.PerPage == 1);
			}
		}

		// More room side by side, more columns: up to six.
		Assert.Equal(6, BigScreenTileLayout.For(120, new Size(1600, 550)).Columns);
	}

	[AvaloniaTheory]
	[InlineData(3, 1, 3, 2, false)]
	[InlineData(30, 2, 15, 4, true)]
	[InlineData(120, 6, 20, 5, true)]
	public async Task TheNeuronsTakeAsManyPagesAsTheyNeedInOrder(int count, int pages, int perPage, int columns, bool small)
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		Board board = Many(s, count);
		BigScreenViewModel big = board.Big;
		big.Start();

		Assert.Equal(count, big.Tiles.Count);
		Assert.Equal(pages, big.Layout.Pages);
		Assert.Equal(perPage, big.Layout.PerPage);
		Assert.Equal(columns, big.Columns);
		Assert.Equal(small, big.SmallTiles);
		Assert.True(big.TileScale >= BigScreenTileLayout.MinScale);

		// A page for each part, and a dot for each page.
		Assert.Equal(Enumerable.Range(0, pages), big.Pages.Where(p => p.Key == BigScreenPages.Neurons).Select(p => p.Part));
		Assert.Equal(pages, big.Dots.Count);
		Assert.Equal(pages > 1, big.HasDots);

		// They turn by themselves, every Neuron once, by name as on one page.
		List<string> seen = [];
		for (int part = 0; part < pages; part++)
		{
			Assert.True(big.IsNeuronsPage);
			Assert.Equal(part, big.Current.Part);
			Assert.Equal($"All {count} Neurons reporting", big.Headline);
			seen.AddRange(big.PageTiles.Select(t => t.Label));
			big.Advance(big.Dwell);
		}

		Assert.Equal(big.Tiles.Select(t => t.Label), seen);
		Assert.Equal(seen.Order(StringComparer.CurrentCultureIgnoreCase), seen);
		Assert.Equal(0, big.Current.Part);

		// Left from the first goes to the last; Space holds a part like any page.
		big.Press(Key.Left);
		Assert.Equal(pages - 1, big.Current.Part);
		big.Press(Key.Space);
		big.Advance(TimeSpan.FromMinutes(5));
		Assert.Equal(pages - 1, big.Current.Part);
		big.Press(Key.Space);
		big.Advance(big.Dwell);
		Assert.Equal(0, big.Current.Part);
		big.Stop();
	}

	[AvaloniaFact]
	public async Task EveryOpenProblemTakesItsTurnFiveToAPage()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		Board board = Many(s, 5, problems: 12);
		BigScreenViewModel big = board.Big;
		big.Start();

		Assert.Equal(12, big.OpenProblems);
		Assert.Equal(12, big.Problems.Count);
		Assert.Equal(["neurons", "problems", "problems", "problems"], big.Dots.Select(p => p.Key));
		Assert.Equal([0, 1, 2], big.Dots.Where(p => p.Key == BigScreenPages.Problems).Select(p => p.Part));

		// Five, five and two: every problem once, in the order of the list.
		List<BigScreenProblemViewModel> seen = [];
		foreach (int expected in new[] { 5, 5, 2 })
		{
			big.Advance(big.Dwell);
			Assert.True(big.IsProblemsPage);
			Assert.Equal("12 open problems", big.Headline);
			Assert.Equal(expected, big.PageProblems.Count);
			seen.AddRange(big.PageProblems);
		}

		Assert.Equal(big.Problems, seen);
		big.Advance(big.Dwell);
		Assert.True(big.IsNeuronsPage);

		// On the last part, a new problem opens: the first part, with it at the top, for half as long again.
		big.Press(Key.Left);
		Assert.Equal(2, big.Current.Part);
		board.Hub.Add(Error(board.Account, "n001.fleet.example", Troubles[12] + "\n\nModule: Waher.IoTGateway"));
		big.Refresh();
		Assert.True(big.IsProblemsPage);
		Assert.Equal(0, big.Current.Part);
		Assert.Equal(big.NewsDwell, big.Left);
		Assert.True(big.PageProblems[0].IsNew);
		Assert.StartsWith("ObjectDisposedException", big.PageProblems[0].Title);
		Assert.Equal(13, big.Problems.Count);
		Assert.Equal(3, big.Dots.Count(p => p.Key == BigScreenPages.Problems));

		// It stays new while the Problems pages turn, and is seen once they are left.
		big.Press(Key.Right);
		Assert.Equal(1, big.Current.Part);
		Assert.True(big.Problems[0].IsNew);
		big.Press(Key.Right);
		big.Press(Key.Right);
		Assert.True(big.IsNeuronsPage);
		Assert.All(big.Problems, p => Assert.False(p.IsNew));

		// Problems close: fewer pages, and the one on the screen gives way to the last one left.
		big.Press(Key.Left);
		Assert.Equal(2, big.Current.Part);
		foreach (BigScreenProblemViewModel p in big.Problems.Skip(4).ToList())
			board.Hub.SetState(p.Fingerprint, IssueState.Resolved);
		big.Refresh();
		Assert.True(big.IsProblemsPage);
		Assert.Equal(0, big.Current.Part);
		Assert.Equal(4, big.PageProblems.Count);
		Assert.Equal(["neurons", "problems"], big.Dots.Select(p => p.Key));
		big.Stop();
	}

	[AvaloniaFact]
	public async Task ANeuronThatGoesDownTurnsToThePartWithItsTile()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		Board board = Many(s, 30);
		BigScreenViewModel big = board.Big;
		big.Start();
		Assert.Equal(2, big.Layout.Pages);
		Assert.Equal(0, big.Current.Part);

		void Down(BigScreenTileViewModel tile)
		{
			board.Monitors.For(board.Account)!.Preview(new WatchedNeuron { AccountId = board.Account, BareJid = tile.BareJid, Name = tile.Label, State = WatchState.Offline });
			board.Fleet.Refresh();
		}

		// The 21st by name is on the second part: that part, for half as long again, with its tile red.
		BigScreenTileViewModel late = big.Tiles[20];
		big.Advance(TimeSpan.FromSeconds(5));
		Down(late);
		Assert.True(big.IsNeuronsPage);
		Assert.Equal(1, big.Current.Part);
		Assert.Equal(big.NewsDwell, big.Left);
		Assert.Contains(late, big.PageTiles);
		Assert.Equal("bad", late.Tone);

		// The 4th goes down next: back to the first part.
		BigScreenTileViewModel early = big.Tiles[3];
		Down(early);
		Assert.Equal(0, big.Current.Part);
		Assert.Contains(early, big.PageTiles);
		Assert.Equal("28 of 30 Neurons reporting", big.Headline);
		big.Stop();
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task ManyNeuronsAndProblemsReadFromAcrossTheRoom(string theme)
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
		string t = theme.ToLowerInvariant();

		foreach (int count in new[] { 3, 30, 120 })
		{
			Board board = Many(s, count, problems: count == 30 ? 12 : 3, trouble: true);
			BigScreenViewModel big = board.Big;
			big.Start();
			foreach ((int width, int height) in new[] { (1920, 1080), (3840, 2160) })
			{
				Window window = new() { Width = width, Height = height, Content = new BigScreenView { DataContext = big } };
				window.Show();
				Dispatcher.UIThread.RunJobs();
				BigScreenView view = Assert.IsType<BigScreenView>(window.Content);
				Assert.Equal(width / 1280.0, view.Factor);
				Assert.True(big.TileScale >= BigScreenTileLayout.MinScale);

				// On a 16:9 screen, the space for the tiles is the one the layout counts on before the view says.
				Assert.Equal(BigScreenTileLayout.DrawnArea.Width, big.TileArea.Width, 1.0);
				Assert.Equal(BigScreenTileLayout.DrawnArea.Height, big.TileArea.Height, 1.0);

				int last = big.Layout.Pages - 1;
				foreach (int part in last == 0 ? new[] { 0 } : new[] { 0, last })
				{
					while (!(big.IsNeuronsPage && big.Current.Part == part))
						big.Press(Key.Right);

					Dispatcher.UIThread.RunJobs();
					AllTilesShowWhole(view, big);
					Capture(window, $"bigscreen-k3-{count}-neurons-{part + 1}-{width}-{t}");
				}

				if (count == 30)
				{
					foreach (int part in new[] { 0, 2 })
					{
						while (!(big.IsProblemsPage && big.Current.Part == part))
							big.Press(Key.Right);

						Dispatcher.UIThread.RunJobs();
						ItemsControl list = GettingAroundTests.Shown<ItemsControl>(view, "ProblemList");
						Assert.Equal(big.PageProblems.Count, list.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("bigProblem") && b.IsEffectivelyVisible));
						Capture(window, $"bigscreen-k3-12-problems-{part + 1}-{width}-{t}");
					}
				}

				window.Close();
			}

			big.Stop();
		}
	}

	/// <summary>Every tile on the page is inside the space for the tiles, nothing cut off.</summary>
	private static void AllTilesShowWhole(BigScreenView view, BigScreenViewModel big)
	{
		Control host = GettingAroundTests.Shown<Control>(view, "TileHost");
		List<Border> tiles = [.. host.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("bigTile") && b.IsEffectivelyVisible)];
		Assert.Equal(big.PageTiles.Count, tiles.Count);
		foreach (Border tile in tiles)
		{
			Point? end = tile.TranslatePoint(new Point(tile.Bounds.Width, tile.Bounds.Height), host);
			Assert.NotNull(end);
			Assert.True(end!.Value.X <= host.Bounds.Width + 1 && end.Value.Y <= host.Bounds.Height + 1, $"A tile ends at {end} in {host.Bounds.Size}");

			// What the tile says fits in it: every tile is as tall as the others, and none cuts its numbers off.
			StackPanel body = tile.GetVisualDescendants().OfType<StackPanel>().First(p => p.Classes.Contains("tileBody"));
			Assert.True(body.DesiredSize.Height <= tile.Bounds.Height - 2, $"A tile needs {body.DesiredSize.Height} and has {tile.Bounds.Height}");
		}
	}
}
