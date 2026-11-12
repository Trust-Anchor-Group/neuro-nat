using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.App;
using Neuro.Nat.App.Features;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Features.Telemetry.Api;
using Neuro.Nat.Features.Telemetry.Charts;
using Neuro.Nat.Features.Telemetry.Data;

namespace Neuro.Nat.Features.Telemetry.ViewModels;

/// <summary>A page of the dashboard in the row at its top.</summary>
public sealed partial class DashboardPageItem(string key, string title) : ObservableObject
{
	public string Key { get; } = key;
	public string Title { get; } = title;

	[ObservableProperty] private bool isCurrent;
}

/// <summary>A colour and a name under a chart.</summary>
public sealed record LegendItem(string Name, int Slot);

/// <summary>One chart, which can also be shown as a table.</summary>
public sealed partial class ChartPanelViewModel : ObservableObject
{
	public ChartPanelViewModel(PanelSpec spec)
	{
		this.Key = spec.Key;
		this.Title = spec.Title;
		this.Hint = spec.Hint;
		this.TeamOnly = spec.TeamOnly;
		this.Data = spec.Data;
		this.TableFirst = spec.TableFirst;
		this.Legend = spec.Data.Series.Count > 1 && spec.Data.Kind != ChartKind.Across ? [.. spec.Data.Series.Select(s => new LegendItem(s.Name, s.Slot))] : [];
	}

	public string Key { get; }
	public string Title { get; }
	public string? Hint { get; }
	public bool HasHint => !string.IsNullOrEmpty(this.Hint);
	public bool TeamOnly { get; }
	public ChartData Data { get; }
	public string TableFirst { get; }
	public IReadOnlyList<LegendItem> Legend { get; }
	public bool HasLegend => this.Legend.Count > 0 || this.Data.SettleFrom >= 0;
	public bool IsSettling => this.Data.SettleFrom >= 0;
	public bool HasNumbers => !this.Data.IsEmpty;
	public bool IsEmpty => this.Data.IsEmpty;
	public bool IsTime => this.Data.Kind != ChartKind.Across;
	public bool IsAcross => this.Data.Kind == ChartKind.Across;

	/// <summary>The numbers as a table instead of the chart.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowChart), nameof(TableText), nameof(ShowTime), nameof(ShowAcross), nameof(ShowNumbersTable))]
	private bool showTable;

	public bool ShowChart => !this.ShowTable && this.HasNumbers;
	public bool ShowTime => this.ShowChart && this.IsTime;
	public bool ShowAcross => this.ShowChart && this.IsAcross;
	public bool ShowNumbersTable => this.ShowTable && this.HasNumbers;

	public string TableText => this.ShowTable ? "Show chart" : "Show table";

	[RelayCommand]
	private void ToggleTable() => this.ShowTable = !this.ShowTable;
}

/// <summary>A choice in a picker on the All metrics page.</summary>
public sealed record Choice(string? Key, string Title)
{
	public override string ToString() => this.Title;
}

/// <summary>
/// One collector's dashboard: pages of numbers about how the app is used, from that collector
/// only. It asks when it opens and at most once an hour after that (the fetcher sees to it).
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, IFeatureContent
{
	private readonly CollectorData data;
	private readonly Func<DateTime> utcNow;
	private readonly FeatureTarget target;
	private DashboardModel? model;
	private int loads;

	public DashboardViewModel(CollectorData data, string name, FeatureTarget target, Func<DateTime>? utcNow = null)
	{
		this.data = data;
		this.Name = name;
		this.target = target;
		this.utcNow = utcNow ?? (() => DateTime.UtcNow);
		foreach ((string key, string title) in Pages.All)
			this.AllPages.Add(new DashboardPageItem(key, title));
		this.PagesShown.Add(this.AllPages[0]);
		this.AllPages[0].IsCurrent = true;
	}

	public TelemetryCollector Collector => this.data.Collector;

	/// <summary>The collector's name, as Neurons are named.</summary>
	public string Name { get; }

	private List<DashboardPageItem> AllPages { get; } = [];

	/// <summary>The pages this account sees.</summary>
	public ObservableCollection<DashboardPageItem> PagesShown { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsNeurons), nameof(IsMetrics), nameof(IsChartPage), nameof(PageTitle))]
	private string page = Pages.Overview;

	public bool IsOverview => this.Page == Pages.Overview;
	public bool IsNeurons => this.Page == Pages.Neurons;
	public bool IsMetrics => this.Page == Pages.Metrics;
	public bool IsChartPage => !this.IsOverview && !this.IsMetrics;
	public string PageTitle => Pages.TitleOf(this.Page);

	// ----- State -----

	/// <summary>True until the first numbers are there.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowsNumbers))]
	private bool isLoading = true;

	/// <summary>Nothing could be read, and nothing was kept.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowsNumbers))]
	private bool hasNoNumbers;

	public bool ShowsNumbers => !this.IsLoading && !this.HasNoNumbers;

	/// <summary>What went wrong the last time the Neuron was asked.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	private string? problem;

	public bool HasProblem => !string.IsNullOrEmpty(this.Problem);

	/// <summary>"Numbers through 11 Nov 2026"</summary>
	[ObservableProperty] private string freshness = string.Empty;

	/// <summary>"Last 30 days: 13 Oct to 11 Nov"</summary>
	[ObservableProperty] private string periodText = string.Empty;

	// ----- Overview -----

	public ObservableCollection<KeyNumber> KeyNumbers { get; } = [];

	public ObservableCollection<Finding> Findings { get; } = [];

	[ObservableProperty] private bool nothingBroken;

	public ObservableCollection<Fact> Facts { get; } = [];

	// ----- Pages with charts -----

	public ObservableCollection<ChartPanelViewModel> Panels { get; } = [];

	/// <summary>The charts in two columns, each as tall as its own charts: the first, third and on.</summary>
	public ObservableCollection<ChartPanelViewModel> PanelsLeft { get; } = [];

	/// <summary>The second, fourth and on.</summary>
	public ObservableCollection<ChartPanelViewModel> PanelsRight { get; } = [];

	public ObservableCollection<NeuronRow> NeuronRows { get; } = [];

	// ----- All metrics -----

	public ObservableCollection<Choice> MetricChoices { get; } = [];

	public ObservableCollection<Choice> SplitChoices { get; } = [];

	[ObservableProperty] private Choice? metric;

	[ObservableProperty] private Choice? split;

	[ObservableProperty] private ChartPanelViewModel? exploreChart;

	[ObservableProperty] private ChartPanelViewModel? exploreTotal;

	[ObservableProperty] private string exploreAbout = string.Empty;

	/// <summary>The exact question asked, as the API takes it.</summary>
	[ObservableProperty] private string exploreQuery = string.Empty;

	private bool choosing;

	partial void OnMetricChanged(Choice? value)
	{
		if (this.choosing)
			return;
		this.FillSplits();
		_ = this.LoadAsync(ask: true);
	}

	partial void OnSplitChanged(Choice? value)
	{
		if (!this.choosing)
			_ = this.LoadAsync(ask: true);
	}

	// ----- Copy link -----

	/// <summary>True when the shell makes links (it does not yet).</summary>
	public bool CanCopyLink => FeatureLinks.Make(this.LinkTarget) is not null;

	private FeatureTarget LinkTarget => this.target with { Part = this.IsMetrics && this.Metric?.Key is string m ? "metric:" + m + (this.Split?.Key is string s ? "/" + s : string.Empty) : this.Page };

	[RelayCommand]
	private async Task CopyLink()
	{
		if (FeatureLinks.Make(this.LinkTarget) is not string link)
			return;

		await Ui.Copy(link, "link");
	}

	// ----- Showing -----

	[RelayCommand]
	private void ShowPage(string? key)
	{
		if (key is null || !this.PagesShown.Any(p => p.Key == key))
			return;

		this.Page = key;
		foreach (DashboardPageItem p in this.AllPages)
			p.IsCurrent = p.Key == key;
		this.OnPropertyChanged(nameof(this.CanCopyLink));
		_ = this.LoadAsync(ask: true);
	}

	/// <summary>Shows a page ("nfc"), or a metric on All metrics ("metric:Nfc.Attempts/platform").</summary>
	public void ShowPart(string part)
	{
		if (part.StartsWith("metric:", StringComparison.Ordinal))
		{
			string[] bits = part["metric:".Length..].Split('/', 2);
			this.choosing = true;
			this.wanted = (bits[0], bits.Length > 1 ? bits[1] : null);
			this.ApplyWanted();
			this.choosing = false;
			this.Page = Pages.Metrics;
			foreach (DashboardPageItem p in this.AllPages)
				p.IsCurrent = p.Key == Pages.Metrics;
			_ = this.LoadAsync(ask: true);
		}
		else if (Pages.All.Any(p => p.Key == part))
		{
			if (!this.PagesShown.Any(p => p.Key == part) && this.model is null)
			{
				// Not known yet whether the account sees it: go there once the catalog is in.
				this.Page = part;
				_ = this.LoadAsync(ask: true);
				return;
			}

			this.ShowPage(part);
		}
	}

	private (string Metric, string? Split)? wanted;

	private void ApplyWanted()
	{
		if (this.wanted is not { } w || !this.MetricChoices.Any(c => c.Key == w.Metric))
			return;

		(string m, string? s) = w;
		this.Metric = this.MetricChoices.First(c => c.Key == m);
		this.FillSplits();
		this.Split = this.SplitChoices.FirstOrDefault(c => c.Key == s) ?? this.SplitChoices.FirstOrDefault();
		this.wanted = null;
	}

	/// <summary>The dashboard came on screen: show what was kept, then ask when an hour has passed.</summary>
	public void Shown() => _ = this.LoadAsync(ask: true);

	/// <summary>Loads the page on screen. First from what was kept, then by asking. For tests, awaitable.</summary>
	public async Task LoadAsync(bool ask)
	{
		int load = Interlocked.Increment(ref this.loads);
		if (this.model is null)
			await this.LoadOnceAsync(ask: false, load);
		await this.LoadOnceAsync(ask, load);
	}

	private async Task LoadOnceAsync(bool ask, int load)
	{
		string page = this.Page;
		List<TelemetryQuery> more = [];
		if (page == Pages.Metrics && this.model is DashboardModel m && this.Metric?.Key is string metric)
		{
			if (m.Questions.Explore(metric, this.Split?.Key) is TelemetryQuery x)
				more.Add(x);
			if (this.Split?.Key is string s && m.Questions.ExploreTotal(metric, s) is TelemetryQuery t)
				more.Add(t);
		}

		Snapshot snap;
		try
		{
			snap = await Task.Run(() => this.data.GetAsync(page == Pages.Overview ? [Pages.Overview] : [Pages.Overview, page], more, ask));
		}
		catch (Exception ex)
		{
			AppLog.Error("App telemetry could not be read.", ex);
			snap = new Snapshot(null, "Could not read the numbers", null);
		}

		if (Dispatcher.UIThread.CheckAccess())
			this.Apply(snap, ask, load);
		else
			await Dispatcher.UIThread.InvokeAsync(() => this.Apply(snap, ask, load));

		// The All metrics page needs the catalog for its choices; then the chosen metric's numbers.
		if (page == Pages.Metrics && more.Count == 0 && this.Metric?.Key is string chosen && this.model?.Questions.Explore(chosen, this.Split?.Key) is not null)
			await this.LoadOnceAsync(ask, load);
	}

	private void Apply(Snapshot snap, bool asked, int load)
	{
		if (load != this.loads && !asked)
			return;

		this.Problem = asked ? snap.Problem : this.Problem;
		if (snap.Model is not DashboardModel m)
		{
			if (asked)
			{
				this.IsLoading = false;
				this.HasNoNumbers = this.model is null;
			}

			return;
		}

		this.model = m;
		this.IsLoading = false;
		this.HasNoNumbers = false;

		// The pages this account may see; one it may not see is left out, not shown as an error.
		List<string> visible = [.. m.VisiblePages().Select(p => p.Key)];
		List<DashboardPageItem> shown = [.. this.AllPages.Where(p => visible.Contains(p.Key))];
		if (!shown.SequenceEqual(this.PagesShown))
		{
			this.PagesShown.Clear();
			foreach (DashboardPageItem p in shown)
				this.PagesShown.Add(p);
		}

		if (!visible.Contains(this.Page))
			this.Page = Pages.Overview;
		foreach (DashboardPageItem p in this.AllPages)
			p.IsCurrent = p.Key == this.Page;

		StatusNeuron? self = m.Status?.Self;
		this.Freshness = self?.LastDay is DateTime d ? "Numbers through " + Words.FullDay(d) : "No numbers yet";
		this.PeriodText = "Last 30 days, " + Words.Day(m.Period.From) + " to " + Words.Day(m.Period.To);

		Replace(this.KeyNumbers, m.KeyNumbers());
		List<Finding> findings = [.. m.Findings()];
		this.NothingBroken = !findings.Any(f => f.Level is "bad" or "busy");
		Replace(this.Findings, findings);
		Replace(this.Facts, m.Facts());
		Replace(this.NeuronRows, this.Page == Pages.Neurons ? m.NeuronRows() : []);

		List<ChartPanelViewModel> panels = [.. m.Panels(this.Page).Select(p => new ChartPanelViewModel(p))];
		List<ChartPanelViewModel> shownPanels = [.. panels.Where(p => p.HasNumbers || !this.IsOverview)];
		Replace(this.Panels, shownPanels);
		Replace(this.PanelsLeft, shownPanels.Where((_, i) => i % 2 == 0).ToList());
		Replace(this.PanelsRight, shownPanels.Where((_, i) => i % 2 == 1).ToList());

		this.FillMetrics(m);
		if (this.IsMetrics)
			this.FillExplore(m);
		this.OnPropertyChanged(nameof(this.CanCopyLink));
	}

	private void FillMetrics(DashboardModel m)
	{
		List<Choice> metrics = [.. m.Catalog.Metrics.Where(x => !x.Deprecated).Select(x => new Choice(x.Key, x.Name))];
		if (metrics.Select(c => c.Key).SequenceEqual(this.MetricChoices.Select(c => c.Key)))
			return;

		this.choosing = true;
		Replace(this.MetricChoices, metrics);
		this.Metric = this.MetricChoices.FirstOrDefault(c => c.Key == (this.Metric?.Key ?? M.Attempts)) ?? this.MetricChoices.FirstOrDefault();
		this.FillSplits();
		this.ApplyWanted();
		this.choosing = false;
	}

	private void FillSplits()
	{
		if (this.model is not DashboardModel m || this.Metric?.Key is not string key || m.Catalog.Metric(key) is not CatalogMetric metric)
			return;

		List<Choice> splits = metric.Total ? [new Choice(null, "Not split")] : [];
		splits.AddRange(metric.Dimensions.Where(m.Catalog.Dimensions.ContainsKey).Select(d => new Choice(d, "By " + Words.Dimension(d).ToLowerInvariant())));
		if (m.Catalog.HasNeurons)
			splits.Add(new Choice(TelemetryQuery.Neuron, "By Neuron"));

		bool was = this.choosing;
		this.choosing = true;
		string? keep = this.Split?.Key;
		Replace(this.SplitChoices, splits);
		this.Split = this.SplitChoices.FirstOrDefault(c => c.Key == keep) ?? this.SplitChoices.FirstOrDefault();
		this.choosing = was;
	}

	private void FillExplore(DashboardModel m)
	{
		if (this.Metric?.Key is not string key || m.Catalog.Metric(key) is not CatalogMetric metric)
			return;

		string? split = this.Split?.Key;
		string first = metric.IsDistinct ? "Week" : "Day";
		this.ExploreChart = new ChartPanelViewModel(new PanelSpec("explore", metric.Name, split is null ? null : "The six largest", m.Explore(key, split), first, false));
		this.ExploreTotal = split is null || metric.IsDistinct ? null
			: new ChartPanelViewModel(new PanelSpec("exploreTotal", "Last 30 days in all", null, m.ExploreTotal(key, split), Words.Dimension(split), false));
		this.ExploreAbout = metric.Description ?? string.Empty;
		this.ExploreQuery = m.Questions.Explore(key, split)?.ToPath() ?? string.Empty;
	}

	private static void Replace<T>(ObservableCollection<T> list, IReadOnlyList<T> items)
	{
		if (list.SequenceEqual(items))
			return;

		list.Clear();
		foreach (T item in items)
			list.Add(item);
	}

	[RelayCommand]
	private void Go(string? page)
	{
		if (page is not null)
			this.ShowPage(page);
	}
}
