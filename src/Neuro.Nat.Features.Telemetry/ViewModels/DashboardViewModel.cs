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
	private string? loadedPage;
	private int loads;
	private Period activePeriod;

	public DashboardViewModel(CollectorData data, string name, FeatureTarget target, Func<DateTime>? utcNow = null)
	{
		this.data = data;
		this.Name = name;
		this.target = target;
		this.utcNow = utcNow ?? (() => DateTime.UtcNow);
		this.activePeriod = Period.Before(this.utcNow());
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

	public IReadOnlyList<ReportRange> Ranges => ReportRange.All;
	public DateTime Today => this.utcNow().Date;
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsCustom))]
	private ReportRange range = ReportRange.Default;
	public bool IsCustom => this.Range.Select is null;
	[ObservableProperty, NotifyPropertyChangedFor(nameof(DateError)), NotifyCanExecuteChangedFor(nameof(ApplyDatesCommand))]
	private DateTime? customFrom;
	[ObservableProperty, NotifyPropertyChangedFor(nameof(DateError)), NotifyCanExecuteChangedFor(nameof(ApplyDatesCommand))]
	private DateTime? customTo;

	public string? DateError => this.ValidateDates(this.CustomFrom, this.CustomTo);

	private string? ValidateDates(DateTime? from, DateTime? to) => (from, to) switch
	{
		(null, _) or (_, null) => "Choose a start and an end date.",
		(DateTime f, DateTime t) when f > t => "The end date must be on or after the start date.",
		(_, DateTime t) when t > this.Today => "Choose an end date through today (UTC).",
		(DateTime f, DateTime t) when (f - DateTime.MinValue).Days < (t - f).Days + 1 => "Choose a later start date to allow a comparison.",
		_ => null,
	};
	private bool DatesValid => this.DateError is null;
	public string PeriodText => this.activePeriod.Label + " UTC";
	public string ComparisonText => "Compared with " + this.activePeriod.Previous.Label;

	partial void OnRangeChanged(ReportRange value)
	{
		if (value.Select is null)
		{
			this.CustomFrom = this.activePeriod.From;
			this.CustomTo = this.activePeriod.To;
		}
		else
			_ = this.LoadAsync(ask: true);
	}

	[RelayCommand(CanExecute = nameof(DatesValid))]
	private void ApplyDates()
	{
		if (this.DatesValid)
		{
			this.SetPeriod(new(this.CustomFrom!.Value.Date, this.CustomTo!.Value.Date));
			_ = this.LoadAsync(ask: true);
		}
	}

	private void SetPeriod(Period value)
	{
		if (this.activePeriod == value)
			return;
		this.activePeriod = value;
		this.IsLoading = true;
		this.HasNoNumbers = false;
		this.Problem = null;
		this.OnPropertyChanged(nameof(this.PeriodText));
		this.OnPropertyChanged(nameof(this.ComparisonText));
	}

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

	[ObservableProperty] private string emptyText = "No numbers available for this period.";

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

	private FeatureTarget LinkTarget
	{
		get
		{
			string part = this.IsMetrics && this.Metric?.Key is string m ? "metric:" + m + (this.Split?.Key is string s ? "/" + s : string.Empty) : this.Page;
			part += "~" + TelemetryQuery.Day(this.activePeriod.From) + "~" + TelemetryQuery.Day(this.activePeriod.To);
			return this.target with { Part = part };
		}
	}

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
		string[] dates = part.Split('~');
		if (dates.Length == 3 && TelemetryQuery.ParseDay(dates[1]) is DateTime from && TelemetryQuery.ParseDay(dates[2]) is DateTime to)
		{
			if (this.ValidateDates(from, to) is not null)
				return;
			this.Range = ReportRange.All.Single(r => r.Select is null);
			this.CustomFrom = from;
			this.CustomTo = to;
			this.SetPeriod(new(from, to));
			part = dates[0];
		}
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
		Period period = this.Range.Select?.Invoke(this.Today) ?? this.activePeriod;
		this.SetPeriod(period);
		if (this.loadedPage != this.Page)
		{
			this.IsLoading = true;
			this.HasNoNumbers = false;
		}
		if (ask && this.model is null)
			await this.LoadOnceAsync(ask: false, load, period);
		if (load == this.loads)
			await this.LoadOnceAsync(ask, load, period);
	}

	private async Task LoadOnceAsync(bool ask, int load, Period period)
	{
		string page = this.Page;
		List<TelemetryQuery> more = [];
		if (page == Pages.Metrics && this.model is DashboardModel m && this.Metric?.Key is string metric)
		{
			Questions questions = new(m.Catalog, period);
			if (questions.Explore(metric, this.Split?.Key) is TelemetryQuery x)
				more.Add(x);
			if (this.Split?.Key is string s && questions.ExploreTotal(metric, s) is TelemetryQuery t)
				more.Add(t);
		}

		Snapshot snap;
		try
		{
			snap = await Task.Run(() => this.data.GetAsync([page], more, ask, period: period));
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

		if (load != this.loads)
			return;
		// A restricted page falls back to Overview. All metrics needs the catalog before it can ask for a metric.
		if (this.Page != page || page == Pages.Metrics && more.Count == 0 && this.Metric?.Key is string chosen && this.model?.Questions.Explore(chosen, this.Split?.Key) is not null)
			await this.LoadOnceAsync(ask, load, period);
	}

	private void Apply(Snapshot snap, bool asked, int load)
	{
		if (load != this.loads)
			return;

		this.Problem = asked ? snap.Problem : this.Problem;
		if (snap.Model is not DashboardModel m)
		{
			if (asked)
			{
				this.IsLoading = false;
				this.HasNoNumbers = this.HasNoNumbers || this.model?.Period != this.activePeriod || this.loadedPage != this.Page;
				this.EmptyText = "No saved numbers for this period.";
			}

			return;
		}

		this.model = m;
		this.loadedPage = this.Page;
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

		DateTime? latest = m.Status?.Neurons.Max(n => n.LastDay);
		this.Freshness = latest is DateTime d ? "Latest reported day " + Words.FullDay(d) : "Latest reported day unknown";
		bool unpublished = latest < m.Period.From;
		this.HasNoNumbers = !this.IsNeurons && (unpublished || !snap.HasReportAnswer && !this.IsMetrics);
		this.EmptyText = unpublished ? "The collector has not published numbers for these dates yet." : "No numbers available for this period.";

		if (this.IsOverview)
		{
			Replace(this.KeyNumbers, m.KeyNumbers());
			IReadOnlyList<Finding> findings = m.Findings();
			this.NothingBroken = snap.Problem is null && !findings.Any(f => f.Level is "bad" or "busy");
			Replace(this.Findings, findings);
			Replace(this.Facts, m.Facts());
		}
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
		string first = m.Period.IntervalTitle;
		this.ExploreChart = new ChartPanelViewModel(new PanelSpec("explore", metric.Name, split is null ? null : "The six largest", m.Explore(key, split), first, false));
		this.ExploreTotal = split is null || metric.IsDistinct ? null
			: new ChartPanelViewModel(new PanelSpec("exploreTotal", "Totals for the selected period", null, m.ExploreTotal(key, split), Words.Dimension(split), false));
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
	private void ExploreMetric(string metric) => this.ShowPart("metric:" + metric);

	[RelayCommand]
	private void Go(string? page)
	{
		if (page is not null)
			this.ShowPage(page);
	}
}
