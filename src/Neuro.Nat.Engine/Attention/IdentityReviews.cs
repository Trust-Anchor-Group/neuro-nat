using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.Engine.Attention;

/// <summary>
/// Ties legal identity applications in the Inbox to the Neuron's automatic review of them.
/// An application waits quietly until the review reports (the <c>IdReviewPerformed</c> event).
/// It needs a person only when the review left at least one claim unvalidated and invalidated
/// none. When the review validated everything, invalidated something, or the Neuron says it
/// approved or rejected the application automatically, it is settled and stays quiet. When no
/// review comes within <see cref="WaitForReview"/>, or this app cannot read the Neuron's events
/// at all, it needs a person, so Neurons without the review service still work.
/// </summary>
public sealed class IdentityReviews : IDisposable
{
	/// <summary>The event the Neuron logs when its automatic review of an application is done.</summary>
	public const string ReviewEventId = "IdReviewPerformed";

	/// <summary>Reviews are kept this long, for applications whose notification comes after the event.</summary>
	private static readonly TimeSpan keepReviews = TimeSpan.FromMinutes(30);

	private readonly AttentionInbox inbox;
	private readonly EventHub hub;
	private readonly Func<Guid, string, bool> canReadEvents;
	private readonly Func<DateTime> clock;
	private readonly Timer? timer;
	private readonly object sync = new();
	private readonly Dictionary<string, DateTime> waitingSince = [];
	private readonly List<Review> reviews = [];

	/// <summary>What one automatic review said.</summary>
	private sealed record Review(string LegalId, string? Applicant, bool NeedsPerson, DateTime At);

	/// <param name="canReadEvents">
	/// True when the Neuron's events reach the hub (account id, Neuron bare JID). When it is false,
	/// no review event can come, so an application needs a person at once. Null: always true.
	/// </param>
	/// <param name="clock">The time now. Tests set it.</param>
	/// <param name="startTimer">False in tests, which call <see cref="Check"/> themselves.</param>
	public IdentityReviews(AttentionInbox inbox, EventHub hub, Func<Guid, string, bool>? canReadEvents = null,
		Func<DateTime>? clock = null, bool startTimer = true)
	{
		this.inbox = inbox;
		this.hub = hub;
		this.canReadEvents = canReadEvents ?? ((_, _) => true);
		this.clock = clock ?? (() => DateTime.Now);

		inbox.Changed += this.Inbox_Changed;
		inbox.Reset += this.Inbox_Reset;
		hub.EventAdded += this.Hub_EventAdded;

		this.Scan();

		if (startTimer)
			this.timer = new Timer(_ => this.Check(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
	}

	/// <summary>How long an application waits for its automatic review before it needs a person.</summary>
	public static TimeSpan WaitForReview { get; } = TimeSpan.FromMinutes(2);

	/// <summary>
	/// What a review event means: true when the application needs a person (at least one
	/// "Unvalidated" claim and no "Invalidator:" claim), false when the review settled it, and null
	/// when the event is not a review of an application (another event, or a preview).
	/// </summary>
	public static bool? NeedsPerson(NeuronEvent e)
	{
		if (!string.Equals(e.EventId, ReviewEventId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(e.Object))
			return null;

		if (e.Tags.Any(t => string.Equals(t.Key, "Preview", StringComparison.OrdinalIgnoreCase) && string.Equals(t.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
			return null;

		bool unvalidated = false;
		bool invalidated = false;

		foreach (KeyValuePair<string, string> t in e.Tags)
		{
			// A claim key the sniffer could not read as a field ends up on its own line in the
			// value before it, as "key: value". Look at each line.
			foreach (string line in t.Value.Split('\n'))
			{
				string v = line.Trim();
				if (v.Length == 0)
					continue;

				if (v == "Unvalidated" || v.EndsWith(": Unvalidated", StringComparison.Ordinal))
					unvalidated = true;

				if (v.StartsWith("Invalidator:", StringComparison.Ordinal) || v.Contains(": Invalidator:", StringComparison.Ordinal))
					invalidated = true;
			}
		}

		return unvalidated && !invalidated;
	}

	/// <summary>The legal ids an application or decision notice is about: its first link's text and the id in its links.</summary>
	public static IReadOnlyList<string> LegalIdsOf(NeuronNotification n)
	{
		List<string> ids = [];

		void Add(string? s)
		{
			s = s?.Trim();
			if (!string.IsNullOrEmpty(s) && !ids.Contains(s, StringComparer.OrdinalIgnoreCase))
				ids.Add(s);
		}

		if (n.Links.Count > 0)
			Add(n.Links[0].Text);

		foreach (NotificationLink l in n.Links)
		{
			int q = l.Url.IndexOf('?');
			if (q < 0)
				continue;

			foreach (string part in l.Url[(q + 1)..].Split('&'))
			{
				int eq = part.IndexOf('=');
				if (eq > 0 && string.Equals(part[..eq], "ID", StringComparison.OrdinalIgnoreCase))
					Add(Uri.UnescapeDataString(part[(eq + 1)..]));
			}
		}

		return ids;
	}

	/// <summary>
	/// Applications that waited longer than <see cref="WaitForReview"/> need a person. Runs on a
	/// timer, and after loading.
	/// </summary>
	public void Check()
	{
		DateTime now = this.clock();
		List<string> due = [];

		lock (this.sync)
		{
			HashSet<string> waiting = [];
			foreach (NeuronNotification n in this.inbox.Items)
			{
				if (n.Kind != NotificationKind.LegalIdApplication || n.Review != IdentityReview.Waiting)
					continue;

				waiting.Add(n.Id);
				if (!this.waitingSince.TryGetValue(n.Id, out DateTime since))
					this.waitingSince[n.Id] = since = n.Received;

				if (now - since >= WaitForReview)
					due.Add(n.Id);
			}

			foreach (string id in this.waitingSince.Keys.Where(id => !waiting.Contains(id)).ToList())
				this.waitingSince.Remove(id);

			this.reviews.RemoveAll(r => now - r.At > keepReviews);
		}

		foreach (string id in due)
			this.Set(id, IdentityReview.NeedsReview, null);
	}

	private void Inbox_Reset(object? sender, EventArgs e) => this.Scan();

	/// <summary>Looks at every waiting application, for example after loading the stored Inbox.</summary>
	private void Scan()
	{
		foreach (NeuronNotification n in this.inbox.Items)
		{
			if (n.Kind == NotificationKind.LegalIdApplication && n.Review == IdentityReview.Waiting)
				this.Resolve(n, arrived: false);
		}

		this.Check();
	}

	private void Inbox_Changed(object? sender, NeuronNotification n)
	{
		if (n.Kind == NotificationKind.LegalIdApplication && n.Review == IdentityReview.Waiting)
		{
			bool known;
			lock (this.sync)
				known = this.waitingSince.ContainsKey(n.Id);

			if (!known)
				this.Resolve(n, arrived: true);
		}
		else if (n.Kind == NotificationKind.LegalIdDecision)
		{
			// The Neuron approved or rejected it without a person: nothing left to review.
			IReadOnlyList<string> ids = LegalIdsOf(n);
			foreach (NeuronNotification a in this.Applications(ids))
			{
				if (a.Review != IdentityReview.Settled)
					this.Set(a.Id, IdentityReview.Settled, null);
			}
		}
	}

	/// <summary>Decides what a waiting application is, from what is already known about it.</summary>
	private void Resolve(NeuronNotification n, bool arrived)
	{
		IReadOnlyList<string> ids = LegalIdsOf(n);
		Review? review;

		lock (this.sync)
			review = this.reviews.LastOrDefault(r => ids.Contains(r.LegalId, StringComparer.OrdinalIgnoreCase));

		if (review is not null)
		{
			this.Set(n.Id, review.NeedsPerson ? IdentityReview.NeedsReview : IdentityReview.Settled, review.Applicant);
			return;
		}

		bool decided = this.inbox.Items.Any(d => d.Kind == NotificationKind.LegalIdDecision &&
			LegalIdsOf(d).Any(id => ids.Contains(id, StringComparer.OrdinalIgnoreCase)));
		if (decided)
		{
			this.Set(n.Id, IdentityReview.Settled, null);
			return;
		}

		if (arrived && !this.canReadEvents(n.AccountId, n.NeuronJid))
		{
			// No review event can reach us: a person has to look now.
			this.Set(n.Id, IdentityReview.NeedsReview, null);
			return;
		}

		// The wait counts from when the Neuron sent it. A message delivered late, after its review
		// event has passed, needs a person when it arrives.
		DateTime now = this.clock();
		DateTime since = n.Received > now ? now : n.Received;
		if (arrived && now - since >= WaitForReview)
		{
			this.Set(n.Id, IdentityReview.NeedsReview, null);
			return;
		}

		lock (this.sync)
			this.waitingSince.TryAdd(n.Id, since);
	}

	private void Hub_EventAdded(object? sender, NeuronEvent e)
	{
		if (NeedsPerson(e) is not bool needsPerson)
			return;

		string legalId = e.Object!.Trim();
		string? applicant = BareJid(e.Actor);

		lock (this.sync)
			this.reviews.Add(new Review(legalId, applicant, needsPerson, this.clock()));

		foreach (NeuronNotification a in this.Applications([legalId]))
			this.Set(a.Id, needsPerson ? IdentityReview.NeedsReview : IdentityReview.Settled, applicant);
	}

	private IEnumerable<NeuronNotification> Applications(IReadOnlyList<string> legalIds)
	{
		if (legalIds.Count == 0)
			return [];

		return this.inbox.Items.Where(n => n.Kind == NotificationKind.LegalIdApplication &&
			LegalIdsOf(n).Any(id => legalIds.Contains(id, StringComparer.OrdinalIgnoreCase))).ToList();
	}

	private void Set(string id, IdentityReview review, string? applicant)
	{
		lock (this.sync)
			this.waitingSince.Remove(id);

		this.inbox.SetReview(id, review, applicant);
	}

	private static string? BareJid(string? jid)
	{
		if (string.IsNullOrWhiteSpace(jid))
			return null;

		jid = jid.Trim();
		int i = jid.IndexOf('/');
		return i < 0 ? jid : jid[..i];
	}

	public void Dispose()
	{
		this.inbox.Changed -= this.Inbox_Changed;
		this.inbox.Reset -= this.Inbox_Reset;
		this.hub.EventAdded -= this.Hub_EventAdded;
		this.timer?.Dispose();
	}
}
