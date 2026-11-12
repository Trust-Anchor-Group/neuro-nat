using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Concentrator;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// Drives the Sources workspace against the local test Neuron: browse to a node, open a
/// report's form, run it, and open a node's edit form. Saves frames for review. Needs the
/// NAT_TEST_* variables; does nothing without them.
/// </summary>
[Collection("Live Neuron")]
public class LiveSourcesTests
{
	private static async Task Until(Func<bool> condition, int timeoutMs = 30000)
	{
		long end = Environment.TickCount64 + timeoutMs;
		while (!condition())
		{
			if (Environment.TickCount64 > end)
				throw new TimeoutException("Condition not met in time.");
			Dispatcher.UIThread.RunJobs();
			await Task.Delay(50);
		}
	}

	private static async Task<T> Find<T>(TreeItemViewModel parent, string label) where T : TreeItemViewModel
	{
		parent.IsExpanded = true;
		await Until(() => parent.Children.OfType<T>().Any(c => c.Label == label));
		return parent.Children.OfType<T>().First(c => c.Label == label);
	}

	[AvaloniaFact]
	public async Task BrowsesRunsAReportAndOpensAnEditForm()
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? neuron = Environment.GetEnvironmentVariable("NAT_TEST_NEURON");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(neuron))
			Live.Skip("NAT_TEST_HOST", "NAT_TEST_USER", "NAT_TEST_PASSWORD", "NAT_TEST_NEURON");

		Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.LiveUi." + Guid.NewGuid().ToString("N"), "accounts.json");
		await using AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true, ConnectOnStartup = false },
			AccountSecret.FromPassword(password), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		MainWindowViewModel vm = new(manager, new FleetService(monitors));
		await vm.StartAsync();
		await s.ConnectAsync();

		await Until(() => monitors.For(s.Profile.Id)?.Concentrator(neuron) is not null);
		// Sources is a tab of the Neuron's page.
		vm.OpenNeuron(neuron, s.Profile.Id, WorkspaceKind.Sources);
		SourcesViewModel src = vm.Sources;
		await Until(() => src.Roots.Count > 0);

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();

		SourceItemViewModel reports = src.Roots.OfType<SourceItemViewModel>().First(r => r.Source.SourceID == "Reports");
		NodeItemViewModel events = await Find<NodeItemViewModel>(reports, "Events");
		NodeItemViewModel search = await Find<NodeItemViewModel>(events, "Search Event Log");
		src.SelectedItem = search;
		await Until(() => src.HasCommands);

		await src.RunCommand.ExecuteAsync(src.Commands.First());
		await Until(() => src.IsForm && src.Form is not null);
		if (src.Form!["MaxCount"] is Waher.Networking.XMPP.DataForms.Field max)
			await max.SetValue("40");
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(200);
		Save(window, "sources-live-report-form");

		await src.SubmitFormCommand.ExecuteAsync(null);
		await Until(() => src.IsReport && src.Report is not null && src.Report.State != QueryRunState.Running);
		await Task.Delay(500);
		Dispatcher.UIThread.RunJobs();
		Save(window, "sources-live-report");
		Assert.Equal(QueryRunState.Done, src.Report!.State);
		Assert.True(src.Report.RowCount > 0);

		SourceItemViewModel metering = src.Roots.OfType<SourceItemViewModel>().First(r => r.Source.SourceID == "MeteringTopology");
		NodeItemViewModel root = await Find<NodeItemViewModel>(metering, "Root");
		NodeItemViewModel web = await Find<NodeItemViewModel>(root, "Local Web Server");
		src.SelectedItem = web;
		await src.EditCommand.ExecuteAsync(null);
		await Until(() => src.IsForm && src.Form is not null);
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(200);
		Save(window, "sources-live-edit");

		src.BackToNodeCommand.Execute(null);

		// Sniff the web server and make a request.
		await src.SniffCommand.ExecuteAsync(null);
		await Until(() => src.SelectedSniffer?.IsLive == true);
		string site = Environment.GetEnvironmentVariable("NAT_NEURON_WEB") ?? "http://localhost:8081";
		string marker = "nat-ui-" + Guid.NewGuid().ToString("N")[..6];
		using (HttpClient http = new())
		{
			try { await http.GetAsync(site + "/" + marker); } catch (HttpRequestException) { }
		}
		await Until(() => src.SelectedSniffer!.Lines.Lines.Any(l => l.Text.Contains(marker)));
		await Task.Delay(300);
		Save(window, "sources-live-sniffer");

		// Search every source.
		src.SearchText = "Event";
		await src.SearchCommand.ExecuteAsync(null);
		await Until(() => !src.IsSearching);
		Assert.NotEmpty(src.SearchResults);
		src.SearchSelection = src.SearchResults[0];
		await Task.Delay(300);
		Save(window, "sources-live-search");

		await src.StopSniffersAsync();
		window.Close();
	}

	/// <summary>
	/// The Performance "All" report: the Neuron sends charts as images and some text as Markdown,
	/// and on a Neuron without samples its chart script fails. Charts and Markdown must show as
	/// such, and the Neuron's errors in the section they belong to.
	/// </summary>
	[AvaloniaFact]
	public async Task ShowsPerformanceChartsAndErrorsInPlace()
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? neuron = Environment.GetEnvironmentVariable("NAT_TEST_NEURON");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(neuron))
			Live.Skip("NAT_TEST_HOST", "NAT_TEST_USER", "NAT_TEST_PASSWORD", "NAT_TEST_NEURON");

		Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.LiveUi." + Guid.NewGuid().ToString("N"), "accounts.json");
		await using AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true, ConnectOnStartup = false },
			AccountSecret.FromPassword(password), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		MainWindowViewModel vm = new(manager, new FleetService(monitors));
		await vm.StartAsync();
		await s.ConnectAsync();

		await Until(() => monitors.For(s.Profile.Id)?.Concentrator(neuron) is not null);
		// Sources is a tab of the Neuron's page.
		vm.OpenNeuron(neuron, s.Profile.Id, WorkspaceKind.Sources);
		SourcesViewModel src = vm.Sources;
		await Until(() => src.Roots.Count > 0);

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();

		SourceItemViewModel reports = src.Roots.OfType<SourceItemViewModel>().First(r => r.Source.SourceID == "Reports");
		NodeItemViewModel performance = await Find<NodeItemViewModel>(reports, "Performance");
		NodeItemViewModel all = await Find<NodeItemViewModel>(performance, "All");
		src.SelectedItem = all;
		await Until(() => src.HasCommands);

		await src.RunCommand.ExecuteAsync(src.Commands.First());
		if (src.IsForm)
			await src.SubmitFormCommand.ExecuteAsync(null);
		await Until(() => src.IsReport && src.Report is not null && src.Report.State != QueryRunState.Running, 60000);
		await Task.Delay(800);
		Dispatcher.UIThread.RunJobs();
		Save(window, "sources-live-performance");

		QueryRun run = src.Report!;
		Assert.Equal(QueryRunState.Done, run.State);

		// Errors from the Neuron's chart script belong to the section it was drawing.
		Assert.All(run.Messages.Where(m => m.Type is Waher.Things.Queries.QueryEventType.Exception or Waher.Things.Queries.QueryEventType.Error),
			m => Assert.NotNull(m.Section));

		// No object is shown by its .NET type name any more.
		static IEnumerable<Waher.Networking.XMPP.Concentrator.Queries.QueryObject> Objects(IEnumerable<Waher.Networking.XMPP.Concentrator.Queries.QueryItem> items)
			=> items.SelectMany(i => i is Waher.Networking.XMPP.Concentrator.Queries.QuerySection sec ? Objects(sec.Content)
				: i is Waher.Networking.XMPP.Concentrator.Queries.QueryObject o ? [o] : []);
		List<Waher.Networking.XMPP.Concentrator.Queries.QueryObject> objects = [.. Objects(run.Items)];
		Assert.NotEmpty(objects);
		Assert.Contains(objects, o => o.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
		IEnumerable<string> texts = window.GetVisualDescendants().OfType<Controls.ReportView>().SelectMany(r => r.GetVisualDescendants()).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text ?? string.Empty);
		Assert.DoesNotContain(texts, t => t.StartsWith("Waher.", StringComparison.Ordinal));
		Assert.NotEmpty(window.GetVisualDescendants().OfType<Avalonia.Controls.Image>());

		window.Close();
	}

	private static void Save(MainWindow window, string name)
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
}
