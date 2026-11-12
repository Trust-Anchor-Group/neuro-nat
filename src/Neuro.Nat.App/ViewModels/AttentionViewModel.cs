using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.ViewModels;

/// <summary>Which notifications the Attention list shows.</summary>
public enum AttentionFilter
{
	/// <summary>Only what still needs a person.</summary>
	Open,

	/// <summary>Everything, newest first.</summary>
	All,
}

/// <summary>One notification in the Attention list.</summary>
public sealed partial class AttentionItemViewModel(string id) : ObservableObject
{
	public string Id { get; } = id;

	[ObservableProperty] private NeuronNotification? model;
	[ObservableProperty] private string title = string.Empty;
	[ObservableProperty] private string kindLabel = string.Empty;
	[ObservableProperty] private EventSeverity severity;
	[ObservableProperty] private bool needsAction;
	[ObservableProperty] private bool isOpen;
	[ObservableProperty] private bool isUnread;
	[ObservableProperty] private bool isHandled;
	[ObservableProperty] private string neuronJid = string.Empty;
	[ObservableProperty] private string neuronLabel = string.Empty;
	[ObservableProperty] private string accountLabel = string.Empty;
	[ObservableProperty] private int accountColorIndex;
	[ObservableProperty] private string? subject;
	[ObservableProperty] private string meta = string.Empty;
	[ObservableProperty] private string timeText = string.Empty;
	[ObservableProperty] private string whenText = string.Empty;
	[ObservableProperty] private string markdown = string.Empty;
	[ObservableProperty] private IReadOnlyList<NotificationLink> links = [];
	[ObservableProperty] private bool hasLinks;

	internal void Apply(NeuronNotification n, string neuronLabel, AccountProfile? account, DateTime now)
	{
		this.Model = n;
		this.Title = n.Title;
		this.KindLabel = n.Kind.Label();
		this.Severity = n.Severity;
		this.NeedsAction = n.NeedsAction;
		this.IsOpen = n.IsOpen;
		this.IsUnread = n.State == AttentionState.Unread;
		this.IsHandled = n.State == AttentionState.Handled;
		this.NeuronJid = n.NeuronJid;
		this.NeuronLabel = neuronLabel;
		this.AccountLabel = account?.ShortLabel ?? string.Empty;
		this.AccountColorIndex = account?.ColorIndex ?? 0;
		this.Subject = n.Subject;
		this.Meta = string.IsNullOrEmpty(n.Subject) ? neuronLabel : n.Subject + " on " + neuronLabel;
		this.TimeText = RelativeTime.Format(n.Updated ?? n.Received, now);

		string when = n.Received.ToString("ddd d MMM, HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
		if (n.Updated is DateTime u)
			when += ". Corrected at " + u.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
		if (n.State == AttentionState.Handled && n.HandledAt is DateTime h)
			when += ". Handled " + RelativeTime.Format(h, now);
		this.WhenText = when;

		this.Markdown = n.Markdown;
		this.Links = n.Links;
		this.HasLinks = n.Links.Count > 0;
	}
}

/// <summary>
/// The Attention workspace: what the Neurons told their administrators, typed, with the items
/// that need a person kept apart until someone marks them handled.
/// </summary>
public sealed partial class AttentionViewModel : ObservableObject, IDisposable
{
	private readonly MonitorService monitors;
	private readonly AccountManager accounts;
	private readonly DispatcherTimer timer;
	private int dirty = 1;
	private int tick;

	public AttentionViewModel(MonitorService monitors, AccountManager accounts)
	{
		this.monitors = monitors;
		this.accounts = accounts;
		monitors.Attention.Changed += this.Inbox_Changed;
		monitors.Attention.Reset += this.Inbox_Reset;
		monitors.NeuronsChanged += this.Inbox_Reset;

		this.timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => this.Tick());
		this.timer.Start();
	}

	public ObservableCollection<AttentionItemViewModel> Items { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOpenFilter), nameof(IsAllFilter))]
	private AttentionFilter filter = AttentionFilter.Open;

	public bool IsOpenFilter => this.Filter == AttentionFilter.Open;
	public bool IsAllFilter => this.Filter == AttentionFilter.All;

	[ObservableProperty] private string searchText = string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelection))]
	private AttentionItemViewModel? selectedItem;

	public bool HasSelection => this.SelectedItem is not null;

	/// <summary>Items that still need a person, for the rail badge.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasOpen), nameof(OpenBadge))]
	private int openCount;

	public bool HasOpen => this.OpenCount > 0;
	public string OpenBadge => this.OpenCount > 99 ? "99+" : this.OpenCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

	[ObservableProperty] private int unreadCount;
	[ObservableProperty] private int totalCount;
	[ObservableProperty] private string summary = string.Empty;
	[ObservableProperty] private bool hasItems;

	/// <summary>Set by the shell: opens the Events workspace filtered to one Neuron.</summary>
	public Action<string>? ShowEventsFor { get; set; }

	/// <summary>Set by the shell: sends a title and Markdown to Chat to pick who gets it.</summary>
	public Action<string, string>? ShareTo { get; set; }

	[RelayCommand]
	private void Share(AttentionItemViewModel? item)
	{
		item ??= this.SelectedItem;
		if (item is not null)
			this.ShareTo?.Invoke(item.KindLabel + ": " + item.Title, $"**{item.Title}** ({item.NeuronLabel}, {item.WhenText})\n\n{item.Markdown}");
	}

	/// <summary>Set by the view: puts text on the clipboard.</summary>
	public Func<string, Task>? CopyToClipboard { get; set; }

	/// <summary>Set by the view: opens a link in the browser.</summary>
	public Func<string, Task>? OpenUrl { get; set; }

	partial void OnFilterChanged(AttentionFilter value) => this.Refresh();
	partial void OnSearchTextChanged(string value) => this.Refresh();

	partial void OnSelectedItemChanged(AttentionItemViewModel? value)
	{
		if (value is not null && value.IsUnread)
			this.monitors.Attention.SetState(value.Id, AttentionState.Read);
	}

	private void Inbox_Changed(object? sender, NeuronNotification e) => Interlocked.Exchange(ref this.dirty, 1);
	private void Inbox_Reset(object? sender, EventArgs e) => Interlocked.Exchange(ref this.dirty, 1);

	private void Tick()
	{
		// Rebuild when something changed, and every 30 s so relative times stay fresh.
		if (Interlocked.Exchange(ref this.dirty, 0) == 1 || ++this.tick % 75 == 0)
			this.Refresh();
	}

	/// <summary>Rebuilds the list from the inbox. UI thread.</summary>
	public void Refresh()
	{
		DateTime now = DateTime.Now;
		IReadOnlyList<NeuronNotification> all = this.monitors.Attention.Items;
		IReadOnlyList<WatchedNeuron> known = this.monitors.Neurons;
		IReadOnlyDictionary<string, string> names = NeuronNames.Distinct(known);
		Dictionary<(Guid, string), string> labels = known
			.GroupBy(n => (n.AccountId, n.BareJid.ToLowerInvariant()))
			.ToDictionary(g => g.Key, g => names[g.First().BareJid]);
		Dictionary<Guid, AccountProfile> profiles = this.accounts.Sessions.ToDictionary(s => s.Profile.Id, s => s.Profile);
		string search = this.SearchText.Trim();

		IEnumerable<NeuronNotification> shown = all;
		if (this.Filter == AttentionFilter.Open)
			shown = shown.Where(n => n.IsOpen);

		List<AttentionItemViewModel> keep = [];
		foreach (NeuronNotification n in shown)
		{
			string label = labels.TryGetValue((n.AccountId, n.NeuronJid.ToLowerInvariant()), out string? l) ? l : NeuronNames.For(known, n.NeuronJid);

			if (search.Length > 0 &&
				!n.Title.Contains(search, StringComparison.OrdinalIgnoreCase) &&
				!n.Markdown.Contains(search, StringComparison.OrdinalIgnoreCase) &&
				!label.Contains(search, StringComparison.OrdinalIgnoreCase) &&
				!n.Kind.Label().Contains(search, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			AttentionItemViewModel vm = this.Items.FirstOrDefault(i => i.Id == n.Id) ?? new AttentionItemViewModel(n.Id);
			vm.Apply(n, label, profiles.TryGetValue(n.AccountId, out AccountProfile? p) ? p : null, now);
			keep.Add(vm);
		}

		AttentionItemViewModel? selected = this.SelectedItem;
		int selectedIndex = selected is null ? -1 : this.Items.IndexOf(selected);

		for (int i = this.Items.Count - 1; i >= 0; i--)
		{
			if (!keep.Contains(this.Items[i]))
				this.Items.RemoveAt(i);
		}

		for (int i = 0; i < keep.Count; i++)
		{
			int existing = this.Items.IndexOf(keep[i]);
			if (existing < 0)
				this.Items.Insert(i, keep[i]);
			else if (existing != i)
				this.Items.Move(existing, i);
		}

		// When the selected item left the list (handled under "Needs you"), move on to the next one.
		if (selected is not null && !keep.Contains(selected))
			this.SelectedItem = keep.Count == 0 ? null : keep[Math.Clamp(selectedIndex, 0, keep.Count - 1)];

		this.OpenCount = all.Count(n => n.IsOpen);
		this.UnreadCount = all.Count(n => n.State == AttentionState.Unread);
		this.TotalCount = all.Count;
		this.HasItems = keep.Count > 0;
		this.Summary = this.OpenCount == 0
			? (all.Count == 0 ? "Nothing yet" : "Nothing needs you")
			: $"{this.OpenCount} need{(this.OpenCount == 1 ? "s" : "")} you";
	}

	[RelayCommand]
	private void SetFilter(AttentionFilter f) => this.Filter = f;

	[RelayCommand]
	private void MarkHandled(AttentionItemViewModel? item)
	{
		item ??= this.SelectedItem;
		if (item is not null)
		{
			this.monitors.Attention.SetState(item.Id, AttentionState.Handled);
			this.Refresh();
		}
	}

	[RelayCommand]
	private void Reopen(AttentionItemViewModel? item)
	{
		item ??= this.SelectedItem;
		if (item is not null)
		{
			this.monitors.Attention.SetState(item.Id, AttentionState.Read);
			this.Refresh();
		}
	}

	[RelayCommand]
	private void MarkAllRead()
	{
		this.monitors.Attention.MarkAllRead();
		this.Refresh();
	}

	[RelayCommand]
	private void ClearDone()
	{
		this.monitors.Attention.ClearDone();
		this.Refresh();
	}

	[RelayCommand]
	private void ShowEvents(AttentionItemViewModel? item)
	{
		item ??= this.SelectedItem;
		if (item is not null)
			this.ShowEventsFor?.Invoke(item.NeuronJid);
	}

	[RelayCommand]
	private async Task OpenLink(NotificationLink? link)
	{
		if (link is not null && this.OpenUrl is not null)
			await this.OpenUrl(link.Url);
	}

	[RelayCommand]
	private async Task Copy(AttentionItemViewModel? item)
	{
		item ??= this.SelectedItem;
		if (item is not null && this.CopyToClipboard is not null)
			await this.CopyToClipboard($"**{item.Title}** ({item.NeuronLabel}, {item.WhenText})\n\n{item.Markdown}");
	}

	public void Dispose()
	{
		this.timer.Stop();
		this.monitors.Attention.Changed -= this.Inbox_Changed;
		this.monitors.Attention.Reset -= this.Inbox_Reset;
		this.monitors.NeuronsChanged -= this.Inbox_Reset;
	}
}
