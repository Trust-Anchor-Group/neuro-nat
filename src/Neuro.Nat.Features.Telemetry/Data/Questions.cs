using Neuro.Nat.Features.Telemetry.Api;

namespace Neuro.Nat.Features.Telemetry.Data;

/// <summary>The days a dashboard shows: the last 30 whole days (UTC), and the 30 before for comparison.</summary>
public sealed record Period(DateTime From, DateTime To)
{
	public const int Days = 30;

	/// <summary>The 30 days up to and including yesterday.</summary>
	public static Period Before(DateTime utcNow)
	{
		DateTime to = utcNow.Date.AddDays(-1);
		return new Period(to.AddDays(-(Days - 1)), to);
	}

	public Period Previous => new(this.From.AddDays(-Days), this.From.AddDays(-1));

	/// <summary>This month so far.</summary>
	public Period MonthSoFar => new(new DateTime(this.To.Year, this.To.Month, 1, 0, 0, 0, DateTimeKind.Utc), this.To);

	/// <summary>The same days of the month before.</summary>
	public Period MonthBefore
	{
		get
		{
			DateTime from = this.MonthSoFar.From.AddMonths(-1);
			DateTime to = from.AddDays((this.To - this.MonthSoFar.From).TotalDays);
			DateTime last = this.MonthSoFar.From.AddDays(-1);
			return new Period(from, to > last ? last : to);
		}
	}
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
		=> this.Ask(TelemetryQuery.Of(metrics).Over(period.From, period.To).By(by));

	private static readonly string[] KeyTotals = [M.Submitted, M.ApprovalRate, M.ReadSuccessRate, M.JourneySuccessRate];
	private static readonly string[] PeopleCounts = [M.Applicants, M.Users];
	private static readonly string[] Decisions = [M.Approved, M.Rejected, M.Undecided];
	private static readonly string[] Quality = [M.InvalidEvents, M.EventsWithoutAccount, M.UnknownReadOutcomes];

	// ----- Overview -----
	public TelemetryQuery? Totals => this.Total(period, KeyTotals);
	public TelemetryQuery? TotalsBefore => this.Total(period.Previous, KeyTotals);
	public TelemetryQuery? Trend => this.Daily(KeyTotals);
	public TelemetryQuery? PeopleNow => this.Ask(TelemetryQuery.Of(PeopleCounts).Over(period.MonthSoFar.From, period.MonthSoFar.To, "month"));
	public TelemetryQuery? PeopleBefore => this.Ask(TelemetryQuery.Of(PeopleCounts).Over(period.MonthBefore.From, period.MonthBefore.To, "month"));
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

	/// <summary>One metric over the period, split by a dimension or not. Daily, or weekly for people counts.</summary>
	public TelemetryQuery? Explore(string metric, string? split)
	{
		CatalogMetric? m = catalog.Metric(metric);
		if (m is null)
			return null;

		TelemetryQuery q = TelemetryQuery.Of(metric).Over(period.From, period.To, m.IsDistinct ? "week" : "day");
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
