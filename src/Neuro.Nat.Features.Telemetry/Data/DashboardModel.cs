using Neuro.Nat.Features.Telemetry.Api;
using Neuro.Nat.Features.Telemetry.Charts;

namespace Neuro.Nat.Features.Telemetry.Data;

/// <summary>A key number with its trend, at the top of the overview and on the big screen.</summary>
public sealed record KeyNumber(string Metric, string Label, string Value, string Unit, string Delta, string Tone, string Arrow, string Hint)
{
	public IReadOnlyList<double?>? Trend { get; init; }

	/// <summary>The change alone, such as "6%" or "1.4 points", where there is no room for what it is against.</summary>
	public string Change { get; init; } = string.Empty;
	public bool HasTrend => this.Trend is { Count: > 1 };

	/// <summary>The icon beside the change: TrendUp, TrendDown or Forward (no change).</summary>
	public string? ArrowIcon => this.Arrow switch { "up" => "TrendUp", "down" => "TrendDown", "flat" => "Forward", _ => null };
}

/// <summary>
/// Something in the numbers worth acting on. <see cref="Kind"/> says which rule found it:
/// neuron (a Neuron is behind or failing), rate (a success rate fell), version (an app version
/// reads worse), and the quieter approval, missing, step, reason and quality.
/// </summary>
public sealed record Finding(string Kind, string Level, string Title, string Detail, string? Page, string Subject)
{
	/// <summary>The page it leads to, by name.</summary>
	public string? PageTitle => this.Page is null ? null : Pages.TitleOf(this.Page);
}

/// <summary>A fact about how fresh and complete the numbers are.</summary>
public sealed record Fact(string Name, string Value, string Tone = "");

/// <summary>A Neuron the numbers come from, and how up to date it is.</summary>
public sealed record NeuronRow(string Neuron, string Name, bool Own, string LastDay, string Label, string Tone, string? Todo, string? Error, int? Behind, string Collected);

/// <summary>One chart of a page.</summary>
public sealed record PanelSpec(string Key, string Title, string? Hint, ChartData Data, string TableFirst, bool TeamOnly);

/// <summary>
/// Turns a collector's answers into what its dashboard shows. Pure: the same answers give the
/// same pages, so it is tested with stand-in answers. Only one collector's answers are ever in one
/// model, so two collectors' numbers are never mixed.
/// </summary>
public sealed class DashboardModel(Questions q, Func<TelemetryQuery?, QueryAnswer> answer, StatusAnswer? status, Func<string, string> nameOf, DateTime utcNow)
{
	/// <summary>A success rate that fell at least this much (points) against the 30 days before is worth a look.</summary>
	public const double RateFall = 1.5;

	/// <summary>An app version that reads at least this much (points) worse than the best one is worth a look.</summary>
	public const double VersionGap = 3;

	/// <summary>An app version needs at least this many NFC reads in the period to be compared.</summary>
	public const double VersionReads = 50;

	/// <summary>An approval rate that fell at least this much (points) is worth a look.</summary>
	public const double ApprovalFall = 2;

	public Questions Questions => q;
	public Catalog Catalog => q.Catalog;
	public Period Period => q.Period;
	public StatusAnswer? Status => status;

	private QueryAnswer A(TelemetryQuery? query) => query is null ? QueryAnswer.Empty : answer(query);

	// ----- Pages -----

	/// <summary>The pages this account sees: a page with nothing the account may read is left out.</summary>
	public IReadOnlyList<(string Key, string Title)> VisiblePages()
		=> [.. Pages.All.Where(p => p.Key switch
		{
			Pages.Overview => true,
			Pages.Neurons => this.Catalog.HasNeurons,
			Pages.Metrics => this.Catalog.Metrics.Count > 0,
			_ => q.For(p.Key).Count > 0,
		})];

	// ----- Key numbers -----

	private sealed record KeySpec(string Metric, string Label, bool Percent, bool People, string Hint);

	private static readonly KeySpec[] Keys =
	[
		new(M.Submitted, "Applications", false, false, "First sent in the period"),
		new(M.ApprovalRate, "Approval rate", true, false, "Approved of decided"),
		new(M.ReadSuccessRate, "NFC read success", true, false, "Reads that worked once started"),
		new(M.JourneySuccessRate, "NFC completion", true, false, "People who got a read in the end"),
		new(M.Applicants, "Applicants", false, true, "This month, against last month"),
		new(M.Users, "NFC users", false, true, "This month, against last month"),
	];

	public IReadOnlyList<KeyNumber> KeyNumbers()
	{
		QueryAnswer now = this.A(q.Totals), before = this.A(q.TotalsBefore), trend = this.A(q.Trend);
		QueryAnswer peopleNow = this.A(q.PeopleNow), peopleBefore = this.A(q.PeopleBefore);
		List<KeyNumber> list = [];
		foreach (KeySpec k in Keys.Where(k => this.Catalog.Has(k.Metric)))
		{
			double? v = k.People ? peopleNow.LastOf(k.Metric) : now.TotalOf(k.Metric);
			double? was = k.People ? peopleBefore.LastOf(k.Metric) : before.Complete ? before.TotalOf(k.Metric) : null;
			(string delta, string change, string tone, string arrow) = Delta(v, was, k.Percent, k.People ? "vs last month" : "vs 30 days before");
			list.Add(new KeyNumber(k.Metric, k.Label, v is double d ? (k.Percent ? d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : Words.N(d)) : "None",
				k.Percent && v is not null ? "%" : string.Empty, delta, tone, arrow, k.Hint)
			{
				Trend = k.People ? null : trend.Find(k.Metric)?.Points.Select(p => p.Value).ToList(),
				Change = change,
			});
		}

		return list;
	}

	private static (string Delta, string Change, string Tone, string Arrow) Delta(double? now, double? before, bool percent, string against)
	{
		if (now is not double n || before is not double b)
			return ("Nothing to compare yet", "No change known", "idle", string.Empty);

		if (percent)
		{
			double d = n - b;
			string arrow = Math.Abs(d) < 0.05 ? "flat" : d > 0 ? "up" : "down";
			string tone = Math.Abs(d) < 0.5 ? "idle" : d > 0 ? "ok" : "bad";
			return (Words.Pts(Math.Abs(d)) + " " + against, Words.Pts(Math.Abs(d)), tone, arrow);
		}

		if (b == 0)
			return ("Nothing to compare yet", "No change known", "idle", string.Empty);

		double pct = 100 * (n - b) / b;
		string change = Math.Abs(pct).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
		return (change + " " + against, change, "idle", Math.Abs(pct) < 0.5 ? "flat" : pct > 0 ? "up" : "down");
	}

	// ----- Neurons -----

	public IReadOnlyList<NeuronRow> NeuronRows()
	{
		DateTime yesterday = utcNow.Date.AddDays(-1);
		List<NeuronRow> rows = [];
		foreach (StatusNeuron n in status?.Neurons ?? [])
		{
			bool own = n.IsSelf;
			DateTime? last = n.LastDay;
			string? error = own ? n.LastComputeError : n.LastCollectError;
			int? behind = last is DateTime l ? (int)Math.Round((yesterday - l).TotalDays) : null;
			string label = "Up to date", tone = "ok";
			string? todo = null;
			if (last is null)
			{
				(label, tone) = ("No numbers yet", "busy");
				todo = own ? "Wait 5 minutes after start, or press Compute now on its settings page." : "Press Collect now on the root's settings page.";
			}
			else if (behind > 0 && error is not null)
			{
				(label, tone) = ("Failing", "bad");
				todo = own ? "Press Compute now on its settings page, and read its event log if it fails again." : "Check that the root can reach it and that it allows the root, then press Collect now.";
			}
			else if (behind > 0)
			{
				(label, tone) = (behind == 1 ? "1 day behind" : $"{behind} days behind", behind >= 2 ? "bad" : "busy");
				todo = own ? "The nightly count has not run yet. Press Compute now to run it." : "The root has not collected yet. Press Collect now on the root.";
			}

			if (n.MetricsVersion is int v && v != this.Catalog.MetricsVersion)
			{
				if (tone == "ok")
					(label, tone) = ("Older package", "busy");
				todo = $"Runs metrics version {v}, the root runs {this.Catalog.MetricsVersion}. Update the package on it.";
			}

			DateTime? collected = own ? n.LastCompute : n.LastCollectSuccess;
			rows.Add(new NeuronRow(n.Neuron, nameOf(n.Neuron), own, last is DateTime ld ? Words.FullDay(ld) : "None", label, tone, todo, error, behind,
				Words.Ago(collected, utcNow)));
		}

		return rows;
	}

	// ----- What needs attention -----

	public IReadOnlyList<Finding> Findings()
	{
		List<Finding> list = [];
		QueryAnswer now = this.A(q.Totals);

		foreach (NeuronRow n in this.NeuronRows().Where(r => r.Tone != "ok"))
		{
			bool stopped = n.Tone == "bad";
			list.Add(new Finding("neuron", stopped ? "bad" : "busy",
				n.Name + (stopped ? " stopped sending numbers" : ": " + n.Label.ToLowerInvariant()),
				(n.Behind is > 0 ? "Latest day " + n.LastDay + ". " : string.Empty) + (n.Error is not null ? "Last error: " + n.Error.TrimEnd('.') + ". " : string.Empty) + (n.Todo ?? string.Empty),
				this.Catalog.HasNeurons ? Pages.Neurons : null, n.Neuron));
		}

		if (!now.Complete && list.Count == 0)
			list.Add(new Finding("missing", "busy", "Some days are missing", "Not every Neuron has sent every day yet, so totals are too low.", this.Catalog.HasNeurons ? Pages.Neurons : null, "complete"));

		QueryAnswer platNow = this.A(q.PlatformsNow), platBefore = this.A(q.PlatformsBefore);
		foreach (QuerySeries s in platNow.All(M.ReadSuccessRate))
		{
			string platform = s.ValueOf("platform");
			double? v = s.Total, was = platBefore.Find(M.ReadSuccessRate, "platform", platform)?.Total;
			if (v is double a && was is double b && platBefore.Complete && b - a >= RateFall)
			{
				list.Add(new Finding("rate", "busy", "NFC read success on " + Words.Value(platform) + " fell " + Words.Pts(b - a),
					"Now " + Words.Pct(a) + ", was " + Words.Pct(b) + " in the 30 days before.", Pages.Nfc, platform));
			}
		}

		List<(string Version, double Rate, double Reads)> versions = [.. this.A(q.Versions).All(M.ReadSuccessRate)
			.Select(s => (s.ValueOf("appVersion"), s.Points.FirstOrDefault()))
			.Where(x => x.Item2?.Value is not null && x.Item1 != "Unknown" && (x.Item2.Denominator ?? VersionReads) >= VersionReads)
			.Select(x => (x.Item1, x.Item2!.Value!.Value, x.Item2.Denominator ?? 0))];
		if (versions.Count > 1)
		{
			var best = versions.MaxBy(v => v.Rate);
			var worst = versions.MinBy(v => v.Rate);
			if (best.Rate - worst.Rate >= VersionGap)
			{
				list.Add(new Finding("version", "busy", "App " + worst.Version + " reads " + Words.Pts(best.Rate - worst.Rate) + " worse than " + best.Version,
					Words.Pct(worst.Rate) + " against " + Words.Pct(best.Rate) + (worst.Reads > 0 ? ", from " + Words.N(worst.Reads) + " NFC reads" : string.Empty) + ".",
					Pages.Nfc, worst.Version));
			}
		}

		double? approval = now.TotalOf(M.ApprovalRate);
		QueryAnswer approvalBefore = this.A(q.ApprovalBefore);
		if (approval is double ap && approvalBefore.Complete && approvalBefore.TotalOf(M.ApprovalRate) is double apb && apb - ap >= ApprovalFall)
			list.Add(new Finding("approval", "busy", "Approval rate fell " + Words.Pts(apb - ap), "Now " + Words.Pct(ap) + ", was " + Words.Pct(apb) + ".", Pages.Applications, "approval"));

		QueryAnswer steps = this.A(q.Steps);
		List<(string Step, double Lost)> lost = [];
		string? lastStep = this.Catalog.Dimensions.TryGetValue("step", out CatalogDimension? stepDim) ? stepDim.Values?.LastOrDefault() : null;
		foreach (QuerySeries s in steps.All(M.StepsReached).Where(s => s.ValueOf("step") != lastStep))
		{
			string step = s.ValueOf("step");
			if (steps.Find(M.StepsContinued, "step", step)?.Total is double c && s.Total is double r)
				lost.Add((step, r - c));
		}

		double? first = steps.All(M.StepsReached).FirstOrDefault()?.Total;
		if (lost.Count > 0 && first is > 0)
		{
			var worst = lost.MaxBy(l => l.Lost);
			list.Add(new Finding("step", "info", "Most attempts stop after \"" + Words.Value(worst.Step) + "\"",
				Words.N(worst.Lost) + " attempts (" + (100 * worst.Lost / first.Value).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "% of all) went no further.", Pages.Nfc, worst.Step));
		}

		QuerySeries? reason = this.A(q.Reasons).All(M.RejectionReasons).Where(s => s.ValueOf("rejectionReason") != "Unknown" && s.Total > 0).MaxBy(s => s.Total);
		if (reason is not null)
			list.Add(new Finding("reason", "info", "Top rejection reason: " + Words.Value(reason.ValueOf("rejectionReason")), Words.N(reason.Total ?? 0) + " rejected applications gave it.", Pages.Applications, "reason"));

		QueryAnswer quality = this.A(q.DataQuality);
		double bad = new[] { M.InvalidEvents, M.EventsWithoutAccount, M.UnknownReadOutcomes }.Sum(m => quality.TotalOf(m) ?? 0);
		if (bad > 0)
			list.Add(new Finding("quality", "info", Words.N(bad) + (bad == 1 ? " event" : " events") + " could not be fully used", "Missing IDs, accounts or unknown read outcomes. Usually an app issue.", Pages.Troubleshooting, "quality"));

		int Rank(string level) => level switch { "bad" => 0, "busy" => 1, _ => 2 };
		return [.. list.OrderBy(f => Rank(f.Level)).Take(6)];
	}

	// ----- Facts -----

	public IReadOnlyList<Fact> Facts()
	{
		List<NeuronRow> rows = [.. this.NeuronRows()];
		StatusNeuron? self = status?.Self;
		int up = rows.Count(r => r.Tone == "ok");
		QueryAnswer now = this.A(q.Totals);
		ChartData decisions = this.Decisions();
		List<Fact> facts =
		[
			new("Numbers through", self?.LastDay is DateTime d ? Words.FullDay(d) : "None yet"),
			new("Last counted", Words.Ago(self?.LastCompute, utcNow)),
		];
		if (rows.Count > 1)
			facts.Add(new("Neurons up to date", $"{up} of {rows.Count}", up == rows.Count ? "ok" : rows.Any(r => r.Tone == "bad") ? "bad" : "busy"));
		facts.Add(new("Period complete", now.Complete ? "Yes" : "No, days missing", now.Complete ? "ok" : "busy"));
		facts.Add(new("May still change", decisions.SettleFrom >= 0 && decisions.Tips is { } t ? "From " + t[decisions.SettleFrom] : "Nothing"));
		facts.Add(new("Access", this.Catalog.IsInternal ? "Team" : "Partner"));
		return facts;
	}

	// ----- Charts -----

	private bool TeamOnly(params string[] metrics)
		=> this.Catalog.IsInternal && metrics.Where(this.Catalog.Has).All(m => this.Catalog.Metric(m)!.Audience != "external");

	/// <summary>Days of the period, as labels and tips.</summary>
	private (List<string> Labels, List<string> Tips) Days(QueryAnswer a)
	{
		IReadOnlyList<QueryPoint> pts = a.Series.FirstOrDefault()?.Points ?? [];
		return ([.. pts.Select(p => Words.Day(p.StartDay))], [.. pts.Select(p => p.StartDay == p.EndDay ? Words.LongDay(p.StartDay) : Words.Day(p.StartDay) + " to " + Words.Day(p.EndDay))]);
	}

	private int SettleFrom(QueryAnswer a)
	{
		int n = a.Series.Count == 0 ? 0 : a.Series.Max(s => s.Points.Count);
		for (int i = 0; i < n; i++)
		{
			if (a.Series.Any(s => this.Catalog.FollowsUp(s.Metric) && i < s.Points.Count && !s.Points[i].Final))
				return i;
		}

		return -1;
	}

	/// <summary>A colour slot that follows the group, not its rank: fixed values in their order, Android and iOS always the same.</summary>
	private int SlotOf(string dimension, string value, IReadOnlyList<string> seen)
	{
		if (dimension == "platform")
			return value switch { "Android" => 1, "iOS" => 2, _ => 3 };
		if (this.Catalog.Dimensions.TryGetValue(dimension, out CatalogDimension? d) && d.Values is { } fixedValues && fixedValues.Contains(value))
			return fixedValues.ToList().IndexOf(value) % 6 + 1;
		int i = seen.ToList().IndexOf(value);
		return i < 0 ? 0 : i % 6 + 1;
	}

	/// <summary>Applications by decision over the days, stacked.</summary>
	public ChartData Decisions()
	{
		QueryAnswer a = this.A(q.DecisionsDaily);
		(List<string> labels, List<string> tips) = this.Days(a);
		List<ChartSeries> series = [];
		foreach ((string metric, string name, int slot) in new[] { (M.Approved, "Approved", 1), (M.Rejected, "Rejected", 2), (M.Undecided, "Undecided", 0) })
		{
			if (a.Find(metric) is QuerySeries s)
				series.Add(new ChartSeries(name, slot, [.. s.Points.Select(p => p.Value)]));
		}

		return new ChartData(ChartKind.Stacked, labels, series) { Tips = tips, SettleFrom = this.SettleFrom(a) };
	}

	/// <summary>One metric over the days, one line per group (the largest six).</summary>
	private ChartData Lines(QueryAnswer a, string metric, string? dimension, bool percent)
	{
		(List<string> labels, List<string> tips) = this.Days(a);
		List<QuerySeries> all = [.. a.All(metric)];
		List<string> seen = [.. all.Select(s => dimension is null ? string.Empty : s.ValueOf(dimension))];
		IEnumerable<QuerySeries> top = all.OrderByDescending(s => s.Points.Sum(p => p.Denominator ?? p.Value ?? 0)).Take(6);
		List<ChartSeries> series = [.. top.OrderBy(s => all.IndexOf(s)).Select(s => dimension is null
			? new ChartSeries(this.Catalog.Metric(metric)?.Name ?? metric, 1, [.. s.Points.Select(p => p.Value)])
			: new ChartSeries(Words.Value(dimension, s.ValueOf(dimension)), this.SlotOf(dimension, s.ValueOf(dimension), seen), [.. s.Points.Select(p => p.Value)]))];
		return new ChartData(ChartKind.Line, labels, series) { Tips = tips, SettleFrom = this.SettleFrom(a), Percent = percent };
	}

	/// <summary>Several metrics over the days, one line each.</summary>
	private ChartData MetricLines(QueryAnswer a, params (string Metric, string Name)[] metrics)
	{
		(List<string> labels, List<string> tips) = this.Days(a);
		List<ChartSeries> series = [];
		int slot = 1;
		foreach ((string metric, string name) in metrics)
		{
			if (a.Find(metric) is QuerySeries s)
				series.Add(new ChartSeries(name, slot, [.. s.Points.Select(p => p.Value)]));
			slot++;
		}

		return new ChartData(ChartKind.Line, labels, series) { Tips = tips, SettleFrom = this.SettleFrom(a) };
	}

	/// <summary>One bar per group of a total: fixed values in their order, others largest first, at most ten.</summary>
	private ChartData Across(QueryAnswer a, string metric, string dimension, bool withAverage = false, int most = 10)
	{
		List<QuerySeries> all = [.. a.All(metric)];
		bool rate = this.Catalog.Metric(metric)?.IsRate == true;
		bool fixedOrder = this.Catalog.Dimensions.TryGetValue(dimension, out CatalogDimension? d) && d.Values is not null;
		if (!fixedOrder)
			all = [.. all.OrderByDescending(s => rate ? s.Points.FirstOrDefault()?.Denominator ?? 0 : s.Total ?? 0)];

		List<QuerySeries> shown = [.. all.Take(most)];
		List<string> labels = [.. shown.Select(s => Words.Value(dimension, s.ValueOf(dimension)))];
		List<double?> values = [.. shown.Select(s => s.Total)];
		List<double?>? outOf = rate ? [.. shown.Select(s => s.Points.FirstOrDefault()?.Denominator)] : null;
		if (!rate && all.Count > most)
		{
			labels.Add("Other");
			values.Add(all.Skip(most).Sum(s => s.Total ?? 0));
		}

		double? mark = null;
		if (withAverage && rate)
		{
			double num = all.Sum(s => s.Points.FirstOrDefault()?.Numerator ?? 0), den = all.Sum(s => s.Points.FirstOrDefault()?.Denominator ?? 0);
			mark = den > 0 ? Math.Round(1000 * num / den) / 10 : null;
		}

		return new ChartData(ChartKind.Across, labels, [new ChartSeries(this.Catalog.Metric(metric)?.Name ?? metric, 1, values) { OutOf = outOf }])
		{
			Percent = rate,
			Mark = mark,
			MarkName = mark is null ? null : "Average",
		};
	}

	/// <summary>The charts of a page that has charts. A chart without numbers the account may read is left out.</summary>
	public IReadOnlyList<PanelSpec> Panels(string page)
	{
		List<PanelSpec> list = [];
		void Add(TelemetryQuery? query, string key, string title, string? hint, Func<QueryAnswer, ChartData> make, string first, params string[] metrics)
		{
			if (query is null)
				return;
			list.Add(new PanelSpec(key, title, hint, make(this.A(query)), first, this.TeamOnly(metrics)));
		}

		switch (page)
		{
			case Pages.Overview:
				Add(q.DecisionsDaily, "decisions", "Applications and their decisions", "Counted on the day each was first sent", _ => this.Decisions(), "Day", M.Approved);
				Add(q.PlatformsDaily, "platforms", "NFC read success by platform", null, a => this.Lines(a, M.ReadSuccessRate, "platform", true), "Day", M.ReadSuccessRate);
				break;

			case Pages.Applications:
				Add(q.DecisionsDaily, "decisions", "Applications and their decisions", "Counted on the day each was first sent", _ => this.Decisions(), "Day", M.Approved);
				Add(q.ApprovalDaily, "approval", "Approval rate", "Approved of decided", a => this.Lines(a, M.ApprovalRate, null, true), "Day", M.ApprovalRate);
				Add(q.ByMethod, "method", "Applications by method", null, a => this.Across(a, M.Submitted, "method"), "Method", M.Submitted);
				Add(q.ByMethod is null ? null : (q.ByMethod with { Metrics = [M.ApprovalRate] }).NarrowTo(this.Catalog), "methodRate", "Approval rate by method", null,
					_ => this.Across(this.A(q.ByMethod), M.ApprovalRate, "method", withAverage: true), "Method", M.ApprovalRate);
				Add(q.ByNationality, "nationality", "Applications by nationality", null, a => this.Across(a, M.Submitted, "nationality"), "Nationality", M.Submitted);
				Add(q.Reasons, "reasons", "Rejection reasons", "One application can have several", a => this.Across(a, M.RejectionReasons, "rejectionReason"), "Reason", M.RejectionReasons);
				Add(q.Activity, "activity", "Daily work", "Sent, approved and rejected on each day", a => this.MetricLines(a, (M.Submissions, "Sent"), (M.Approvals, "Approved"), (M.Rejections, "Rejected")), "Day", M.Submissions);
				break;

			case Pages.Nfc:
				Add(q.PlatformsDaily, "platforms", "NFC read success by platform", "Reads that worked once the scan started", a => this.Lines(a, M.ReadSuccessRate, "platform", true), "Day", M.ReadSuccessRate);
				Add(q.JourneysDaily, "journeys", "NFC completion by platform", "People who got a read in the end, retries included", a => this.Lines(a, M.JourneySuccessRate, "platform", true), "Day", M.JourneySuccessRate);
				Add(q.Outcomes, "outcomes", "How NFC attempts end", null, a => this.Across(a, M.Attempts, "outcome"), "How it ended", M.Attempts);
				Add(q.Steps, "steps", "How far attempts get", "Attempts that reached each step", a => this.Across(a, M.StepsReached, "step", most: 12), "Step", M.StepsReached);
				Add(q.Versions, "versions", "NFC read success by app version", "The line is the average", a => this.Across(a, M.ReadSuccessRate, "appVersion", withAverage: true), "App version", M.ReadSuccessRate);
				Add(q.ReadsByNationality, "readNationality", "NFC read success by nationality", null, a => this.Across(a, M.ReadSuccessRate, "nationality", withAverage: true), "Nationality", M.ReadSuccessRate);
				break;

			case Pages.Troubleshooting:
				Add(q.FailureCodes, "failures", "Failure codes", "NFC reads that failed", a => this.Across(a, M.ReadsFailed, "failureCode"), "Failure code", M.ReadsFailed);
				Add(q.ErrorsBefore, "before", "Errors before the read", "Camera, document text or setup", a => this.Across(a, M.ErrorsBeforeRead, "failureCode"), "Failure code", M.ErrorsBeforeRead);
				Add(q.StopReasons, "stops", "Why attempts stop", null, a => this.Across(a, M.StepsStopped, "stopReason"), "Why", M.StepsStopped);
				Add(q.ChipResults, "chip", "Chip results after a failed read", null, a => this.Across(a, M.FailureDiagnostics, "chipResult"), "Chip result", M.FailureDiagnostics);
				Add(q.Unreadable, "unreadable", "Documents the app could not read", null, a => this.Across(a, M.UnreadableReports, "failureCode"), "Failure code", M.UnreadableReports);
				Add(q.DataQuality, "quality", "Events that could not be used", null, this.QualityBars, "Kind", M.InvalidEvents);
				break;

			case Pages.Neurons:
				Add(q.ByNeuron, "neuronApps", "Applications by Neuron", null, a => this.Across(a, M.Submitted, TelemetryQuery.Neuron), "Neuron", M.Submitted);
				Add(q.ByNeuron is null ? null : (q.ByNeuron with { Metrics = [M.Attempts] }).NarrowTo(this.Catalog), "neuronNfc", "NFC attempts by Neuron", null,
					_ => this.Across(this.A(q.ByNeuron), M.Attempts, TelemetryQuery.Neuron), "Neuron", M.Attempts);
				break;
		}

		// Neurons by their names.
		return [.. list.Select(p => p.TableFirst == "Neuron" ? p with { Data = p.Data with { Labels = [.. p.Data.Labels.Select(nameOf)] } } : p)];
	}

	private ChartData QualityBars(QueryAnswer a)
	{
		(string Metric, string Name)[] kinds = [(M.InvalidEvents, "Missing ID, time or link"), (M.EventsWithoutAccount, "Without an account"), (M.UnknownReadOutcomes, "Unknown read outcome")];
		List<(string Metric, string Name)> shown = [.. kinds.Where(k => a.Find(k.Metric) is not null)];
		return new ChartData(ChartKind.Across, [.. shown.Select(k => k.Name)], [new ChartSeries("Events", 1, [.. shown.Select(k => a.TotalOf(k.Metric))])]);
	}

	// ----- All metrics -----

	/// <summary>One metric over the days, split or not, as the All metrics page shows it.</summary>
	public ChartData Explore(string metric, string? split)
	{
		TelemetryQuery? query = q.Explore(metric, split);
		if (query is null)
			return new ChartData(ChartKind.Line, [], []);

		QueryAnswer a = this.A(query);
		CatalogMetric m = this.Catalog.Metric(metric)!;
		ChartData d = this.Lines(a, metric, split, m.IsRate);
		if (split is null && !m.IsRate)
			d = d with { Kind = ChartKind.Bars };
		else if (split is not null && !m.IsRate)
			d = d with { Kind = ChartKind.Stacked };
		if (split == TelemetryQuery.Neuron)
			d = d with { Series = [.. d.Series.Select(s => s with { Name = nameOf(s.Name) })] };
		return d;
	}

	/// <summary>The same metric over the whole period, one bar per group.</summary>
	public ChartData ExploreTotal(string metric, string split)
	{
		TelemetryQuery? query = q.ExploreTotal(metric, split);
		if (query is null)
			return new ChartData(ChartKind.Across, [], []);

		ChartData d = this.Across(this.A(query), metric, split, withAverage: true, most: 12);
		return split == TelemetryQuery.Neuron ? d with { Labels = [.. d.Labels.Select(nameOf)] } : d;
	}
}
