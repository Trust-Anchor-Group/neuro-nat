using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.PubSub;

namespace Neuro.AA.App.ViewModels;

/// <summary>
/// An item on the open node. The kinds the app knows are shown in words, with the XML one click
/// away; everything else is its XML, folded when it is long.
/// </summary>
public sealed partial class PubSubItemRow : ObservableObject
{
	private const int FoldLines = 12;
	private const int FoldChars = 1200;

	private readonly int lines;

	public PubSubItemRow(PubSubItemInfo item)
	{
		this.Item = item;
		this.Reading = Readable.From(item.PayloadXml);
		this.PrettyXml = Readable.Pretty(item.PayloadXml);
		this.lines = this.PrettyXml.Count(c => c == '\n') + 1;
		this.isXmlOpen = this.Reading is null;

		if (this.Reading?.Picture is byte[] picture)
		{
			try
			{
				using MemoryStream stream = new(picture);
				this.Picture = new Bitmap(stream);
			}
			catch (Exception)
			{
				// Not a picture this computer can show: the words and the XML still say what it is.
			}
		}
	}

	public PubSubItemInfo Item { get; }
	public Readable? Reading { get; }
	public string PrettyXml { get; }
	public Bitmap? Picture { get; }

	public string Title => this.Item.ItemId;

	public string Meta => string.Join(" · ", new[]
	{
		this.Item.Publisher is null ? null : "by " + this.Item.Publisher,
		this.Item.Published is DateTime t ? (t.Date == DateTime.Today ? t.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)) : null,
	}.Where(s => s is not null));

	public bool IsReadable => this.Reading is not null;
	public string Kind => this.Reading?.Kind ?? string.Empty;
	public string? Summary => this.Reading?.Summary;
	public bool HasSummary => !string.IsNullOrEmpty(this.Reading?.Summary);
	public IReadOnlyList<ReadableField> Fields => this.Reading?.Fields ?? [];
	public bool HasFields => this.Fields.Count > 0;
	public bool HasPicture => this.Picture is not null;

	/// <summary>The XML shows. Always, for a kind the app has no words for.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(XmlToggleText))]
	private bool isXmlOpen;

	public string XmlToggleText => this.IsXmlOpen ? "Hide XML" : "Show XML";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShownXml), nameof(CanUnfold))]
	private bool isUnfolded;

	private bool IsLong => this.lines > FoldLines || this.PrettyXml.Length > FoldChars;

	public bool CanUnfold => this.IsLong && !this.IsUnfolded;

	public string FoldText => this.lines > FoldLines ? $"Show all {this.lines} lines" : "Show all of it";

	public string ShownXml
	{
		get
		{
			if (!this.IsLong || this.IsUnfolded)
				return this.PrettyXml;

			string cut = string.Join('\n', this.PrettyXml.Split('\n').Take(FoldLines));
			return (cut.Length > FoldChars ? cut[..FoldChars] : cut).TrimEnd() + " …";
		}
	}

	/// <summary>It came while you were looking.</summary>
	[ObservableProperty] private bool isNew;

	/// <summary>It was removed, and is on its way out of the list.</summary>
	[ObservableProperty] private bool isGone;

	[RelayCommand]
	private void ToggleXml() => this.IsXmlOpen = !this.IsXmlOpen;

	[RelayCommand]
	private void Unfold() => this.IsUnfolded = true;

	/// <summary>What the item says, as text: the words when there are any, otherwise the XML.</summary>
	public string Text
	{
		get
		{
			if (this.Reading is not Readable r)
				return this.PrettyXml;

			IEnumerable<string> parts = new[] { r.Summary }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)
				.Concat(r.Fields.Select(f => f.Label + ": " + f.Value));
			return string.Join(Environment.NewLine, parts);
		}
	}

	internal bool Matches(string filter)
		=> this.Item.ItemId.Contains(filter, StringComparison.OrdinalIgnoreCase)
			|| (this.Item.Publisher?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
			|| this.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase)
			|| this.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)
			|| (!this.HasPicture && this.Item.PayloadXml.Contains(filter, StringComparison.OrdinalIgnoreCase));
}

// Items: reading them, what arrives while watching, removing with a way back, and the editor.
public sealed partial class PubSubViewModel
{
	/// <summary>How many items a node shows at first, and how many more each "Load more" adds.</summary>
	public const int PageSize = 50;

	/// <summary>How long "Removed. Undo" stays.</summary>
	public static TimeSpan UndoTime { get; set; } = TimeSpan.FromSeconds(8);

	private readonly List<PubSubItemRow> allItems = [];
	private readonly Dictionary<string, (string Id, string Xml)> lastPublished = [];
	private int shownItems = PageSize;
	private bool haveAll;
	private int itemsRun;

	/// <summary>The items that show: the newest first, narrowed by the filter.</summary>
	public ObservableCollection<PubSubItemRow> Items { get; } = [];

	[ObservableProperty] private bool hasItems;
	[ObservableProperty] private bool isItemsLoading;

	/// <summary>Why the items could not be read, or an item could not be removed.</summary>
	[ObservableProperty] private string? itemsError;

	/// <summary>What is typed above the items: only items that have it show.</summary>
	[ObservableProperty] private string itemFilter = string.Empty;

	[ObservableProperty] private bool hasMore;
	[ObservableProperty] private string itemsFooter = string.Empty;
	[ObservableProperty] private string itemsEmptyText = string.Empty;

	partial void OnItemFilterChanged(string value) => this.ShowItems();

	private async Task LoadItemsAsync()
	{
		int run = ++this.itemsRun;
		this.allItems.Clear();
		this.Items.Clear();
		this.HasItems = false;
		this.HasMore = false;
		this.haveAll = false;
		this.ItemsError = null;
		this.ItemsFooter = string.Empty;
		this.ItemsEmptyText = string.Empty;
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return;

		if (n.IsCollection)
		{
			this.ItemsEmptyText = "A collection holds other nodes, and no items of its own.";
			return;
		}

		this.IsItemsLoading = true;
		this.IsBusy = true;
		try
		{
			// The newest first; all of them only when asked for.
			IReadOnlyList<PubSubItemInfo> items = await this.pubsub.ItemsAsync(a.Id, n.Info, PageSize);
			if (run != this.itemsRun)
				return;

			this.haveAll = items.Count < PageSize;
			this.allItems.AddRange(items.Select(i => new PubSubItemRow(i)));
			this.ShowItems();
		}
		catch (Exception ex)
		{
			if (run == this.itemsRun)
				this.ItemsError = Words(ex);
		}
		finally
		{
			if (run == this.itemsRun)
			{
				this.IsItemsLoading = false;
				this.IsBusy = false;
			}
		}
	}

	private void ShowItems()
	{
		string filter = this.ItemFilter.Trim();
		List<PubSubItemRow> matching = filter.Length == 0 ? this.allItems : [.. this.allItems.Where(i => i.Matches(filter))];
		List<PubSubItemRow> shown = [.. matching.Take(this.shownItems)];
		Reconcile(this.Items, shown);

		this.HasItems = this.Items.Count > 0;
		this.HasMore = matching.Count > shown.Count || !this.haveAll;
		this.ItemsEmptyText = this.HasItems || this.ItemsError is not null ? string.Empty
			: this.allItems.Count == 0 ? "Nothing is published on this node."
			: "No item matches. The filter looks at ids, who published, and what the items say.";

		string count = Ui.Count(shown.Count, "item");
		this.ItemsFooter = !this.HasItems ? string.Empty
			: !this.haveAll ? $"The latest {count}. There may be older ones."
			: filter.Length > 0 ? $"{count} of {this.allItems.Count:N0} match"
			: matching.Count > shown.Count ? $"{count} of {matching.Count:N0}"
			: count;
	}

	/// <summary>Shows a page more. The first time, the rest of the node is read from the service.</summary>
	[RelayCommand]
	private async Task LoadMore()
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return;

		if (!this.haveAll)
		{
			int run = this.itemsRun;
			this.IsItemsLoading = true;
			try
			{
				IReadOnlyList<PubSubItemInfo> items = await this.pubsub.ItemsAsync(a.Id, n.Info);
				if (run != this.itemsRun)
					return;

				// What is already showing keeps its row: what was opened stays open.
				Dictionary<string, PubSubItemRow> have = this.allItems.ToDictionary(i => i.Item.ItemId);
				List<PubSubItemRow> all = [.. this.allItems];
				all.AddRange(items.Where(i => !have.ContainsKey(i.ItemId)).Select(i => new PubSubItemRow(i)));
				this.allItems.Clear();
				this.allItems.AddRange(all);
				this.haveAll = true;
			}
			catch (Exception ex)
			{
				this.ItemsError = Words(ex);
				return;
			}
			finally
			{
				this.IsItemsLoading = false;
			}
		}

		this.shownItems += PageSize;
		this.ShowItems();
	}

	/// <summary>What happened on the open node while it is watched.</summary>
	private void Apply(NodeItemEvent e)
	{
		if (e.Cleared)
		{
			this.allItems.Clear();
			this.ShowItems();
			return;
		}

		if (e.Item is PubSubItemInfo item)
		{
			this.allItems.RemoveAll(i => i.Item.ItemId == item.ItemId);
			this.allItems.Insert(0, new PubSubItemRow(item) { IsNew = true });
			this.ShowItems();
		}
		else if (e.RemovedItemId is string id && this.allItems.FirstOrDefault(i => i.Item.ItemId == id) is PubSubItemRow gone)
			this.Drop(gone);
	}

	/// <summary>How long a removed item takes to fade out of the list.</summary>
	public static TimeSpan FadeTime { get; set; } = TimeSpan.FromMilliseconds(350);

	private void Drop(PubSubItemRow row)
	{
		if (FadeTime <= TimeSpan.Zero)
		{
			this.allItems.Remove(row);
			this.ShowItems();
			return;
		}

		row.IsGone = true;
		DispatcherTimer.RunOnce(() =>
		{
			if (this.allItems.Remove(row))
				this.ShowItems();
		}, FadeTime);
	}

	// ----- Removing, with a way back -----

	private List<PubSubItemInfo>? removed;
	private PubSubNodeRow? removedFrom;
	private DispatcherTimer? undoTimer;

	/// <summary>"Removed 3 items." while they can still be put back.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasUndo))]
	private string? undoText;

	public bool HasUndo => this.UndoText is not null;

	private void ForgetUndo()
	{
		this.undoTimer?.Stop();
		this.removed = null;
		this.removedFrom = null;
		this.UndoText = null;
	}

	/// <summary>Removes items from the node. They can be put back, under the same ids, for a few seconds.</summary>
	[RelayCommand]
	private Task Retract(PubSubItemRow? row) => row is null ? Task.CompletedTask : this.RemoveAsync([row]);

	private async Task RemoveAsync(IReadOnlyList<PubSubItemRow> rows)
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n || rows.Count == 0)
			return;

		this.ForgetUndo();
		this.ItemsError = null;
		List<PubSubItemInfo> done = [];
		foreach (PubSubItemRow row in rows)
		{
			try
			{
				await this.pubsub.RetractAsync(a.Id, n.Info, row.Item.ItemId);
				done.Add(row.Item);
				this.allItems.Remove(row);
			}
			catch (Exception ex)
			{
				this.ItemsError = Words(ex);
				break;
			}
		}

		this.ShowItems();
		if (done.Count == 0)
			return;

		this.removed = done;
		this.removedFrom = n;
		this.UndoText = done.Count == 1 ? "Removed " + done[0].ItemId + "." : $"Removed {done.Count} items.";
		this.undoTimer ??= new DispatcherTimer(UndoTime, DispatcherPriority.Background, (_, _) => this.ForgetUndo());
		this.undoTimer.Interval = UndoTime;
		this.undoTimer.Stop();
		this.undoTimer.Start();
	}

	/// <summary>Publishes what was just removed again, under the ids it had.</summary>
	[RelayCommand]
	private async Task Undo()
	{
		if (this.Account is not { } a || this.removed is not { } items || this.removedFrom is not { } n)
			return;

		this.ForgetUndo();
		try
		{
			// The oldest first, so they end up in the order they were in.
			foreach (PubSubItemInfo i in Enumerable.Reverse(items))
				await this.pubsub.PublishAsync(a.Id, n.Info, i.ItemId, i.PayloadXml);

			Ui.Say(items.Count == 1 ? "Put " + items[0].ItemId + " back" : $"Put {items.Count} items back");
		}
		catch (Exception ex)
		{
			this.ItemsError = "Not everything could be put back. " + Words(ex);
		}

		if (ReferenceEquals(n, this.SelectedNode))
			await this.LoadItemsAsync();
	}

	// ----- The editor -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowsPublish))]
	private bool isEditorOpen;

	/// <summary>The Publish button shows: the node takes items and the editor is not open already.</summary>
	public bool ShowsPublish => this.CanPublishHere && !this.IsEditorOpen;
	[ObservableProperty] private string editorTitle = "Publish an item";
	[ObservableProperty] private string publishItemId = string.Empty;

	/// <summary>The XML to publish. Checked as it is typed.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(EditorProblem), nameof(HasEditorProblem), nameof(CanPublish), nameof(ProblemLine), nameof(ProblemPlace))]
	private string publishXml = string.Empty;

	/// <summary>Why it could not be sent.</summary>
	[ObservableProperty] private string? editorError;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanPublish))]
	private bool isPublishing;

	/// <summary>What is wrong with the XML as it stands, with the line it goes wrong on.</summary>
	public string? EditorProblem => Readable.Problem(this.PublishXml)?.Text;
	public bool HasEditorProblem => !string.IsNullOrWhiteSpace(this.PublishXml) && this.EditorProblem is not null;
	public int ProblemLine => Readable.Problem(this.PublishXml)?.Line ?? 0;
	public int ProblemPlace => Readable.Problem(this.PublishXml)?.Position ?? 0;
	public bool CanPublish => !this.IsPublishing && this.EditorProblem is null;

	/// <summary>The open node takes items: it is not a collection.</summary>
	public bool CanPublishHere => this.SelectedNode is { IsCollection: false };

	private string? NodeKey => this.Account is { } a && this.SelectedNode is { } n ? a.Id.ToString("N") + "|" + n.Info.Service + "|" + n.Node : null;

	/// <summary>Something was published on this node from here earlier: the editor can start from it.</summary>
	public bool HasLast => this.NodeKey is string k && this.lastPublished.ContainsKey(k);

	/// <summary>Raised when the editor opens, so the view can put the keyboard in it.</summary>
	public event EventHandler? EditorOpened;

	private void OpenEditor(string title, string id, string xml)
	{
		this.EditorTitle = title;
		this.PublishItemId = id;
		this.PublishXml = xml;
		this.EditorError = null;
		this.IsEditorOpen = true;
		this.OnPropertyChanged(nameof(this.HasLast));
		this.EditorOpened?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Opens the editor empty.</summary>
	[RelayCommand]
	private void OpenEditor()
	{
		if (this.CanPublishHere)
			this.OpenEditor("Publish an item on " + this.NodeTitle, string.Empty, string.Empty);
	}

	/// <summary>Fills the editor with what was last published on this node from here.</summary>
	[RelayCommand]
	private void UseLast()
	{
		if (this.NodeKey is string k && this.lastPublished.TryGetValue(k, out (string Id, string Xml) last))
		{
			this.PublishXml = last.Xml;
			this.EditorError = null;
			this.EditorOpened?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>Lays the XML in the editor out for reading, when it is XML.</summary>
	[RelayCommand]
	private void Tidy()
	{
		if (Readable.Problem(this.PublishXml) is null)
			this.PublishXml = Readable.Pretty(this.PublishXml);
	}

	[RelayCommand]
	private void CloseEditor() => this.IsEditorOpen = false;

	[RelayCommand]
	private async Task Publish()
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return;

		if (Readable.Problem(this.PublishXml) is { } problem)
		{
			this.EditorError = "This is not XML that can be published. " + problem.Text;
			return;
		}

		this.IsPublishing = true;
		this.EditorError = null;
		try
		{
			string xml = this.PublishXml.Trim();
			string id = await this.pubsub.PublishAsync(a.Id, n.Info, string.IsNullOrWhiteSpace(this.PublishItemId) ? null : this.PublishItemId.Trim(), xml);
			if (this.NodeKey is string k)
				this.lastPublished[k] = (id, xml);

			this.PublishItemId = string.Empty;
			this.IsEditorOpen = false;
			Ui.Say("Published " + id);
			await this.LoadItemsAsync();
			if (this.allItems.FirstOrDefault(i => i.Item.ItemId == id) is PubSubItemRow row)
				row.IsNew = true;
		}
		catch (Exception ex)
		{
			this.EditorError = Words(ex);
		}
		finally
		{
			this.IsPublishing = false;
			this.OnPropertyChanged(nameof(this.HasLast));
		}
	}

	// ----- What can be done to items -----

	private void ItemActions(ActionList list, List<PubSubItemRow> rows)
	{
		if (rows.Count == 1)
		{
			PubSubItemRow row = rows[0];
			list.Copy(row.IsReadable ? "Copy what it says" : "Copy", () => row.Text, "the item", "Ctrl+C");
			if (row.IsReadable)
				list.Copy("Copy XML", () => row.PrettyXml, "the XML");
			list.Copy("Copy id", () => row.Item.ItemId, "the id");
			if (row.Item.Publisher is string who)
				list.Copy("Copy who published", () => who, "the address");

			list.Do("Publish a copy…", () => this.OpenEditor("Publish a copy on " + this.NodeTitle, string.Empty, row.PrettyXml), icon: "Copy");
			list.Do("Edit and publish again…", () => this.OpenEditor("Publish " + row.Item.ItemId + " again", row.Item.ItemId, row.PrettyXml), icon: "Edit");
			list.Do("Remove", () => this.RemoveAsync(rows), danger: true, key: "Delete", icon: "Trash");
			return;
		}

		list.Add(new ItemAction("Copy XML", ActionGroup.Copy, () => Ui.Copy(string.Join(Environment.NewLine + Environment.NewLine, rows.Select(r => r.PrettyXml)), Ui.Count(rows.Count, "item")))
		{
			Key = "Ctrl+C",
			InBar = true,
			BarText = "Copy",
			Icon = "Copy",
		});
		list.Copy("Copy ids", () => string.Join(Environment.NewLine, rows.Select(r => r.Item.ItemId)), Ui.Count(rows.Count, "id"));
		list.Do("Remove", () => this.RemoveAsync(rows), inBar: true, danger: true, key: "Delete", icon: "Trash");
	}
}
