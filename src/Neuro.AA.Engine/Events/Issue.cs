namespace Neuro.AA.Engine.Events;

/// <summary>What the operator has decided about an issue.</summary>
public enum IssueState
{
	Open,
	Acknowledged,
	Muted,

	/// <summary>Resolved issues reopen automatically when the problem comes back.</summary>
	Resolved,
}

/// <summary>
/// A group of events with the same fingerprint, across all Neurons. Mutable; all access
/// goes through <see cref="EventHub"/>, which locks.
/// </summary>
public sealed class Issue
{
	private readonly Dictionary<long, int> perMinute = [];
	private readonly HashSet<string> neurons = new(StringComparer.OrdinalIgnoreCase);

	internal Issue(NeuronEvent first)
	{
		this.Fingerprint = first.Fingerprint;
		this.FirstSeen = first.Timestamp;
		this.Latest = first;
	}

	public string Fingerprint { get; }

	public DateTime FirstSeen { get; private set; }

	public DateTime LastSeen { get; private set; }

	public int Count { get; private set; }

	/// <summary>Highest severity seen.</summary>
	public EventSeverity Severity { get; private set; }

	public NeuronEvent Latest { get; private set; }

	public IssueState State { get; internal set; }

	/// <summary>Set when a resolved issue came back.</summary>
	public bool Reopened { get; internal set; }

	/// <summary>Neurons where this issue has occurred (a copy).</summary>
	public IReadOnlyList<string> Neurons
	{
		get
		{
			lock (this.perMinute)
				return [.. this.neurons];
		}
	}

	public string Title => this.Latest.Title;

	/// <summary>Occurrences per minute for the last <paramref name="minutes"/> minutes, oldest first.</summary>
	public int[] Histogram(DateTime now, int minutes = 60)
	{
		long current = MinuteOf(now);
		int[] result = new int[minutes];
		lock (this.perMinute)
		{
			for (int i = 0; i < minutes; i++)
				result[i] = this.perMinute.TryGetValue(current - (minutes - 1 - i), out int n) ? n : 0;
		}
		return result;
	}

	/// <summary>Occurrences in the last <paramref name="window"/>.</summary>
	public int CountSince(DateTime now, TimeSpan window)
	{
		long from = MinuteOf(now - window);
		lock (this.perMinute)
			return this.perMinute.Where(p => p.Key >= from).Sum(p => p.Value);
	}

	internal void Add(NeuronEvent e)
	{
		lock (this.perMinute)
			this.AddLocked(e);
	}

	private void AddLocked(NeuronEvent e)
	{
		this.Count++;
		if (e.Timestamp < this.FirstSeen)
			this.FirstSeen = e.Timestamp;
		if (e.Timestamp >= this.LastSeen)
		{
			this.LastSeen = e.Timestamp;
			this.Latest = e;
		}
		if (e.Severity > this.Severity)
			this.Severity = e.Severity;

		this.neurons.Add(e.Source.NeuronJid);

		long m = MinuteOf(e.Timestamp);
		this.perMinute[m] = this.perMinute.TryGetValue(m, out int n) ? n + 1 : 1;

		// Keep about a day of minute buckets.
		if (this.perMinute.Count > 1500)
		{
			long cutoff = m - 1440;
			foreach (long k in this.perMinute.Keys.Where(k => k < cutoff).ToList())
				this.perMinute.Remove(k);
		}

		if (this.State == IssueState.Resolved)
		{
			this.State = IssueState.Open;
			this.Reopened = true;
		}
	}

	private static long MinuteOf(DateTime t) => t.Ticks / TimeSpan.TicksPerMinute;
}
