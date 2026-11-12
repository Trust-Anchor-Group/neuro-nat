using System.Security.Cryptography;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Alerts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Sniffing;
using Neuro.Nat.Engine.Storage;
using Waher.Persistence.Files;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// A legal identity application needs a person only when the Neuron's automatic review
/// (IdReviewPerformed) left a claim unvalidated and invalidated none. Wording and tags follow the
/// broker's source (LegalComponent and IdentityApplication.LogResults).
/// </summary>
public class IdentityReviewTests
{
	private const string Neuron = "alpha@lab.local";
	private const string LegalId = "2f9a1c04-57e1-4c1e@legal.lab.local";
	private const string Applicant = "kim@lab.local";

	private static readonly Guid account = Guid.NewGuid();

	private static string Application(string legalId = LegalId)
		=> $"Legal identity application received: [`{legalId}`](https://lab.local/LegalIdentity.md?Id={Uri.EscapeDataString(legalId)})\n\n";

	private static IncomingNotification In(string markdown, DateTime at, Guid? accountId = null)
		=> new(accountId ?? account, Neuron, null, null, at, markdown, markdown);

	/// <summary>The event as the broker logs it: Result, Contact (or Preview), then one tag per claim.</summary>
	private static NeuronEvent Review(DateTime at, bool? result, string legalId = LegalId, bool preview = false, params (string Claim, string Value)[] claims)
	{
		List<KeyValuePair<string, string>> tags = [new("Result", result?.ToString() ?? string.Empty)];
		tags.Add(preview ? new("Preview", "True") : new("Contact", "kim@example.com"));
		tags.AddRange(claims.Select(c => new KeyValuePair<string, string>(c.Claim, c.Value)));

		return new NeuronEvent
		{
			Source = new EventSource(account, Neuron),
			Timestamp = at,
			Severity = EventSeverity.Info,
			Message = "Automatic identity review concluded.",
			Object = legalId,
			Actor = Applicant + "/phone-17",
			EventId = "IdReviewPerformed",
			Tags = tags,
		};
	}

	private const string Validated = "Validator:TAG.Identity.Sweden.PersonalNumberValidator";
	private const string Invalidated = "Invalidator:TAG.Identity.Sweden.PersonalNumberValidator";

	private static async Task<(MonitorService Monitors, AlertService Alerts, List<Alert> Got)> WatchAsync(DateTime now, bool readsEvents = true)
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.IdReviews." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		MonitorService m = new(manager, new EventHub()) { ReadsEventsForTests = (_, _) => readsEvents };
		AlertService alerts = new(m, new AlertSettings(), () => now, startTimer: false);
		List<Alert> got = [];
		alerts.Raised += (_, a) => got.Add(a);
		return (m, alerts, got);
	}

	[Fact]
	public void ApplicationAloneIsQuiet()
	{
		NeuronNotification n = NeuronNotification.From(In(Application(), DateTime.Now));

		Assert.Equal(NotificationKind.LegalIdApplication, n.Kind);
		Assert.Equal(IdentityReview.Waiting, n.Review);
		Assert.False(n.NeedsAction);
		Assert.False(n.IsOpen);
		Assert.Equal([LegalId], IdentityReviews.LegalIdsOf(n));
	}

	[Fact]
	public void ReadsTheRuleFromTheTags()
	{
		DateTime t = DateTime.Now;
		Assert.True(IdentityReviews.NeedsPerson(Review(t, null, claims: [("FIRST", Validated), ("PNR", "Unvalidated")])));
		Assert.False(IdentityReviews.NeedsPerson(Review(t, true, claims: [("FIRST", Validated), ("PNR", Validated)])));
		Assert.False(IdentityReviews.NeedsPerson(Review(t, false, claims: [("PNR", Invalidated), ("FIRST", "Unvalidated")])));
		Assert.Null(IdentityReviews.NeedsPerson(Review(t, null, preview: true, claims: [("PNR", "Unvalidated")])));
		Assert.Null(IdentityReviews.NeedsPerson(Review(t, null, claims: [("PNR", "Unvalidated")]) with { EventId = "LegalIdRegistered" }));
	}

	[Fact]
	public void ReadsTheRuleFromASniffedEvent()
	{
		string text = "Automatic identity review concluded.\n\nObject: " + LegalId + "\nActor: " + Applicant + "/phone-17\nEventId: IdReviewPerformed\n" +
			"Result: \nContact: kim@example.com\nFIRST: " + Validated + "\nPNR: Unvalidated";
		NeuronEvent e = SniffedEventParser.Parse(new SnifferLine(0, DateTime.Now, SnifferDirection.Information, text), new EventSource(account, Neuron))!;

		Assert.True(IdentityReviews.NeedsPerson(e));
	}

	[Fact]
	public async Task UnvalidatedWithoutInvalidatedNeedsYouAndAlertsOnce()
	{
		DateTime now = DateTime.Now;
		var (m, alerts, got) = await WatchAsync(now);
		using AlertService _ = alerts;

		NeuronNotification app = m.Attention.Add(In(Application(), now))!;
		Assert.Equal(IdentityReview.Waiting, app.Review);
		Assert.Empty(got);
		Assert.Equal(0, m.Attention.OpenCount);

		m.Hub.Add(Review(now, null, claims: [("FIRST", Validated), ("PNR", "Unvalidated")]));

		NeuronNotification n = m.Attention.Get(app.Id)!;
		Assert.Equal(IdentityReview.NeedsReview, n.Review);
		Assert.True(n.IsOpen);
		Assert.Equal("Identity application needs your review", n.Title);
		Assert.Equal(Applicant, n.Subject);
		Assert.Equal(1, m.Attention.OpenCount);

		Alert a = Assert.Single(got);
		Assert.True(a.NeedsYou);
		Assert.Equal("Identity application needs your review", a.Title);
		Assert.Contains(Applicant, a.Detail);
		Assert.Contains("lab.local", a.Detail);
		Assert.DoesNotContain("kim@example.com", a.Detail);
		Assert.DoesNotContain(((char)0xB7).ToString(), a.Detail);

		// The same review again, reading it, or handling and reopening it: no second pop-up.
		m.Hub.Add(Review(now.AddSeconds(1), null, claims: [("FIRST", Validated), ("PNR", "Unvalidated")]));
		m.Attention.SetState(app.Id, AttentionState.Read);
		m.Attention.SetState(app.Id, AttentionState.Handled);
		m.Attention.SetState(app.Id, AttentionState.Read);
		Assert.Single(got);
		await m.DisposeAsync();
	}

	[Fact]
	public async Task AllValidatedIsQuiet()
	{
		DateTime now = DateTime.Now;
		var (m, alerts, got) = await WatchAsync(now);
		using AlertService _ = alerts;

		NeuronNotification app = m.Attention.Add(In(Application(), now))!;
		m.Hub.Add(Review(now, true, claims: [("FIRST", Validated), ("PNR", Validated)]));

		NeuronNotification n = m.Attention.Get(app.Id)!;
		Assert.Equal(IdentityReview.Settled, n.Review);
		Assert.False(n.NeedsAction);
		Assert.Single(m.Attention.Items);
		Assert.Empty(got);
		await m.DisposeAsync();
	}

	[Fact]
	public async Task AnyInvalidatedIsQuiet()
	{
		DateTime now = DateTime.Now;
		var (m, alerts, got) = await WatchAsync(now);
		using AlertService _ = alerts;

		NeuronNotification app = m.Attention.Add(In(Application(), now))!;
		m.Hub.Add(Review(now, false, claims: [("FIRST", "Unvalidated"), ("PNR", Invalidated)]));

		Assert.Equal(IdentityReview.Settled, m.Attention.Get(app.Id)!.Review);
		Assert.Equal(0, m.Attention.OpenCount);
		Assert.Empty(got);
		await m.DisposeAsync();
	}

	[Fact]
	public async Task PreviewReviewIsIgnored()
	{
		DateTime now = DateTime.Now;
		var (m, alerts, got) = await WatchAsync(now);
		using AlertService _ = alerts;

		NeuronNotification app = m.Attention.Add(In(Application(), now))!;
		m.Hub.Add(Review(now, null, preview: true, claims: [("PNR", "Unvalidated")]));

		Assert.Equal(IdentityReview.Waiting, m.Attention.Get(app.Id)!.Review);
		Assert.Empty(got);
		await m.DisposeAsync();
	}

	[Fact]
	public async Task ReviewThatCameFirstStillCounts()
	{
		DateTime now = DateTime.Now;
		var (m, alerts, got) = await WatchAsync(now);
		using AlertService _ = alerts;

		m.Hub.Add(Review(now, null, claims: [("PNR", "Unvalidated")]));
		NeuronNotification app = m.Attention.Add(In(Application(), now))!;

		Assert.Equal(IdentityReview.NeedsReview, m.Attention.Get(app.Id)!.Review);
		Assert.True(Assert.Single(got).NeedsYou);
		await m.DisposeAsync();
	}

	[Fact]
	public async Task AutomaticDecisionSettlesIt()
	{
		DateTime now = DateTime.Now;
		var (m, alerts, got) = await WatchAsync(now);
		using AlertService _ = alerts;

		NeuronNotification approved = m.Attention.Add(In(Application(), now))!;
		NeuronNotification rejected = m.Attention.Add(In(Application("77b0e2aa@legal.lab.local"), now))!;
		m.Attention.Add(In($"Legal identity application has been automatically approved: [`{LegalId}`](https://lab.local/LegalIdentity.md?Id={LegalId}) (by **Swedish BankID**)\n", now));
		m.Attention.Add(In("Legal identity application has been automatically rejected: [`77b0e2aa@legal.lab.local`](https://lab.local/LegalIdentity.md?Id=77b0e2aa@legal.lab.local) (by **Swedish BankID**)\n", now));

		Assert.Equal(IdentityReview.Settled, m.Attention.Get(approved.Id)!.Review);
		Assert.Equal(IdentityReview.Settled, m.Attention.Get(rejected.Id)!.Review);
		Assert.Equal(0, m.Attention.OpenCount);
		Assert.DoesNotContain(got, a => a.NeedsYou);
		await m.DisposeAsync();
	}

	[Fact]
	public void NoReviewWithinTwoMinutesNeedsYou()
	{
		DateTime t0 = DateTime.Now;
		DateTime now = t0;
		AttentionInbox inbox = new();
		EventHub hub = new();
		using IdentityReviews reviews = new(inbox, hub, (_, _) => true, () => now, startTimer: false);

		NeuronNotification app = inbox.Add(In(Application(), t0))!;
		now = t0.AddSeconds(119);
		reviews.Check();
		Assert.Equal(IdentityReview.Waiting, inbox.Get(app.Id)!.Review);

		now = t0.AddMinutes(2);
		reviews.Check();
		NeuronNotification n = inbox.Get(app.Id)!;
		Assert.Equal(IdentityReview.NeedsReview, n.Review);
		Assert.True(n.IsOpen);

		// A review that comes after all still decides it.
		hub.Add(Review(now, true, claims: [("PNR", Validated)]));
		Assert.Equal(IdentityReview.Settled, inbox.Get(app.Id)!.Review);
	}

	[Fact]
	public async Task FallbackAlertsLikeANewItem()
	{
		DateTime t0 = DateTime.Now;
		var (m, alerts, got) = await WatchAsync(t0);
		using AlertService _ = alerts;
		DateTime now = t0;
		using IdentityReviews reviews = new(m.Attention, m.Hub, (_, _) => true, () => now, startTimer: false);

		NeuronNotification app = m.Attention.Add(In(Application(), t0))!;
		Assert.Empty(got);

		now = t0.AddMinutes(3);
		reviews.Check();
		reviews.Check();

		Alert a = Assert.Single(got);
		Assert.True(a.NeedsYou);
		Assert.Equal("n:" + app.Id, a.Id);
		await m.DisposeAsync();
	}

	[Fact]
	public async Task CannotReadEventsNeedsYouAtOnce()
	{
		DateTime now = DateTime.Now;
		var (m, alerts, got) = await WatchAsync(now, readsEvents: false);
		using AlertService _ = alerts;

		NeuronNotification app = m.Attention.Add(In(Application(), now))!;

		Assert.Equal(IdentityReview.NeedsReview, m.Attention.Get(app.Id)!.Review);
		Assert.Equal(1, m.Attention.OpenCount);
		Assert.True(Assert.Single(got).NeedsYou);
		await m.DisposeAsync();
	}

	[Fact]
	public async Task WaitingApplicationAfterARestart()
	{
		string file = Path.Combine(Path.GetTempPath(), "Nat.IdReviews." + Guid.NewGuid().ToString("N"), "attention.json");
		JsonAttentionStore store = new(file);
		DateTime t0 = DateTime.Now.AddMinutes(-10);
		DateTime now = t0;

		AttentionInbox inbox = new(store);
		using (IdentityReviews reviews = new(inbox, new EventHub(), (_, _) => true, () => now, startTimer: false))
		{
			inbox.Add(In(Application("old@legal.lab.local"), t0));
			inbox.Add(In(Application("settled@legal.lab.local"), t0));
			now = t0.AddMinutes(9);
			inbox.Add(In(Application("fresh@legal.lab.local"), now));
			inbox.SetReview(inbox.Items.Single(n => n.Markdown.Contains("settled@")).Id, IdentityReview.Settled, Applicant);
		}
		await inbox.DisposeAsync();

		// Ten minutes later NAT starts again.
		now = t0.AddMinutes(10);
		AttentionInbox again = new(store);
		again.Load();
		using IdentityReviews after = new(again, new EventHub(), (_, _) => true, () => now, startTimer: false);

		NeuronNotification Item(string id) => again.Items.Single(n => n.Markdown.Contains(id + "@"));
		Assert.Equal(IdentityReview.NeedsReview, Item("old").Review);
		Assert.True(Item("old").IsOpen);
		Assert.Equal(IdentityReview.Settled, Item("settled").Review);
		Assert.Equal(Applicant, Item("settled").Subject);
		Assert.Equal(IdentityReview.Waiting, Item("fresh").Review);
	}

	[Fact]
	public async Task DatabaseKeepsTheReviewAndReadsOlderItems()
	{
		EngineRuntime.Initialize();
		string folder = Path.Combine(Path.GetTempPath(), "Nat.IdReviews." + Guid.NewGuid().ToString("N"));
		DatabaseOpening o = await LocalDatabase.OpenAsync(folder, RandomNumberGenerator.GetBytes(32));
		Assert.True(o.IsOpen, o.Problem + " " + o.Error);
		FilesProvider db = o.Database!;

		DateTime at = DateTime.Now;
		NeuronNotification app = NeuronNotification.From(In(Application(), at), "a1") with { Review = IdentityReview.Settled, Applicant = Applicant };
		new DatabaseAttentionStore(db).Save([NeuronNotification.Classify(app)]);

		// An item stored before reviews were kept.
		await db.Insert(new StoredInboxItem
		{
			ItemId = "a0",
			Json = Stored.ToJson(new { Id = "a0", AccountId = account, NeuronJid = Neuron, Received = at, Markdown = Application("older@legal.lab.local"), Text = "", State = "Read" }),
		});

		IReadOnlyList<NeuronNotification> loaded = [.. new DatabaseAttentionStore(db).Load().Select(NeuronNotification.Classify)];
		await db.Stop();

		NeuronNotification back = loaded.Single(n => n.Id == "a1");
		Assert.Equal(IdentityReview.Settled, back.Review);
		Assert.Equal(Applicant, back.Applicant);

		NeuronNotification older = loaded.Single(n => n.Id == "a0");
		Assert.Equal(IdentityReview.Waiting, older.Review);
		Assert.Equal(AttentionState.Read, older.State);
	}
}
