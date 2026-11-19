using Neuro.Nat.Features.Telemetry.Api;

namespace Neuro.Nat.Features.Telemetry.Data;

/// <summary>An inclusive range of UTC reporting days.</summary>
public sealed record Period(DateTime From, DateTime To)
{
	public int Days => (this.To - this.From).Days + 1;
	public string Label => this.From == this.To ? Words.FullDay(this.To)
		: (this.From.Year == this.To.Year ? Words.Day(this.From) : Words.FullDay(this.From)) + " to " + Words.FullDay(this.To);
	public string Interval => this.Days >= 90 && this.From.Day == 1 ? "month"
		: this.Days >= 56 && this.From.DayOfWeek == DayOfWeek.Monday ? "week" : "day";
	public string IntervalTitle => this.Interval switch { "month" => "Month", "week" => "Week", _ => "Day" };

	/// <summary>The 30 days up to and including yesterday.</summary>
	public static Period Before(DateTime utcNow) => new(utcNow.Date.AddDays(-30), utcNow.Date.AddDays(-1));

	public Period Previous => new(this.From.AddDays(-this.Days), this.From.AddDays(-1));

	public static DateTime Monday(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
	public static DateTime MonthStart(DateTime day) => day.Date.AddDays(1 - day.Day);

	// The collector counts distinct accounts within a calendar bucket. Adding buckets would count people twice.
	public (Period Current, Period Prior, string Interval) Audience
	{
		get
		{
			DateTime month = MonthStart(this.To);
			if (this.Days >= 28 && month >= this.From)
				return (new(month, this.To), new(month.AddMonths(-1), this.To.AddMonths(-1)), "month");
			DateTime monday = Monday(this.To);
			if (this.Days >= 7 && monday >= this.From)
				return (new(monday, this.To), new(monday.AddDays(-7), this.To.AddDays(-7)), "week");
			return (new(this.To, this.To), new(this.To.AddDays(-1), this.To.AddDays(-1)), "day");
		}
	}
}

public sealed record ReportRange(string Title, Func<DateTime, Period>? Select)
{
	public static ReportRange Default { get; } = new("Last 30 days", Period.Before);
	public static IReadOnlyList<ReportRange> All { get; } =
	[
		new("Today", d => new(d, d)),
		new("Yesterday", d => new(d.AddDays(-1), d.AddDays(-1))),
		new("Last 7 days", d => new(d.AddDays(-7), d.AddDays(-1))),
		new("Last week", d => new(Period.Monday(d).AddDays(-7), Period.Monday(d).AddDays(-1))),
		Default,
		new("Last 12 full months", d => new(Period.MonthStart(d).AddMonths(-12), Period.MonthStart(d).AddDays(-1))),
		new("Custom dates", null),
	];
	public override string ToString() => this.Title;
}

/// <summary>Metric keys the dashboard knows by name. Anything else is found through /Catalog.</summary>
public static class M
{
	public const string Submitted = "Applications.Submitted";
	public const string Approved = "Applications.Approved";
	public const string Rejected = "Applications.Rejected";
	public const string Undecided = "Applications.Undecided";
	public const string ApprovalRate = "Applications.ApprovalRate";
	public const string RejectionReasons = "Applications.RejectionReasons";
	public const string Applicants = "Applications.Applicants";
	public const string Submissions = "Activity.Submissions";
	public const string Approvals = "Activity.Approvals";
	public const string Rejections = "Activity.Rejections";
	public const string Attempts = "Nfc.Attempts";
	public const string ReadsFailed = "Nfc.ReadsFailed";
	public const string ReadSuccessRate = "Nfc.ReadSuccessRate";
	public const string ErrorsBeforeRead = "Nfc.ErrorsBeforeRead";
	public const string JourneySuccessRate = "Nfc.JourneySuccessRate";
	public const string Users = "Nfc.Users";
	public const string StepsReached = "Nfc.StepsReached";
	public const string StepsContinued = "Nfc.StepsContinued";
	public const string StepsStopped = "Nfc.StepsStopped";
	public const string FailureDiagnostics = "Nfc.FailureDiagnostics";
	public const string UnreadableReports = "Nfc.UnreadableReports";
	public const string InvalidEvents = "DataQuality.InvalidEvents";
	public const string EventsWithoutAccount = "DataQuality.EventsWithoutAccount";
	public const string UnknownReadOutcomes = "DataQuality.UnknownReadOutcomes";
}

/// <summary>
/// Every question a dashboard asks, for one account's catalog and one period. A question the
/// account may not ask (a metric or dimension it cannot see) is null, and what would show it is
/// left out.
/// </summary>
public sealed class Questions(Catalog catalog, Period period)
{
	public Catalog Catalog => catalog;
	public Period Period => period;

	private TelemetryQuery? Ask(TelemetryQuery q) => q.NarrowTo(catalog);

	private TelemetryQuery? Total(Period p, string[] metrics, params string[] by)
		=> this.Ask(TelemetryQuery.Of(metrics).Over(p.From, p.To, "total").By(by));

	private TelemetryQuery? Daily(string[] metrics, params string[] by)
		=> this.Ask(TelemetryQuery.Of(metrics).Over(period.From, period.To, period.Interval).By(by));

	private static readonly string[] KeyTotals = [M.Submitted, M.ApprovalRate, M.ReadSuccessRate, M.JourneySuccessRate];
	private static readonly string[] PeopleCounts = [M.Applicants, M.Users];
	private static readonly string[] Decisions = [M.Approved, M.Rejected, M.Undecided];
	private static readonly string[] Quality = [M.InvalidEvents, M.EventsWithoutAccount, M.UnknownReadOutcomes];

	// ----- Overview -----
	public TelemetryQuery? Totals => this.Total(period, KeyTotals);
	public TelemetryQuery? TotalsBefore => this.Total(period.Previous, KeyTotals);
	public TelemetryQuery? Trend => this.Daily(KeyTotals);
	public TelemetryQuery? PeopleNow => this.People(period.Audience.Current);
	public TelemetryQuery? PeopleBefore => this.People(period.Audience.Prior);
	private TelemetryQuery? People(Period p) => this.Ask(TelemetryQuery.Of(PeopleCounts).Over(p.From, p.To, period.Audience.Interval));
	public TelemetryQuery? PlatformsNow => this.Total(period, [M.ReadSuccessRate], "platform");
	public TelemetryQuery? PlatformsBefore => this.Total(period.Previous, [M.ReadSuccessRate], "platform");
	public TelemetryQuery? Versions => this.Total(period, [M.ReadSuccessRate], "appVersion");
	public TelemetryQuery? ApprovalBefore => this.Total(period.Previous, [M.ApprovalRate]);
	public TelemetryQuery? Steps => this.Total(period, [M.StepsReached, M.StepsContinued], "step");
	public TelemetryQuery? Reasons => this.Total(period, [M.RejectionReasons], "rejectionReason");
	public TelemetryQuery? DataQuality => this.Total(period, Quality);
	public TelemetryQuery? DecisionsDaily => this.Daily(Decisions);
	public TelemetryQuery? PlatformsDaily => this.Daily([M.ReadSuccessRate], "platform");

	// ----- Applications -----
	public TelemetryQuery? ApprovalDaily => this.Daily([M.ApprovalRate]);
	public TelemetryQuery? ByMethod => this.Total(period, [M.Submitted, M.ApprovalRate], "method");
	public TelemetryQuery? ByNationality => this.Total(period, [M.Submitted], "nationality");
	public TelemetryQuery? Activity => this.Daily([M.Submissions, M.Approvals, M.Rejections]);

	// ----- NFC reading -----
	public TelemetryQuery? JourneysDaily => this.Daily([M.JourneySuccessRate], "platform");
	public TelemetryQuery? Outcomes => this.Total(period, [M.Attempts], "outcome");
	public TelemetryQuery? ReadsByNationality => this.Total(period, [M.ReadSuccessRate], "nationality");

	// ----- Troubleshooting -----
	public TelemetryQuery? FailureCodes => this.Total(period, [M.ReadsFailed], "failureCode");
	public TelemetryQuery? ErrorsBefore => this.Total(period, [M.ErrorsBeforeRead], "failureCode");
	public TelemetryQuery? StopReasons => this.Total(period, [M.StepsStopped], "stopReason");
	public TelemetryQuery? ChipResults => this.Total(period, [M.FailureDiagnostics], "chipResult");
	public TelemetryQuery? Unreadable => this.Total(period, [M.UnreadableReports], "failureCode");

	// ----- Neurons -----
	public TelemetryQuery? ByNeuron => this.Total(period, [M.Submitted, M.Attempts], TelemetryQuery.Neuron);

	// ----- All metrics -----

	/// <summary>One metric over the selected period, optionally split by a dimension.</summary>
	public TelemetryQuery? Explore(string metric, string? split)
	{
		CatalogMetric? m = catalog.Metric(metric);
		if (m is null)
			return null;

		TelemetryQuery q = TelemetryQuery.Of(metric).Over(period.From, period.To, period.Interval);
		if (split is not null)
			q = q.By(split);
		return this.Ask(q);
	}

	/// <summary>The same metric over the whole period, one number per group. Null for people counts.</summary>
	public TelemetryQuery? ExploreTotal(string metric, string split)
		=> catalog.Metric(metric) is { IsDistinct: false } ? this.Total(period, [metric], split) : null;

	/// <summary>What each page asks, in the order it is asked.</summary>
	public IReadOnlyList<TelemetryQuery> For(string page) => page switch
	{
		Pages.Overview => Some(this.Totals, this.TotalsBefore, this.Trend, this.PeopleNow, this.PeopleBefore, this.PlatformsNow, this.PlatformsBefore,
			this.Versions, this.ApprovalBefore, this.Steps, this.Reasons, this.DataQuality, this.DecisionsDaily, this.PlatformsDaily),
		Pages.Applications => Some(this.DecisionsDaily, this.ApprovalDaily, this.ByMethod, this.ByNationality, this.Reasons, this.Activity),
		Pages.Nfc => Some(this.PlatformsDaily, this.JourneysDaily, this.Outcomes, this.Steps, this.Versions, this.ReadsByNationality),
		Pages.Troubleshooting => Some(this.FailureCodes, this.ErrorsBefore, this.StopReasons, this.ChipResults, this.Unreadable, this.DataQuality),
		Pages.Neurons => Some(this.ByNeuron),
		_ => [],
	};

	private static List<TelemetryQuery> Some(params TelemetryQuery?[] qs) => [.. qs.OfType<TelemetryQuery>().DistinctBy(q => q.ToPath())];
}

/// <summary>The dashboard's pages, by key.</summary>
public static class Pages
{
	public const string Overview = "overview";
	public const string Applications = "applications";
	public const string Nfc = "nfc";
	public const string Troubleshooting = "troubleshooting";
	public const string Neurons = "neurons";
	public const string Metrics = "metrics";

	public static readonly IReadOnlyList<(string Key, string Title)> All =
	[
		(Overview, "Overview"),
		(Applications, "Applications"),
		(Nfc, "NFC reading"),
		(Troubleshooting, "Troubleshooting"),
		(Neurons, "Neurons"),
		(Metrics, "All metrics"),
	];

	public static string TitleOf(string key) => All.FirstOrDefault(p => p.Key == key).Title ?? key;
}
