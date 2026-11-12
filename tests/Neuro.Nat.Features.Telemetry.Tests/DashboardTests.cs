using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Neuro.Nat.App.Features;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Engine.Storage;
using Neuro.Nat.Features.Telemetry.Api;
using Neuro.Nat.Features.Telemetry.Charts;
using Neuro.Nat.Features.Telemetry.Data;
using Neuro.Nat.Features.Telemetry.Tests.StandIn;
using Neuro.Nat.Features.Telemetry.ViewModels;

namespace Neuro.Nat.Features.Telemetry.Tests;

/// <summary>
/// The App telemetry dashboard against a stand-in API with made-up numbers: what it asks, how
/// often, what it keeps, what it finds, and what each account may see. No live Neuron.
/// </summary>
public class DashboardTests
{
	private const string Lab = "lab.tagroot.io";
	private const string Edge = "edge.example.org";

	private static Task<TelemetrySample> LabAsync(bool internalAccess = true, IFeatureAnswerStore? store = null)
		=> TelemetrySample.CreateAsync([(Lab, new StandInTelemetry(TelemetrySample.Start) { Internal = internalAccess })], store);

	// ----- Asking -----

	[Fact]
	public void QueriesAreWrittenTheWayTheApiTakesThem()
	{
		DateTime from = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), to = new(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);
		Assert.Equal("/Query?metrics=Nfc.ReadSuccessRate&from=2026-10-13&to=2026-11-11&interval=total&groupBy=platform",
			TelemetryQuery.Of(M.ReadSuccessRate).Over(from, to, "total").By("platform").ToPath());
		Assert.Equal("/Query?metrics=Applications.Submitted,Applications.Approved&from=2026-10-13&to=2026-11-11",
			TelemetryQuery.Of(M.Submitted, M.Approved).Over(from, to).ToPath());
		Assert.Equal("/Query?metrics=Nfc.Attempts&groupBy=neuron,outcome&filter=outcome:ReadFailed%7CAbandoned",
			TelemetryQuery.Of(M.Attempts).By("neuron", "outcome").Where("outcome", "ReadFailed", "Abandoned").ToPath());
		Assert.Equal("/Query?metrics=Nfc.Users&interval=month", (TelemetryQuery.Of(M.Users) with { Interval = "month" }).ToPath());
	}

	[Fact]
	public void AnswersAreReadInTheirExactShape()
	{
		StandInTelemetry api = new(TelemetrySample.Start);
		Catalog catalog = TelemetryJson.Catalog(api.Catalog())!;
		Assert.Equal("internal", catalog.Access);
		Assert.Equal(5, catalog.MinimumDenominator);
		Assert.True(catalog.HasNeurons);
		CatalogMetric rate = catalog.Metric(M.ReadSuccessRate)!;
		Assert.True(rate.IsRate);
		Assert.Equal("Nfc.ReadsSucceeded", rate.Numerator);
		Assert.Equal(30, rate.FollowUpDays);
		Assert.False(catalog.Metric(M.StepsReached)!.Total);
		Assert.Equal(["ReadSucceeded", "ReadFailed", "ErrorBeforeRead", "SwitchedMethod", "Abandoned", "NoReadResult"], catalog.Dimensions["outcome"].Values);

		// The example from docs/API.md, word for word.
		QueryAnswer a = TelemetryJson.Query("""
			{ "from": "2026-09-01", "to": "2026-10-31", "interval": "month", "complete": true, "series": [
			  { "metric": "Applications.Submitted", "kind": "count", "unit": "applications", "group": {}, "points": [
			      { "start": "2026-09-01", "end": "2026-09-30", "value": 412, "final": true },
			      { "start": "2026-10-01", "end": "2026-10-31", "value": 455, "final": false } ] },
			  { "metric": "Applications.ApprovalRate", "kind": "rate", "unit": "percent", "group": {}, "points": [
			      { "start": "2026-09-01", "end": "2026-09-30", "value": 85.5, "numerator": 301, "denominator": 352, "final": true },
			      { "start": "2026-10-01", "end": "2026-10-31", "value": null, "numerator": 2, "denominator": 3, "final": false } ] } ] }
			""")!;
		Assert.Equal("month", a.Interval);
		Assert.Equal(455, a.LastOf(M.Submitted));
		QueryPoint p = a.Find(M.ApprovalRate)!.Points[0];
		Assert.Equal((85.5, 301.0, 352.0, true), (p.Value!.Value, p.Numerator!.Value, p.Denominator!.Value, p.Final));
		Assert.Null(a.Find(M.ApprovalRate)!.Points[1].Value);
		Assert.Equal(new DateTime(2026, 10, 31), a.Find(M.Submitted)!.Points[1].EndDay);

		Assert.Null(TelemetryJson.Catalog("<html>"));
		Assert.Null(TelemetryJson.Catalog("""{"neurons":[]}"""));
		Assert.Equal("Unknown dimension: Platform.", TelemetryJson.ErrorMessage("""{ "error": "unknownDimension", "message": "Unknown dimension: Platform." }"""));
	}

	[AvaloniaFact]
	public async Task OnlyWhatTheAccountMayAskIsAsked()
	{
		await using TelemetrySample s = await LabAsync(internalAccess: false);
		s.StartFeatures();
		await s.FindAsync();
		DashboardViewModel d = s.Feature.DashboardOf(s.Feature.Collectors().Single());
		foreach ((string page, _) in Pages.All)
		{
			d.ShowPart(page);
			await d.LoadAsync(ask: true);
		}

		// Nothing was answered "unknown": a hidden metric or dimension is never asked for.
		StandInTelemetry api = s.Apis[Lab];
		Assert.All(api.Asked.Where(p => p.Contains("/Query", StringComparison.Ordinal)), p => Assert.DoesNotContain("neuron", p, StringComparison.Ordinal));
		Assert.False(d.HasProblem, d.Problem);
	}

	[AvaloniaFact]
	public async Task AnswersAreUsedForAnHourThenAskedAgainWithTheirETag()
	{
		await using TelemetrySample s = await LabAsync();
		s.StartFeatures();
		await s.FindAsync();
		StandInTelemetry api = s.Apis[Lab];
		TelemetryFetcher fetcher = s.Feature.Fetcher!;
		int first = fetcher.Requests;
		Assert.True(first > 3);
		Assert.Equal(0, fetcher.NotModified);

		// Opened, and opened again: nothing new is asked within the hour.
		DashboardViewModel d = s.Feature.DashboardOf(s.Feature.Collectors().Single());
		await d.LoadAsync(ask: true);
		s.Now = s.Now.AddMinutes(59);
		await d.LoadAsync(ask: true);
		s.Feature.RefreshAll();
		await s.Feature.SettledAsync();
		Assert.Equal(first, fetcher.Requests);

		// An hour later: asked again, each with If-None-Match, and "nothing changed" is a 304.
		s.Now = s.Now.AddMinutes(2);
		await d.LoadAsync(ask: true);
		Assert.Equal(2 * first, fetcher.Requests);
		Assert.Equal(first, fetcher.NotModified);
		Assert.Equal(first, api.NotModified);
		Assert.True(d.ShowsNumbers);
		Assert.Equal(6, d.KeyNumbers.Count);
	}

	[AvaloniaFact]
	public async Task WhatWasFetchedIsThereAtTheNextStart()
	{
		InMemoryFeatureAnswerStore store = new();
		await using (TelemetrySample s = await LabAsync(store: store))
		{
			s.StartFeatures();
			await s.FindAsync();
		}

		IReadOnlyDictionary<string, FeatureAnswer> kept = await store.LoadAsync(TelemetryFeature.FeatureKey);
		Assert.Contains(kept.Keys, k => k.EndsWith("|/Catalog", StringComparison.Ordinal));
		Assert.All(kept.Values, a => Assert.False(string.IsNullOrEmpty(a.ETag)));

		// A new run, half an hour later: the dashboard shows the kept numbers without asking.
		await using TelemetrySample again = await LabAsync(store: store);
		again.Now = again.Now.AddMinutes(30);
		again.StartFeatures();
		again.Feature.FetchesNumbers = false;
		await again.FindAsync();
		DashboardViewModel d = again.Feature.DashboardOf(again.Feature.Collectors().Single());
		await d.LoadAsync(ask: true);
		Assert.Equal(0, again.QueryRequests());
		Assert.Equal(6, d.KeyNumbers.Count);
	}

	// ----- Two collectors -----

	[AvaloniaFact]
	public async Task TwoCollectorsAreNeverMixed()
	{
		StandInTelemetry big = new(TelemetrySample.Start), small = new(TelemetrySample.Start, ["edge.example.org", "a.example.org"]) { Scale = 0.1, Stopped = null };
		await using TelemetrySample s = await TelemetrySample.CreateAsync([(Lab, big), (Edge, small)]);
		MainWindowViewModel vm = await s.WindowModelAsync();
		await s.FindAsync();

		IReadOnlyList<Data.TelemetryCollector> collectors = s.Feature.Collectors();
		Assert.Equal(2, collectors.Count);
		Assert.NotEqual(collectors[0].Key, collectors[1].Key);

		// Each dashboard shows its own Neuron's numbers only.
		DashboardViewModel labBoard = s.Feature.DashboardOf(collectors.Single(c => c.Domain == Lab));
		DashboardViewModel edgeBoard = s.Feature.DashboardOf(collectors.Single(c => c.Domain == Edge));
		await labBoard.LoadAsync(ask: true);
		await edgeBoard.LoadAsync(ask: true);
		double labApps = double.Parse(labBoard.KeyNumbers[0].Value.Replace(",", string.Empty, StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);
		double edgeApps = double.Parse(edgeBoard.KeyNumbers[0].Value.Replace(",", string.Empty, StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);
		Assert.True(labApps > 5 * edgeApps, $"{labApps} and {edgeApps}");
		Assert.All(s.Requests.Where(r => r.StartsWith(Edge, StringComparison.Ordinal)), r => Assert.Contains(Edge, r, StringComparison.Ordinal));

		// The rail has one place, with a picker; picking one shows that one's dashboard.
		Assert.Equal(["App telemetry"], vm.FeaturePlaces.Select(p => p.Title));
		vm.ShowFeaturePlaceCommand.Execute(TelemetryFeature.PlaceKey);
		TelemetryPlaceViewModel place = Assert.IsType<TelemetryPlaceViewModel>(vm.FeatureContent);
		Assert.True(place.HasPicker);
		Assert.Equal(["edge.example.org", "Lab"], place.Collectors.Select(c => c.Name));
		place.PickCommand.Execute(place.Collectors[1].Key);
		Assert.Same(labBoard, place.Current);

		// Each collector's page has its own tab, showing its own dashboard.
		vm.OpenNeuron(Edge, s.AccountOf[Edge]);
		Dispatcher.UIThread.RunJobs();
		vm.ShowFeatureTabCommand.Execute(TelemetryFeature.TabKey);
		Assert.Same(edgeBoard, vm.FeatureContent);

		// The big screen shows both, each in its own row.
		IReadOnlyList<AppNumbersRow> rows = s.Feature.AppNumberRows();
		Assert.Equal(["edge.example.org", "Lab"], rows.Select(r => r.Name));
		Assert.All(rows, r => Assert.True(r.ShowName));
	}

	[AvaloniaFact]
	public async Task ANeuronTheCollectorCollectsIsNotADashboardOfItsOwn()
	{
		// gbg.lab.tagroot.io is one of the root's sources; its own numbers are in the root's dashboard.
		StandInTelemetry root = new(TelemetrySample.Start), source = new(TelemetrySample.Start, ["gbg.lab.tagroot.io"]) { Stopped = null };
		await using TelemetrySample s = await TelemetrySample.CreateAsync([(Lab, root), ("gbg.lab.tagroot.io", source)]);
		s.StartFeatures();
		await s.FindAsync();
		Assert.Equal([Lab], s.Feature.Collectors().Select(c => c.Domain));
	}

	// ----- The partner view -----

	[AvaloniaFact]
	public async Task APartnerSeesLessAndNoErrors()
	{
		await using TelemetrySample s = await LabAsync(internalAccess: false);
		s.StartFeatures();
		await s.FindAsync();
		DashboardViewModel d = s.Feature.DashboardOf(s.Feature.Collectors().Single());
		await d.LoadAsync(ask: true);

		// Troubleshooting and Neurons are the team's only: left out, not shown as errors.
		Assert.Equal(["Overview", "Applications", "NFC reading", "All metrics"], d.PagesShown.Select(p => p.Title));
		Assert.Equal(6, d.KeyNumbers.Count);
		Assert.False(d.HasProblem);
		Assert.DoesNotContain(d.Facts, f => f.Name == "Neurons up to date");
		Assert.Contains(d.Facts, f => f.Name == "Access" && f.Value == "Partner");

		d.ShowPart(Pages.Applications);
		await d.LoadAsync(ask: true);
		Assert.DoesNotContain(d.Panels, p => p.Key == "reasons");
		Assert.DoesNotContain(d.Panels, p => p.TeamOnly);

		d.ShowPart(Pages.Nfc);
		await d.LoadAsync(ask: true);
		Assert.DoesNotContain(d.Panels, p => p.Key == "steps");
		Assert.Contains(d.Panels, p => p.Key == "versions");

		// The metrics it may not read are not in All metrics, nor found by search.
		d.ShowPart(Pages.Metrics);
		await d.LoadAsync(ask: true);
		Assert.DoesNotContain(d.MetricChoices, c => c.Key == "Nfc.ReadsFailed");
		Assert.DoesNotContain(d.SplitChoices, c => c.Key == "neuron");
		Assert.DoesNotContain(s.Feature.SearchHits(), h => h.Title == "Troubleshooting");

		// The team sees it all.
		await using TelemetrySample team = await LabAsync();
		team.StartFeatures();
		await team.FindAsync();
		DashboardViewModel t = team.Feature.DashboardOf(team.Feature.Collectors().Single());
		await t.LoadAsync(ask: true);
		Assert.Equal(["Overview", "Applications", "NFC reading", "Troubleshooting", "Neurons", "All metrics"], t.PagesShown.Select(p => p.Title));
	}

	// ----- Joined with the rest of NAT -----

	[AvaloniaFact]
	public async Task TheInboxHearsOfAStoppedNeuronAFallenRateAndAWorseVersion()
	{
		await using TelemetrySample s = await LabAsync();
		s.StartFeatures();
		await s.FindAsync();

		List<FeatureInboxItem> items = [.. s.Feature.InboxItems()];
		FeatureInboxItem stopped = Assert.Single(items, i => i.Key.EndsWith("|neuron|sto.lab.tagroot.io", StringComparison.Ordinal));
		Assert.Equal("sto.lab.tagroot.io stopped sending numbers", stopped.Title);
		Assert.Equal("bad", stopped.Tone);
		Assert.Equal(Pages.Neurons, stopped.Opens!.Part);

		FeatureInboxItem fell = Assert.Single(items, i => i.Key.EndsWith("|rate|Android", StringComparison.Ordinal));
		Assert.StartsWith("NFC read success on Android fell ", fell.Title, StringComparison.Ordinal);
		Assert.Equal(Pages.Nfc, fell.Opens!.Part);

		FeatureInboxItem worse = Assert.Single(items, i => i.Key.Contains("|version|", StringComparison.Ordinal));
		Assert.StartsWith("App 2.5.1 reads ", worse.Title, StringComparison.Ordinal);
		Assert.All(items, i => Assert.Equal(TelemetryFeature.Title, i.KindLabel));
		Assert.All(items, i => Assert.Equal(TelemetryFeature.TabKey, i.Opens!.ViewKey));
		Assert.Equal(3, items.Count);

		// Fixed: the Neuron sends again, so its item goes.
		s.Apis[Lab].Stopped = null;
		s.Now = s.Now.AddHours(1.1);
		s.Feature.RefreshAll();
		await s.Feature.SettledAsync();
		Assert.DoesNotContain(s.Feature.InboxItems(), i => i.Key.Contains("|neuron|", StringComparison.Ordinal));

		// The thresholds, on their own.
		Assert.True(TelemetryFeature.IsForInbox(new Finding("rate", "busy", "t", "d", null, "iOS")));
		Assert.False(TelemetryFeature.IsForInbox(new Finding("neuron", "busy", "t", "d", null, "x")));
		Assert.False(TelemetryFeature.IsForInbox(new Finding("reason", "info", "t", "d", null, "x")));
	}

	[AvaloniaFact]
	public async Task TheInboxShowsTheItemsAndOpensTheDashboardThere()
	{
		await using TelemetrySample s = await LabAsync();
		MainWindowViewModel vm = await s.WindowModelAsync();
		await s.FindAsync();
		vm.Inbox.Refresh();
		InboxRowViewModel row = Assert.Single(vm.Inbox.Everything, r => r.IsFeatureItem && r.Title.StartsWith("App 2.5.1", StringComparison.Ordinal));
		Assert.Equal(Lab, row.Jid);

		vm.OpenFeature(row.FeatureItem!.Opens!);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.IsNeuronFeature);
		DashboardViewModel d = Assert.IsType<DashboardViewModel>(vm.FeatureContent);
		Assert.Equal(Pages.Nfc, d.Page);
	}

	[AvaloniaFact]
	public async Task KeyNumbersAreTheThirdBigScreenPage()
	{
		await using TelemetrySample s = await LabAsync();
		s.StartFeatures();
		Assert.Empty(s.Feature.BigScreenPages());
		await s.FindAsync();

		FeatureScreenPage page = Assert.Single(s.Feature.BigScreenPages());
		Assert.Equal(("app-numbers", "App numbers"), (page.Key, page.Title));
		AppNumbersViewModel numbers = Assert.IsType<AppNumbersViewModel>(page.Content());
		AppNumbersRow row = Assert.Single(numbers.Rows);
		Assert.False(row.ShowName);
		Assert.Equal(["Applications", "Approval rate", "NFC read success", "NFC completion", "Applicants", "NFC users"], row.Numbers.Select(n => n.Label));

		// Fed to the big screen through the contract: after Neurons and Problems.
		List<IBigScreenPage> added = [];
		s.Features.FeedBigScreen(added);
		IBigScreenPage screen = Assert.Single(added);
		Assert.Equal("App numbers", screen.Title);
		Assert.True(screen.HasContent);
		Assert.Same(numbers, screen.Content);
	}

	[AvaloniaFact]
	public async Task SearchFindsThePagesAndEveryMetric()
	{
		await using TelemetrySample s = await LabAsync();
		MainWindowViewModel vm = await s.WindowModelAsync();
		await s.FindAsync();

		List<FeatureSearchHit> hits = [.. s.Feature.SearchHits()];
		Assert.Contains(hits, h => h.Title == "NFC reading" && h.Opens.Part == Pages.Nfc);
		FeatureSearchHit metric = Assert.Single(hits, h => h.Title == "NFC read success rate");
		Assert.Equal("Metric", metric.Kind);
		Assert.Equal("metric:Nfc.ReadSuccessRate", metric.Opens.Part);
		Assert.Equal(38, hits.Count(h => h.Kind == "Metric"));

		// Ctrl+K: the metric's line opens All metrics with it chosen.
		vm.Search.Open();
		vm.Search.Query = "NFC read success rate";
		Assert.Contains("NFC read success rate", vm.Search.Results.Select(r => r.Title));
		vm.Search.Run();
		Dispatcher.UIThread.RunJobs();
		DashboardViewModel d = Assert.IsType<DashboardViewModel>(vm.FeatureContent);
		await d.LoadAsync(ask: true);
		Assert.Equal(Pages.Metrics, d.Page);
		Assert.Equal("Nfc.ReadSuccessRate", d.Metric?.Key);
		Assert.NotNull(d.ExploreChart);
		Assert.True(d.ExploreChart!.HasNumbers);
	}

	[AvaloniaFact]
	public async Task AViewCanBeCopiedAsALinkOnceTheShellMakesThem()
	{
		await using TelemetrySample s = await LabAsync();
		s.StartFeatures();
		await s.FindAsync();
		DashboardViewModel d = s.Feature.DashboardOf(s.Feature.Collectors().Single());
		Assert.False(d.CanCopyLink);

		Func<FeatureTarget, string?> was = FeatureLinks.Make;
		FeatureTarget? asked = null;
		try
		{
			FeatureLinks.Make = t =>
			{
				asked = t;
				return "neuro-nat://" + t.NeuronJid + "/" + t.ViewKey + "/" + t.Part;
			};
			d.ShowPart(Pages.Nfc);
			Assert.True(d.CanCopyLink);
			Assert.Equal(new FeatureTarget(TelemetryFeature.TabKey, s.AccountOf[Lab], Lab) { Part = Pages.Nfc }, asked);
		}
		finally
		{
			FeatureLinks.Make = was;
		}
	}

	// ----- What the numbers say -----

	[AvaloniaFact]
	public async Task TheChartsHatchTheDaysThatCanStillChange()
	{
		await using TelemetrySample s = await LabAsync();
		s.StartFeatures();
		await s.FindAsync();
		DashboardModel m = s.Feature.DataOf(s.Feature.Collectors().Single()).Overview!.Model!;
		ChartData decisions = m.Decisions();
		Assert.Equal(30, decisions.Count);

		// Every day of the last 30 is followed for 30 days: all may change.
		Assert.Equal(0, decisions.SettleFrom);
		Assert.Contains("May change", decisions.ToTable("Day").Rows[0]);

		// A metric that is not followed is final.
		Data.CollectorData data = s.Feature.DataOf(s.Feature.Collectors().Single());
		Snapshot snap = await data.GetAsync([Pages.Overview], [m.Questions.Explore("Activity.Submissions", null)!, m.Questions.Explore("Activity.Submissions", "method")!], ask: true);
		m = snap.Model!;
		ChartData whole = m.Explore("Activity.Submissions", null);
		Assert.Equal(ChartKind.Bars, whole.Kind);
		Assert.False(whole.IsEmpty);
		Assert.Equal(-1, whole.SettleFrom);
		ChartTable table = m.Explore("Activity.Submissions", "method").ToTable("Day");
		Assert.Equal(["Day", "Manual review", "NFC", "Peer review"], table.Headers);
		Assert.Equal(30, table.Rows.Count);
	}

	[Fact]
	public void TheWordsFollowTheRules()
	{
		foreach (string w in new[] { "No numbers in these days", "Could not read new numbers:", "May change", "Team only", "Needs attention", "Nothing is broken" })
			Assert.DoesNotContain(w, c => c is (char)0xB7 or (char)0x2014);
	}
}
