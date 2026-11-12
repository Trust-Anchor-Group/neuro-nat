using System.Security.Cryptography;
using System.Text;

namespace Neuro.Nat.Engine.Events;

/// <summary>
/// Collects events from every Neuron and account into one stream, removes duplicates, and
/// groups warnings and worse into issues. Thread-safe. Change notifications are raised on
/// the calling thread; the UI batches them.
/// </summary>
public sealed class EventHub(int capacity = 50_000)
{
	private readonly object sync = new();
	private readonly LinkedList<NeuronEvent> events = new();
	private readonly Dictionary<string, Issue> issues = [];
	private readonly HashSet<string> seen = [];
	private readonly Queue<string> seenOrder = new();
	private long sequence;

	/// <summary>Raised for each new (non-duplicate) event.</summary>
	public event EventHandler<NeuronEvent>? EventAdded;

	/// <summary>Raised when an issue is created or updated.</summary>
	public event EventHandler<Issue>? IssueChanged;

	/// <summary>Number of events kept.</summary>
	public int Count
	{
		get
		{
			lock (this.sync)
				return this.events.Count;
		}
	}

	/// <summary>Adds an event. Returns false if it was a duplicate.</summary>
	public bool Add(NeuronEvent e)
	{
		Issue? changed = null;

		lock (this.sync)
		{
			string key = DuplicateKey(e);
			if (!this.seen.Add(key))
				return false;

			this.seenOrder.Enqueue(key);
			if (this.seenOrder.Count > capacity * 2)
				this.seen.Remove(this.seenOrder.Dequeue());

			e = e with { Sequence = ++this.sequence };
			this.events.AddLast(e);
			if (this.events.Count > capacity)
				this.events.RemoveFirst();

			if (e.Severity >= EventSeverity.Warning)
			{
				if (!this.issues.TryGetValue(e.Fingerprint, out Issue? issue))
				{
					issue = new Issue(e);
					this.issues[e.Fingerprint] = issue;
				}

				issue.Add(e);
				changed = issue;
			}
		}

		this.EventAdded?.Invoke(this, e);
		if (changed is not null)
			this.IssueChanged?.Invoke(this, changed);

		return true;
	}

	/// <summary>All kept events, oldest first.</summary>
	public IReadOnlyList<NeuronEvent> Snapshot()
	{
		lock (this.sync)
			return [.. this.events];
	}

	/// <summary>The newest kept event that matches, or null.</summary>
	public NeuronEvent? FindLast(Func<NeuronEvent, bool> match)
	{
		lock (this.sync)
		{
			for (LinkedListNode<NeuronEvent>? n = this.events.Last; n is not null; n = n.Previous)
			{
				if (match(n.Value))
					return n.Value;
			}

			return null;
		}
	}

	/// <summary>All issues, most recent first.</summary>
	public IReadOnlyList<Issue> Issues()
	{
		lock (this.sync)
			return [.. this.issues.Values.OrderByDescending(i => i.LastSeen)];
	}

	/// <summary>Events belonging to one issue, newest first.</summary>
	public IReadOnlyList<NeuronEvent> EventsOf(string fingerprint, int max = 500)
	{
		lock (this.sync)
		{
			List<NeuronEvent> result = [];
			for (LinkedListNode<NeuronEvent>? n = this.events.Last; n is not null && result.Count < max; n = n.Previous)
			{
				if (n.Value.Fingerprint == fingerprint)
					result.Add(n.Value);
			}
			return result;
		}
	}

	/// <summary>Per-Neuron figures for the fleet overview.</summary>
	public sealed record NeuronSummary(int ErrorsLastHour, int WarningsLastHour, int OpenIssues, DateTime? LastEvent);

	/// <summary>Summarizes events and open issues per Neuron (bare JID).</summary>
	public IReadOnlyDictionary<string, NeuronSummary> SummarizeByNeuron(DateTime now)
	{
		DateTime hourAgo = now.AddHours(-1);
		Dictionary<string, (int Errors, int Warnings, DateTime? Last)> counts = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> open = new(StringComparer.OrdinalIgnoreCase);

		lock (this.sync)
		{
			foreach (NeuronEvent e in this.events)
			{
				counts.TryGetValue(e.Source.NeuronJid, out var c);
				if (e.Timestamp >= hourAgo)
				{
					if (e.Severity >= EventSeverity.Error)
						c.Errors++;
					else if (e.Severity == EventSeverity.Warning)
						c.Warnings++;
				}
				if (c.Last is null || e.Timestamp > c.Last)
					c.Last = e.Timestamp;
				counts[e.Source.NeuronJid] = c;
			}

			foreach (Issue i in this.issues.Values)
			{
				if (i.State is IssueState.Open or IssueState.Acknowledged)
				{
					foreach (string jid in i.Neurons)
						open[jid] = open.TryGetValue(jid, out int n) ? n + 1 : 1;
				}
			}
		}

		Dictionary<string, NeuronSummary> result = new(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, (int Errors, int Warnings, DateTime? Last)> p in counts)
			result[p.Key] = new NeuronSummary(p.Value.Errors, p.Value.Warnings, open.TryGetValue(p.Key, out int n) ? n : 0, p.Value.Last);

		return result;
	}

	/// <summary>Changes the state of an issue.</summary>
	public void SetState(string fingerprint, IssueState state)
	{
		Issue? issue;
		lock (this.sync)
		{
			if (!this.issues.TryGetValue(fingerprint, out issue))
				return;
			issue.State = state;
			issue.Reopened = false;
		}

		this.IssueChanged?.Invoke(this, issue);
	}

	private static string DuplicateKey(NeuronEvent e)
	{
		string s = string.Join("\u001f", e.Source.NeuronJid, e.Timestamp.Ticks, e.Severity, e.Message, e.EventId, e.Object);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)), 0, 12);
	}
}
