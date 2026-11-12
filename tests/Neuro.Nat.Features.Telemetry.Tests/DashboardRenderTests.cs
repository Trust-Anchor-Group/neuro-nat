using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Features.Telemetry.Charts;
using Neuro.Nat.Features.Telemetry.Data;
using Neuro.Nat.Features.Telemetry.Tests.StandIn;
using Neuro.Nat.Features.Telemetry.ViewModels;

namespace Neuro.Nat.Features.Telemetry.Tests;

/// <summary>
/// Renders every page of the App telemetry dashboard with the stand-in numbers, in both looks at
/// 1440 by 900 and at 760 wide, for review in .impeccable/review. Also checks that with no package
/// anywhere NAT shows nothing of it.
/// </summary>
public class DashboardRenderTests
{
	private const string Lab = "lab.example.com";
	private static Application? templatesFor;

	private static void UseTemplates(TelemetrySample s)
	{
		if (templatesFor == Application.Current)
			return;
		templatesFor = Application.Current;
		foreach (Avalonia.Controls.Templates.IDataTemplate t in s.Features.Templates())
			Application.Current!.DataTemplates.Add(t);
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

	private static void Capture(Window window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		Bitmap? frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		using FileStream stream = File.Create(Path.Combine(ReviewFolder(), name + ".png"));
		frame!.Save(stream, new PngBitmapEncoderOptions());
	}

	private static async Task<(TelemetrySample Sample, MainWindowViewModel Vm, MainWindow Window, DashboardViewModel Dashboard)> OpenAsync(string theme, int width, int height, bool internalAccess = true)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		TelemetrySample s = await TelemetrySample.CreateAsync([(Lab, new StandInTelemetry(TelemetrySample.Start) { Internal = internalAccess })]);
		UseTemplates(s);
		MainWindowViewModel vm = await s.WindowModelAsync();
		await s.FindAsync();

		vm.OpenNeuron(Lab, s.AccountOf[Lab]);
		Dispatcher.UIThread.RunJobs();
		vm.ShowFeatureTabCommand.Execute(TelemetryFeature.TabKey);
		DashboardViewModel d = Assert.IsType<DashboardViewModel>(vm.FeatureContent);
		MainWindow window = new() { DataContext = vm, Width = width, Height = height };
		window.Show();
		await d.LoadAsync(ask: true);
		Dispatcher.UIThread.RunJobs();
		return (s, vm, window, d);
	}

	private static async Task ShowAsync(MainWindow window, DashboardViewModel d, string part)
	{
		d.ShowPart(part);
		await d.LoadAsync(ask: true);
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(50);
		Dispatcher.UIThread.RunJobs();
		_ = window;
	}

	[AvaloniaTheory]
	[InlineData("Light", 1440, 900)]
	[InlineData("Dark", 1440, 900)]
	[InlineData("Light", 760, 900)]
	public async Task RendersEveryPage(string theme, int width, int height)
	{
		(TelemetrySample s, MainWindowViewModel vm, MainWindow window, DashboardViewModel d) = await OpenAsync(theme, width, height);
		await using TelemetrySample owned = s;
		string tag = theme.ToLowerInvariant() + "-" + width;

		Assert.Equal(["App telemetry"], vm.PageFeatureTabs.Select(t => t.Title));
		Assert.Equal(["App telemetry"], vm.FeaturePlaces.Select(t => t.Title));

		foreach ((string page, _) in Pages.All)
		{
			await ShowAsync(window, d, page);
			Assert.Equal(page, d.Page);
			Assert.True(d.ShowsNumbers);
			Capture(window, $"telemetry-{page}-{tag}");
		}

		// The same pages in a tall window, to see what is under the fold.
		if (width == 1440)
		{
			window.Height = 2200;
			foreach ((string page, _) in Pages.All)
			{
				await ShowAsync(window, d, page);
				Capture(window, $"telemetry-{page}-{tag}-full");
			}

			// A chart's values on hover, and a chart as a table.
			await ShowAsync(window, d, Pages.Overview);
			TimeChart chart = window.GetVisualDescendants().OfType<TimeChart>().First(c => c.IsEffectivelyVisible);
			chart.ShowValuesAt(20);
			d.Panels[1].ShowTable = true;
			Dispatcher.UIThread.RunJobs();
			Capture(window, $"telemetry-hover-table-{tag}");
			d.Panels[1].ShowTable = false;
		}

		// No middle dots and no long dashes in anything shown.
		foreach (TextBlock t in window.GetVisualDescendants().OfType<TextBlock>())
			Assert.DoesNotContain(t.Text ?? string.Empty, c => c is (char)0xB7 or (char)0x2014);
		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light", 1440, 900)]
	[InlineData("Dark", 1440, 900)]
	public async Task RendersThePartnerView(string theme, int width, int height)
	{
		(TelemetrySample s, _, MainWindow window, DashboardViewModel d) = await OpenAsync(theme, width, height, internalAccess: false);
		await using TelemetrySample owned = s;
		await ShowAsync(window, d, Pages.Overview);
		Capture(window, $"telemetry-partner-{theme.ToLowerInvariant()}-{width}");
		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light", 1440, 900)]
	[InlineData("Dark", 1440, 900)]
	[InlineData("Light", 760, 900)]
	public async Task RendersTheRailWithTwoCollectors(string theme, int width, int height)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		StandInTelemetry big = new(TelemetrySample.Start), small = new(TelemetrySample.Start, ["edge.example.org", "a.example.org"]) { Scale = 0.1, Stopped = null };
		await using TelemetrySample s = await TelemetrySample.CreateAsync([(Lab, big), ("edge.example.org", small)]);
		UseTemplates(s);
		MainWindowViewModel vm = await s.WindowModelAsync();
		await s.FindAsync();
		vm.ShowFeaturePlaceCommand.Execute(TelemetryFeature.PlaceKey);
		TelemetryPlaceViewModel place = Assert.IsType<TelemetryPlaceViewModel>(vm.FeatureContent);
		place.PickCommand.Execute(place.Collectors.Single(c => c.Name == "Lab").Key);
		MainWindow window = new() { DataContext = vm, Width = width, Height = height };
		window.Show();
		await place.Current!.LoadAsync(ask: true);
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(50);
		Capture(window, $"telemetry-rail-{theme.ToLowerInvariant()}-{width}");
		Assert.Equal("App telemetry", vm.WorkspaceTitle);
		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light", false)]
	[InlineData("Dark", false)]
	[InlineData("Light", true)]
	public async Task RendersAppNumbersOnTheBigScreen(string theme, bool two)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		List<(string, StandInTelemetry?)> neurons = [(Lab, new StandInTelemetry(TelemetrySample.Start))];
		if (two)
			neurons.Add(("edge.example.org", new StandInTelemetry(TelemetrySample.Start, ["edge.example.org", "a.example.org"]) { Scale = 0.1, Stopped = null }));
		await using TelemetrySample s = await TelemetrySample.CreateAsync(neurons);
		UseTemplates(s);
		MainWindowViewModel vm = await s.WindowModelAsync();
		await s.FindAsync();

		List<IBigScreenPage> added = [];
		s.Features.FeedBigScreen(added);
		BigScreenViewModel screen = new(vm.Fleet, s.Monitors.Hub, null, added);
		screen.Start();
		screen.Current = screen.Pages.Single(p => p.Key == TelemetryFeature.ScreenKey);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(["neurons", "problems", "app-numbers"], screen.Pages.Select(p => p.Key));

		Window window = new() { Content = new BigScreenView { DataContext = screen }, Width = 1280, Height = 720 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(50);
		Capture(window, $"telemetry-bigscreen-{theme.ToLowerInvariant()}" + (two ? "-two" : string.Empty));
		screen.Stop();
		window.Close();
	}

	[AvaloniaFact]
	public async Task WithNoPackageAnywhereNatShowsNothingOfIt()
	{
		Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

		// Two accounts, neither Neuron has the package: every question is answered 404.
		await using TelemetrySample s = await TelemetrySample.CreateAsync([(Lab, null), ("edge.example.org", null)]);
		UseTemplates(s);
		MainWindowViewModel vm = await s.WindowModelAsync();
		await s.FindAsync();
		Assert.NotEmpty(s.Requests);
		Assert.Empty(s.Feature.Collectors());

		Assert.Empty(vm.FeaturePlaces);
		vm.OpenNeuron(Lab, s.AccountOf[Lab]);
		Dispatcher.UIThread.RunJobs();
		Assert.Empty(vm.PageFeatureTabs);
		vm.Inbox.Refresh();
		Assert.DoesNotContain(vm.Inbox.Everything, r => r.IsFeatureItem);
		vm.Search.Open();
		vm.Search.Query = "NFC";
		Assert.Empty(vm.Search.Results);
		vm.Search.Close();

		List<IBigScreenPage> added = [];
		s.Features.FeedBigScreen(added);
		Assert.Empty(added);

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
			t => t.IsEffectivelyVisible && (t.Text ?? string.Empty).Contains("telemetry", StringComparison.OrdinalIgnoreCase));
		Capture(window, "telemetry-none-light-1440");
		window.Close();
	}
}
