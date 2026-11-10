using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.ViewModels;

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

	[ObservableProperty]
	private int[] histogram = [];

	[ObservableProperty]
	private IssueState state;

	[ObservableProperty]
	private bool isReopened;

	[ObservableProperty]
	private DateTime lastSeen;

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

		int neurons = i.Neurons.Count;
		string where = neurons == 1 ? i.Neurons[0] : $"{neurons} Neurons";
		string since = i.FirstSeen.Date == now.Date ? i.FirstSeen.ToString("HH:mm") : i.FirstSeen.ToString("yyyy-MM-dd HH:mm");
		this.Meta = $"{where} · since {since} · last {RelativeTime.Format(i.LastSeen, now)}";
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
