using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.ViewModels;

/// <summary>One issue in the Issues list. Refreshed from the engine's <see cref="Issue"/> in batches.</summary>
public sealed partial class IssueItemViewModel(Issue issue) : ObservableObject
{
	public Issue Issue { get; } = issue;

	public string Fingerprint => this.Issue.Fingerprint;

	[ObservableProperty]
	private string title = string.Empty;

	[ObservableProperty]
	private EventSeverity severity;

	[ObservableProperty]
	private int count;

	[ObservableProperty]
	private string meta = string.Empty;

	/// <summary>"12 events on Lab since 10:42, latest 4 min ago"</summary>
	[ObservableProperty]
	private string countText = string.Empty;

	[ObservableProperty]
	private int[] histogram = [];

	[ObservableProperty]
	private IssueState state;

	[ObservableProperty]
	private bool isReopened;

	[ObservableProperty]
	private DateTime lastSeen;

	/// <summary>+1 when the last 30 minutes saw clearly more of this than the 30 before, -1 when clearly fewer, else 0.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsRising), nameof(IsFalling))]
	private int trend;

	[ObservableProperty]
	private string? trendText;

	public bool IsRising => this.Trend > 0;
	public bool IsFalling => this.Trend < 0;

	/// <summary>Compares the two halves of an hour of per-minute counts.</summary>
	internal static int TrendOf(ReadOnlySpan<int> perMinute, out int before, out int recent)
	{
		int half = perMinute.Length / 2;
		before = 0;
		recent = 0;
		for (int i = 0; i < perMinute.Length; i++)
		{
			if (i < half)
				before += perMinute[i];
			else
				recent += perMinute[i];
		}

		// A difference of one or two events is noise, not a trend.
		if (recent - before >= 3 && recent > before * 1.25)
			return 1;
		if (before - recent >= 3 && recent < before * 0.75)
			return -1;
		return 0;
	}

	/// <summary>Re-reads the issue. Call on the UI thread.</summary>
	public void Refresh(DateTime now)
	{
		Issue i = this.Issue;
		this.Title = i.Title;
		this.Severity = i.Severity;
		this.Count = i.Count;
		this.State = i.State;
		this.IsReopened = i.Reopened;
		this.LastSeen = i.LastSeen;
		int[] histogram = i.Histogram(now, 60);
		if (this.Histogram is null || !this.Histogram.AsSpan().SequenceEqual(histogram))
			this.Histogram = histogram;

		this.Trend = TrendOf(histogram, out int before, out int recent);
		this.TrendText = this.Trend == 0 ? null
			: $"{(this.Trend > 0 ? "Rising" : "Falling")}: {recent} in the last 30 minutes, {before} in the 30 before";

		int neurons = i.Neurons.Count;
		string where = neurons == 1 ? Converters.NeuronNameConverter.Name(i.Neurons[0]) : $"{neurons} Neurons";
		string since = i.FirstSeen.Date == now.Date ? i.FirstSeen.ToString("HH:mm") : i.FirstSeen.ToString("yyyy-MM-dd HH:mm");
		string phrase = $"on {where} since {since}, latest {RelativeTime.Format(i.LastSeen, now)}";
		this.Meta = "O" + phrase[1..];
		this.CountText = (i.Count == 1 ? "1 event " : $"{i.Count:N0} events ") + phrase;
	}
}

/// <summary>Short relative times for lists: "now", "4 min ago", "2 h ago", or a date.</summary>
public static class RelativeTime
{
	public static string Format(DateTime t, DateTime now)
	{
		TimeSpan d = now - t;
		if (d < TimeSpan.FromSeconds(45))
			return "now";
		if (d < TimeSpan.FromMinutes(60))
			return $"{Math.Max(1, (int)Math.Round(d.TotalMinutes))} min ago";
		if (d < TimeSpan.FromHours(24))
			return $"{(int)d.TotalHours} h ago";
		return t.ToString("yyyy-MM-dd HH:mm");
	}
}
