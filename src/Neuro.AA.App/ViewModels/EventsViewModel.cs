using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.ViewModels;

/// <summary>Which events the stream shows.</summary>
public enum SeverityFilter
{
	All,
	WarningsAndWorse,
	ErrorsAndWorse,
}

/// <summary>
/// The Events workspace: issues on the left, the live stream on the right, details of the
/// selected event below. Engine notifications arrive on network threads and are applied to
/// the collections in batches a few times per second.
/// </summary>
public sealed partial class EventsViewModel : ObservableObject, IDisposable
{
	private const int MaxStream = 2000;

	private readonly MonitorService monitors;
	private readonly ConcurrentQueue<NeuronEvent> pendingEvents = new();
	private readonly ConcurrentDictionary<string, Issue> pendingIssues = new();
	private readonly Dictionary<string, IssueItemViewModel> issueIndex = [];
	private readonly DispatcherTimer flush;
	private readonly DispatcherTimer clock;
	private int neuronsDirty = 1;

	public EventsViewModel(MonitorService monitors)
	{
		this.monitors = monitors;
		monitors.Hub.EventAdded += this.Hub_EventAdded;
		monitors.Hub.IssueChanged += this.Hub_IssueChanged;
		monitors.NeuronsChanged += this.Monitors_NeuronsChanged;

		foreach (Issue i in monitors.Hub.Issues())
			this.pendingIssues[i.Fingerprint] = i;
		foreach (NeuronEvent e in monitors.Hub.Snapshot().OrderBy(e => e.Timestamp).TakeLast(MaxStream))
			this.pendingEvents.Enqueue(e);

		this.flush = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => this.Flush());
		this.flush.Start();
		this.clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => this.RefreshTimes());
		this.clock.Start();
	}

	/// <summary>
	/// Events shown in the stream, newest first. An AvaloniaList, so a batch of new events is one
	/// change for the list control rather than one per event.
	/// </summary>
	public Avalonia.Collections.AvaloniaList<NeuronEvent> Stream { get; } = [];

	/// <summary>New events kept aside while the stream is scrolled down, so rows do not move under the pointer.</summary>
	private readonly List<NeuronEvent> held = [];

	private DateTime lastIssueRebuild = DateTime.MinValue;

	/// <summary>Set by the view: true while the stream shows its newest events.</summary>
	public bool IsStreamAtTop { get; set; } = true;

	/// <summary>Raised when the view should scroll the stream to its newest event.</summary>
	public event EventHandler? ScrollStreamToTop;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasHeld), nameof(HeldText))]
	private int heldCount;

	public bool HasHeld => this.HeldCount > 0;
	public string HeldText => this.HeldCount == 1 ? "1 new event" : $"{this.HeldCount:N0} new events";

	/// <summary>Shows the events kept aside and scrolls to them.</summary>
	[RelayCommand]
	private void ShowHeld()
	{
		this.InsertNewest(this.held);
		this.held.Clear();
		this.HeldCount = 0;
		this.ScrollStreamToTop?.Invoke(this, EventArgs.Empty);
	}

	private void InsertNewest(List<NeuronEvent> batch)
	{
		if (batch.Count == 0)
			return;

		// Arrival order is oldest first; the stream is newest first.
		List<NeuronEvent> newest = [.. Enumerable.Reverse(batch)];
		if (newest.Count > MaxStream)
			newest.RemoveRange(MaxStream, newest.Count - MaxStream);

		this.Stream.InsertRange(0, newest);
		if (this.Stream.Count > MaxStream)
			this.Stream.RemoveRange(MaxStream, this.Stream.Count - MaxStream);
	}

	/// <summary>Issues, open ones first, most recent first.</summary>
	public ObservableCollection<IssueItemViewModel> Issues { get; } = [];

	/// <summary>Neurons and the state of their event streams.</summary>
	public ObservableCollection<WatchedNeuron> Neurons { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAllSelected), nameof(IsWarningsSelected), nameof(IsErrorsSelected))]
	private SeverityFilter severityFilter = SeverityFilter.WarningsAndWorse;

	public bool IsAllSelected => this.SeverityFilter == SeverityFilter.All;
	public bool IsWarningsSelected => this.SeverityFilter == SeverityFilter.WarningsAndWorse;
	public bool IsErrorsSelected => this.SeverityFilter == SeverityFilter.ErrorsAndWorse;

	[ObservableProperty]
	private string searchText = string.Empty;

	[ObservableProperty]
	private bool showResolved;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelectedIssue), nameof(StreamTitle))]
	private IssueItemViewModel? selectedIssue;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelectedEvent))]
	private NeuronEvent? selectedEvent;

	[ObservableProperty]
	private bool isPaused;

	[ObservableProperty]
	private int liveNeuronCount;

	[ObservableProperty]
	private string neuronSummary = "No Neurons yet";

	[ObservableProperty]
	private bool hasNeurons;

	[ObservableProperty]
	private int errorsLastHour;

	[ObservableProperty]
	private bool isStreamEmpty = true;

	[ObservableProperty]
	private bool hasIssues;

	public bool HasSelectedIssue => this.SelectedIssue is not null;
	public bool HasSelectedEvent => this.SelectedEvent is not null;
	public string StreamTitle => this.SelectedIssue is null ? "Live stream" : "Events in this issue";

	/// <summary>Set by the view: copies text to the clipboard.</summary>
	public Func<string, Task>? CopyToClipboard { get; set; }

	/// <summary>Set by the shell: sends a title and Markdown to Chat to pick who gets it.</summary>
	public Action<string, string>? ShareTo { get; set; }

	[RelayCommand]
	private void ShareSelectedEvent()
	{
		if (this.SelectedEvent is NeuronEvent e)
			this.ShareTo?.Invoke("Event: " + e.Title, ToMarkdown(e));
	}

	[RelayCommand]
	private void ShareIssue(IssueItemViewModel? issue)
	{
		issue ??= this.SelectedIssue;
		if (issue is null)
			return;

		Issue i = issue.Issue;
		StringBuilder sb = new();
		sb.Append("**Issue:** ").AppendLine(i.Title).AppendLine();
		sb.AppendLine("| | |");
		sb.AppendLine("|:--|:--|");
		sb.Append("| Severity | ").Append(i.Severity).AppendLine(" |");
		sb.Append("| Occurrences | ").Append(i.Count).AppendLine(" |");
		sb.Append("| First seen | ").Append(i.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine(" |");
		sb.Append("| Last seen | ").Append(i.LastSeen.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine(" |");
		sb.Append("| Neurons | ").Append(string.Join(", ", i.Neurons.Select(n => "`" + n + "`"))).AppendLine(" |");
		sb.AppendLine().AppendLine("Latest occurrence:").AppendLine().Append(ToMarkdown(i.Latest));
		this.ShareTo?.Invoke("Issue: " + i.Title, sb.ToString());
	}

	partial void OnSeverityFilterChanged(SeverityFilter value) => this.Rebuild();
	partial void OnSearchTextChanged(string value) => this.Rebuild();
	partial void OnSelectedIssueChanged(IssueItemViewModel? value) => this.Rebuild();
	partial void OnShowResolvedChanged(bool value) => this.RebuildIssues();

	[RelayCommand]
	private void SetFilter(SeverityFilter filter) => this.SeverityFilter = filter;

	[RelayCommand]
	private void ClearIssue() => this.SelectedIssue = null;

	[RelayCommand]
	private void Acknowledge(IssueItemViewModel? issue) => this.SetState(issue, IssueState.Acknowledged);

	[RelayCommand]
	private void Mute(IssueItemViewModel? issue) => this.SetState(issue, IssueState.Muted);

	[RelayCommand]
	private void Resolve(IssueItemViewModel? issue) => this.SetState(issue, IssueState.Resolved);

	[RelayCommand]
	private void Reopen(IssueItemViewModel? issue) => this.SetState(issue, IssueState.Open);

	[RelayCommand]
	private async Task CopySelectedEvent()
	{
		if (this.SelectedEvent is NeuronEvent e && this.CopyToClipboard is Func<string, Task> copy)
			await copy(ToMarkdown(e));
	}

	private void SetState(IssueItemViewModel? issue, IssueState state)
	{
		issue ??= this.SelectedIssue;
		if (issue is not null)
			this.monitors.Hub.SetState(issue.Fingerprint, state);
	}

	private void Hub_EventAdded(object? sender, NeuronEvent e) => this.pendingEvents.Enqueue(e);

	private void Hub_IssueChanged(object? sender, Issue i) => this.pendingIssues[i.Fingerprint] = i;

	private void Monitors_NeuronsChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref this.neuronsDirty, 1);

	private bool Matches(NeuronEvent e)
	{
		if (this.SelectedIssue is IssueItemViewModel issue)
			return e.Fingerprint == issue.Fingerprint;

		switch (this.SeverityFilter)
		{
			case SeverityFilter.WarningsAndWorse when e.Severity < EventSeverity.Warning:
			case SeverityFilter.ErrorsAndWorse when e.Severity < EventSeverity.Error:
				return false;
		}

		string q = this.SearchText.Trim();
		if (q.Length == 0)
			return true;

		return e.Message.Contains(q, StringComparison.OrdinalIgnoreCase)
			|| (e.Module?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
			|| (e.Facility?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
			|| (e.EventId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
			|| (e.Object?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
			|| e.Source.NeuronJid.Contains(q, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Applies pending engine changes. Runs on the UI thread a few times per second.</summary>
	public void Flush()
	{
		DateTime now = DateTime.Now;

		if (!this.IsPaused && !this.pendingEvents.IsEmpty)
		{
			List<NeuronEvent> batch = [];
			while (this.pendingEvents.TryDequeue(out NeuronEvent? e))
			{
				if (this.Matches(e))
					batch.Add(e);
			}

			if (this.IsStreamAtTop && this.held.Count == 0)
				this.InsertNewest(batch);
			else
			{
				this.held.AddRange(batch);
				if (this.held.Count > MaxStream)
					this.held.RemoveRange(0, this.held.Count - MaxStream);
				this.HeldCount = this.held.Count;
			}
		}

		// Issues re-sort at most once a second: moving rows is what makes a list stutter.
		if (!this.pendingIssues.IsEmpty && now - this.lastIssueRebuild >= TimeSpan.FromSeconds(1))
		{
			this.lastIssueRebuild = now;
			foreach (string key in this.pendingIssues.Keys.ToList())
			{
				if (!this.pendingIssues.TryRemove(key, out Issue? issue))
					continue;

				if (!this.issueIndex.TryGetValue(key, out IssueItemViewModel? vm))
				{
					vm = new IssueItemViewModel(issue);
					this.issueIndex[key] = vm;
				}

				vm.Refresh(now);
			}

			this.RebuildIssues();
			this.ErrorsLastHour = this.issueIndex.Values.Where(i => i.Severity >= EventSeverity.Error).Sum(i => i.Issue.CountSince(now, TimeSpan.FromHours(1)));
		}

		if (Interlocked.Exchange(ref this.neuronsDirty, 0) == 1)
			this.RefreshNeurons();

		this.IsStreamEmpty = this.Stream.Count == 0 && this.held.Count == 0;
	}

	private void RebuildIssues()
	{
		IssueItemViewModel? selected = this.SelectedIssue;

		List<IssueItemViewModel> ordered = [.. this.issueIndex.Values
			.Where(i => this.ShowResolved || i.State is IssueState.Open or IssueState.Acknowledged)
			.OrderBy(i => i.State == IssueState.Open ? 0 : i.State == IssueState.Acknowledged ? 1 : 2)
			.ThenByDescending(i => i.Severity >= EventSeverity.Error)
			.ThenByDescending(i => i.LastSeen)];

		// Update in place to keep selection and scroll position stable.
		for (int i = 0; i < ordered.Count; i++)
		{
			if (i < this.Issues.Count)
			{
				if (!ReferenceEquals(this.Issues[i], ordered[i]))
				{
					int existing = this.Issues.IndexOf(ordered[i]);
					if (existing > i)
						this.Issues.Move(existing, i);
					else
						this.Issues.Insert(i, ordered[i]);
				}
			}
			else
				this.Issues.Add(ordered[i]);
		}

		while (this.Issues.Count > ordered.Count)
			this.Issues.RemoveAt(this.Issues.Count - 1);

		if (selected is not null && this.Issues.Contains(selected))
			this.SelectedIssue = selected;

		this.HasIssues = this.Issues.Count > 0;
	}

	private void Rebuild()
	{
		this.held.Clear();
		this.HeldCount = 0;
		IEnumerable<NeuronEvent> source = this.SelectedIssue is IssueItemViewModel issue
			? this.monitors.Hub.EventsOf(issue.Fingerprint, MaxStream)
			: this.monitors.Hub.Snapshot().AsEnumerable().Reverse();

		List<NeuronEvent> list = [.. source.Where(this.Matches).OrderByDescending(e => e.Timestamp).Take(MaxStream)];
		this.Stream.Clear();
		this.Stream.AddRange(list);

		this.IsStreamEmpty = this.Stream.Count == 0;
	}

	private void RefreshNeurons()
	{
		IReadOnlyList<WatchedNeuron> list = this.monitors.Neurons;
		this.Neurons.Clear();
		foreach (WatchedNeuron n in list.OrderBy(n => n.Label))
			this.Neurons.Add(n);

		this.HasNeurons = list.Count > 0;
		this.LiveNeuronCount = list.Count(n => n.State == WatchState.Live);
		this.NeuronSummary = list.Count switch
		{
			0 => "No Neurons yet",
			1 => this.LiveNeuronCount == 1 ? "1 Neuron, live" : "1 Neuron, not live",
			_ => $"{list.Count} Neurons, {this.LiveNeuronCount} live",
		};
	}

	private void RefreshTimes()
	{
		DateTime now = DateTime.Now;
		foreach (IssueItemViewModel vm in this.issueIndex.Values)
			vm.Refresh(now);
	}

	/// <summary>An event as Markdown, for pasting into chat or a ticket.</summary>
	public static string ToMarkdown(NeuronEvent e)
	{
		StringBuilder sb = new();
		sb.Append("**").Append(e.Severity).Append("** on `").Append(e.Source.NeuronJid).Append("` at ").Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine();
		sb.AppendLine();
		sb.AppendLine(e.Message);
		sb.AppendLine();

		void Field(string name, string? value)
		{
			if (!string.IsNullOrEmpty(value))
				sb.Append("| ").Append(name).Append(" | `").Append(value.Replace("|", "\\|")).AppendLine("` |");
		}

		sb.AppendLine("| Field | Value |");
		sb.AppendLine("|---|---|");
		Field("Event ID", e.EventId);
		Field("Object", e.Object);
		Field("Actor", e.Actor);
		Field("Facility", e.Facility);
		Field("Module", e.Module);
		foreach (KeyValuePair<string, string> t in e.Tags)
			Field(t.Key, t.Value);

		if (!string.IsNullOrEmpty(e.StackTrace))
		{
			sb.AppendLine();
			sb.AppendLine("```");
			sb.AppendLine(e.StackTrace);
			sb.AppendLine("```");
		}

		return sb.ToString();
	}

	public void Dispose()
	{
		this.flush.Stop();
		this.clock.Stop();
		this.monitors.Hub.EventAdded -= this.Hub_EventAdded;
		this.monitors.Hub.IssueChanged -= this.Hub_IssueChanged;
		this.monitors.NeuronsChanged -= this.Monitors_NeuronsChanged;
	}
}
