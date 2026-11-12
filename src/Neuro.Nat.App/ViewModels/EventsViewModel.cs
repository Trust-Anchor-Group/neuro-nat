using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.ViewModels;

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
public sealed partial class EventsViewModel : ObservableObject, IDisposable, IActionSource
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

	/// <summary>The bare JIDs of the Neurons to show, set by the shell's Neuron filter. Empty shows all.</summary>
	public IReadOnlySet<string> NeuronFilter
	{
		get => this.neuronFilter;
		set
		{
			value ??= NeuronSet.All;
			if (value.SameAs(this.neuronFilter))
				return;

			this.neuronFilter = value;
			this.OnPropertyChanged();
			this.OnNeuronFilterChanged();
		}
	}

	private IReadOnlySet<string> neuronFilter = NeuronSet.All;

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
	public string StreamTitle => this.SelectedIssue is null ? "All events" : "Events in this problem";

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

		this.ShareTo?.Invoke("Problem: " + issue.Issue.Title, IssueMarkdown(issue.Issue));
	}

	partial void OnSeverityFilterChanged(SeverityFilter value) => this.Rebuild();
	partial void OnSearchTextChanged(string value) => this.Rebuild();
	partial void OnSelectedIssueChanged(IssueItemViewModel? value) => this.Rebuild();
	partial void OnShowResolvedChanged(bool value) => this.RebuildIssues();

	private void OnNeuronFilterChanged()
	{
		if (this.SelectedIssue is IssueItemViewModel issue && !this.IsOnFilteredNeuron(issue))
			this.SelectedIssue = null;

		this.RebuildIssues();
		this.Rebuild();
		this.RefreshErrorCount(DateTime.Now);
	}

	private bool IsOnFilteredNeuron(IssueItemViewModel issue)
		=> this.NeuronFilter.Count == 0 || issue.Issue.Neurons.Any(this.NeuronFilter.Contains);

	/// <summary>Errors in the last hour: everywhere, or on the filtered Neurons.</summary>
	private void RefreshErrorCount(DateTime now)
	{
		if (this.NeuronFilter.Count == 0)
		{
			this.ErrorsLastHour = this.issueIndex.Values.Where(i => i.Severity >= EventSeverity.Error).Sum(i => i.Issue.CountSince(now, TimeSpan.FromHours(1)));
			return;
		}

		IReadOnlyDictionary<string, EventHub.NeuronSummary> byNeuron = this.monitors.Hub.SummarizeByNeuron(now);
		this.ErrorsLastHour = this.NeuronFilter.Sum(jid => byNeuron.TryGetValue(jid, out EventHub.NeuronSummary? s) ? s.ErrorsLastHour : 0);
	}

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
			this.SetStates([issue], state);
	}

	/// <summary>
	/// Acknowledges, mutes, marks fixed or reopens problems, says so, and offers to take it
	/// back for a few seconds: these are one click, and easy to do to the wrong row.
	/// </summary>
	private void SetStates(IReadOnlyList<IssueItemViewModel> issues, IssueState state)
	{
		List<(string Fingerprint, IssueState Was)> before = [.. issues.Where(i => i.State != state).Select(i => (i.Fingerprint, i.State))];
		if (before.Count == 0)
			return;

		foreach ((string fingerprint, IssueState _) in before)
			this.monitors.Hub.SetState(fingerprint, state);

		string done = state switch
		{
			IssueState.Acknowledged => "acknowledged",
			IssueState.Muted => "muted",
			IssueState.Resolved => "marked fixed",
			_ => "reopened",
		};
		string note = before.Count == 1 ? char.ToUpperInvariant(done[0]) + done[1..] : Ui.Count(before.Count, "problem") + " " + done;
		Ui.Say(note, () =>
		{
			foreach ((string fingerprint, IssueState was) in before)
				this.monitors.Hub.SetState(fingerprint, was);
		});
	}

	private void Hub_EventAdded(object? sender, NeuronEvent e) => this.pendingEvents.Enqueue(e);

	private void Hub_IssueChanged(object? sender, Issue i) => this.pendingIssues[i.Fingerprint] = i;

	private void Monitors_NeuronsChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref this.neuronsDirty, 1);

	private bool Matches(NeuronEvent e)
	{
		if (!this.NeuronFilter.Admits(e.Source.NeuronJid))
			return false;

		if (this.SelectedIssue is IssueItemViewModel issue)
			return e.Fingerprint == issue.Fingerprint;

		if (this.hidden.Count > 0 && this.hidden.Contains(e.Fingerprint))
			return false;

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
			this.RefreshErrorCount(now);
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
			.Where(this.IsOnFilteredNeuron)
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
		IReadOnlyList<NeuronEvent> snapshot = this.monitors.Hub.Snapshot();

		// Events still waiting for the next flush are already in the snapshot: showing them now and
		// again at the flush would list them twice.
		if (!this.pendingEvents.IsEmpty)
		{
			HashSet<NeuronEvent> shown = new(snapshot, ReferenceEqualityComparer.Instance);
			List<NeuronEvent> later = [];
			while (this.pendingEvents.TryDequeue(out NeuronEvent? e))
			{
				if (!shown.Contains(e))
					later.Add(e);
			}

			foreach (NeuronEvent e in later)
				this.pendingEvents.Enqueue(e);
		}

		IEnumerable<NeuronEvent> source = this.SelectedIssue is IssueItemViewModel issue
			? this.monitors.Hub.EventsOf(issue.Fingerprint, MaxStream)
			: snapshot.AsEnumerable().Reverse();

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
			1 => this.LiveNeuronCount == 1 ? "1 Neuron, reporting" : "1 Neuron, not reporting",
			_ => $"{list.Count} Neurons, {this.LiveNeuronCount} reporting",
		};
	}

	private void RefreshTimes()
	{
		DateTime now = DateTime.Now;
		foreach (IssueItemViewModel vm in this.issueIndex.Values)
			vm.Refresh(now);
	}

	// ----- Actions: the right-click menu, the selection bar and the shortcuts -----

	/// <summary>Set by the shell: the moves to other screens.</summary>
	public IShell? Shell { get; set; }

	/// <summary>Set by the view: the events selected in the stream.</summary>
	public IReadOnlyList<NeuronEvent> SelectedEvents { get; set; } = [];

	/// <summary>Kinds of events the person asked not to see, by fingerprint. Kept until the app closes.</summary>
	private readonly HashSet<string> hidden = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasHidden), nameof(HiddenText))]
	private int hiddenCount;

	public bool HasHidden => this.HiddenCount > 0;
	public string HiddenText => this.HiddenCount == 1 ? "1 kind of event hidden" : $"{this.HiddenCount} kinds of events hidden";

	[RelayCommand]
	private void ShowHidden()
	{
		this.hidden.Clear();
		this.HiddenCount = 0;
		this.Rebuild();
	}

	private void Hide(IEnumerable<NeuronEvent> events)
	{
		foreach (NeuronEvent e in events)
			this.hidden.Add(e.Fingerprint);

		this.HiddenCount = this.hidden.Count;
		this.Rebuild();
		Ui.Say("Hidden until you close the app. Show them again above the list.");
	}

	private static readonly string[] eventColumns = ["Time", "Level", "Neuron", "Where", "Message", "Event ID", "Object", "Actor", "Facility"];

	private static string?[] Cells(NeuronEvent e)
		=> [e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), e.Severity.ToString(), e.Source.NeuronJid, e.Module, e.Message, e.EventId, e.Object, e.Actor, e.Facility];

	/// <summary>Events as tab-separated rows, oldest first, which pastes into a spreadsheet or a chat.</summary>
	public static string ToRows(IEnumerable<NeuronEvent> events, bool header = true)
		=> Export.Tabs(eventColumns, events.OrderBy(e => e.Timestamp).Select(Cells), header);

	public static string ToCsv(IEnumerable<NeuronEvent> events)
		=> Export.Csv(eventColumns, events.OrderBy(e => e.Timestamp).Select(Cells));

	public static string ToMarkdown(IEnumerable<NeuronEvent> events)
		=> string.Join(Environment.NewLine + "---" + Environment.NewLine + Environment.NewLine, events.OrderBy(e => e.Timestamp).Select(ToMarkdown));

	private static SaveFormat[] Formats(IReadOnlyList<NeuronEvent> events) =>
	[
		new SaveFormat("Spreadsheet (CSV)", "csv", () => ToCsv(events)),
		new SaveFormat("Markdown", "md", () => ToMarkdown(events)),
		new SaveFormat("Text", "txt", () => ToRows(events)),
	];

	/// <summary>Toolbar Save: what is selected, or everything the filter shows when nothing is.</summary>
	[RelayCommand]
	private Task SaveStream()
	{
		IReadOnlyList<NeuronEvent> events = this.SelectedEvents.Count > 1 ? this.SelectedEvents : [.. this.Stream];
		return Ui.Save(Ui.FileName("Events"), Formats(events));
	}

	/// <summary>Toolbar Copy: what is selected, or everything the filter shows when nothing is.</summary>
	[RelayCommand]
	private Task CopyStream()
	{
		IReadOnlyList<NeuronEvent> events = this.SelectedEvents.Count > 0 ? this.SelectedEvents : [.. this.Stream];
		return Ui.Copy(ToRows(events), Ui.Count(events.Count, "event"));
	}

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		if (items.Count == 0)
			return [];

		if (items.All(i => i is NeuronEvent))
			return this.EventActions([.. items.Cast<NeuronEvent>()]);

		if (items.All(i => i is IssueItemViewModel))
			return this.IssueActions([.. items.Cast<IssueItemViewModel>()]);

		return [];
	}

	private ActionList EventActions(List<NeuronEvent> events)
	{
		ActionList list = [];
		string what = Ui.Count(events.Count, "event");

		if (events.Count == 1)
		{
			NeuronEvent e = events[0];
			list.Copy("Copy message", () => e.Message, "the message", "Ctrl+C");
			list.Copy("Copy row", () => ToRows(events, false).TrimEnd(), "the row");
			list.Copy("Copy as Markdown", () => ToMarkdown(e), "the event as Markdown", "Ctrl+Shift+C");
			if (!string.IsNullOrEmpty(e.StackTrace))
				list.Copy("Copy stack trace", () => e.StackTrace, "the stack trace");
			list.Save("Save…", Ui.FileName("Event " + Converters.NeuronNameConverter.Name(e.Source.NeuronJid), e.Timestamp), Formats(events));

			string jid = e.Source.NeuronJid;
			if (!(this.NeuronFilter.Count == 1 && this.NeuronFilter.Contains(jid)))
				list.Filter("Only this Neuron", () => this.Shell?.ShowEvents(jid));
			if (this.SelectedIssue is null && this.issueIndex.TryGetValue(e.Fingerprint, out IssueItemViewModel? issue))
				list.Filter("Only events like this", () => this.OpenIssue(issue));
			if (this.SelectedIssue is null)
				list.Filter("Hide events like this", () => this.Hide(events));

			if (this.Shell is IShell shell)
				list.Sub("Go to " + Converters.NeuronNameConverter.Name(jid), ActionGroup.GoTo, shell.NeuronPlaces(jid, e.Source.AccountId, WorkspaceKind.Events));

			list.Do("Share to chat…", () => this.ShareTo?.Invoke("Event: " + e.Title, ToMarkdown(e)), icon: "Share");
		}
		else
		{
			list.Copy("Copy " + what, () => ToRows(events), what, "Ctrl+C", inBar: true);
			list.Copy("Copy as Markdown", () => ToMarkdown(events), what + " as Markdown", "Ctrl+Shift+C");
			list.Save("Save " + what + "…", Ui.FileName("Events"), Formats(events));
			if (this.SelectedIssue is null)
				list.Filter("Hide events like these", () => this.Hide(events));
			list.Add(new ItemAction("Share to chat…", ActionGroup.Do, () => this.ShareTo?.Invoke(what, ToMarkdown(events))) { InBar = true, BarText = "Share", Icon = "Share" });
		}

		return list;
	}

	private void OpenIssue(IssueItemViewModel issue)
	{
		if (!this.Issues.Contains(issue))
			this.ShowResolved = true;

		this.SelectedIssue = issue;
	}

	private static string IssueMarkdown(Issue i)
	{
		StringBuilder sb = new();
		sb.Append("**Problem:** ").AppendLine(i.Title).AppendLine();
		sb.AppendLine("| | |");
		sb.AppendLine("|:--|:--|");
		sb.Append("| Severity | ").Append(i.Severity).AppendLine(" |");
		sb.Append("| Occurrences | ").Append(i.Count).AppendLine(" |");
		sb.Append("| First seen | ").Append(i.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine(" |");
		sb.Append("| Last seen | ").Append(i.LastSeen.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine(" |");
		sb.Append("| Neurons | ").Append(string.Join(", ", i.Neurons.Select(n => "`" + n + "`"))).AppendLine(" |");
		sb.AppendLine().AppendLine("Latest occurrence:").AppendLine().Append(ToMarkdown(i.Latest));
		return sb.ToString();
	}

	private ActionList IssueActions(List<IssueItemViewModel> issues)
	{
		ActionList list = [];
		string what = Ui.Count(issues.Count, "problem");
		bool anyOpen = issues.Any(i => i.State is IssueState.Open or IssueState.Acknowledged);
		bool anyNew = issues.Any(i => i.State == IssueState.Open);
		bool anyClosed = issues.Any(i => i.State is IssueState.Resolved or IssueState.Muted);

		void All(IssueState state) => this.SetStates(issues, state);

		if (issues.Count == 1)
		{
			IssueItemViewModel issue = issues[0];
			if (!ReferenceEquals(this.SelectedIssue, issue))
				list.Open("Show its events", () => this.SelectedIssue = issue);

			list.Copy("Copy title", () => issue.Title, "the title", "Ctrl+C");
			list.Copy("Copy summary as Markdown", () => IssueMarkdown(issue.Issue), "the summary", "Ctrl+Shift+C");
			list.Save("Save its events…", Ui.FileName("Problem " + issue.Title[..Math.Min(40, issue.Title.Length)]),
				Formats([.. this.monitors.Hub.EventsOf(issue.Fingerprint, MaxStream)]));

			if (this.Shell is IShell shell)
			{
				IReadOnlyList<string> neurons = issue.Issue.Neurons;
				if (neurons.Count == 1)
					list.Sub("Go to " + neurons[0], ActionGroup.GoTo, shell.NeuronPlaces(neurons[0], null, WorkspaceKind.Events));
				else
				{
					foreach (string n in neurons.Take(8))
						list.Sub("Go to " + n, ActionGroup.GoTo, shell.NeuronPlaces(n));
				}
			}
		}
		else
		{
			list.Copy("Copy titles", () => string.Join(Environment.NewLine, issues.Select(i => i.Title)), what, "Ctrl+C");
			list.Copy("Copy summaries as Markdown", () => string.Join(Environment.NewLine + "---" + Environment.NewLine + Environment.NewLine, issues.Select(i => IssueMarkdown(i.Issue))), what + " as Markdown", "Ctrl+Shift+C");
		}

		if (anyNew)
			list.Do("Acknowledge", () => All(IssueState.Acknowledged), inBar: true, icon: "Check");
		if (anyOpen)
		{
			list.Do("Mute", () => All(IssueState.Muted), inBar: true, icon: "BellOff");
			list.Do("Mark fixed", () => All(IssueState.Resolved), inBar: true);
		}

		if (anyClosed)
			list.Do("Reopen", () => All(IssueState.Open), inBar: !anyOpen, icon: "Undo");

		if (issues.Count == 1)
			list.Do("Share to chat…", () => this.ShareIssue(issues[0]), icon: "Share");

		return list;
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
