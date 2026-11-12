using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.PubSub;

namespace Neuro.Nat.App.ViewModels;

/// <summary>A node in the tree: a place things are published, or a collection that holds other nodes.</summary>
public sealed partial class PubSubNodeRow(PubSubNodeInfo info, int depth) : ObservableObject
{
	public PubSubNodeInfo Info { get; } = info;
	public int Depth { get; } = depth;
	public string Node => this.Info.Node;
	public string? Name => this.Info.Name;

	/// <summary>What the node is called in the tree: a name people know for the usual personal nodes, otherwise its own.</summary>
	public string Label => Known(this.Info.Node) ?? this.Info.Node;

	/// <summary>The line under it: the node's title, or its real name where the label is a friendlier one.</summary>
	public string? Sub => Known(this.Info.Node) is not null ? this.Info.Node : string.IsNullOrWhiteSpace(this.Info.Name) ? null : this.Info.Name;

	public bool HasSub => this.Sub is not null;

	/// <summary>The nodes apps keep in personal storage, in words.</summary>
	internal static string? Known(string node) => node switch
	{
		"urn:xmpp:avatar:data" => "Avatar picture",
		"urn:xmpp:avatar:metadata" => "Avatar details",
		"http://jabber.org/protocol/nick" => "Nickname",
		"http://jabber.org/protocol/mood" => "Mood",
		"http://jabber.org/protocol/tune" => "Tune",
		"http://jabber.org/protocol/activity" => "Activity",
		"http://jabber.org/protocol/geoloc" => "Location",
		"urn:xmpp:bookmarks:1" or "urn:xmpp:bookmarks:0" or "storage:bookmarks" => "Room bookmarks",
		"urn:xmpp:vcard4" => "Contact card",
		"urn:xmpp:microblog:0" => "Posts",
		"eu.siacs.conversations.axolotl.devicelist" or "urn:xmpp:omemo:1:devices" or "urn:xmpp:omemo:2:devices" => "Encryption devices",
		_ when node.StartsWith("eu.siacs.conversations.axolotl.bundles", StringComparison.Ordinal) || node.StartsWith("urn:xmpp:omemo:", StringComparison.Ordinal) => "Encryption keys",
		_ => null,
	};

	public bool IsCollection => this.Info.IsCollection;
	public bool IsLeaf => !this.Info.IsCollection;
	public Thickness Indent => new(this.Depth * 16, 0, 0, 0);

	/// <summary>The nodes inside a collection, once it has been opened. Null before that.</summary>
	public List<PubSubNodeRow>? Children { get; set; }

	[ObservableProperty] private bool isExpanded;

	/// <summary>The account subscribes to this node: what is published arrives while you look.</summary>
	[ObservableProperty] private bool isWatched;

	/// <summary>How many items came while another node was open.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNew), nameof(NewText))]
	private int newCount;

	public bool HasNew => this.NewCount > 0;
	public string NewText => this.NewCount > 99 ? "99+" : this.NewCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>The three sides of a node.</summary>
public enum PubSubTab
{
	Items,
	Settings,
	People,
}

/// <summary>
/// Publish/subscribe for one account: the nodes of the service on its Neuron, or the account's
/// own personal storage. Nodes are a tree; a node shows its items as they are published, takes
/// new ones through an editor, and has its settings and its people on tabs of their own.
/// </summary>
public sealed partial class PubSubViewModel : ObservableObject, IActionSource
{
	private readonly PubSubService pubsub;
	private readonly AccountManager accounts;
	private readonly List<PubSubNodeRow> roots = [];
	private readonly List<PubSubNodeRow> strays = [];
	private readonly HashSet<string> watched = new(StringComparer.Ordinal);
	private bool loading;
	private bool looking;
	private int nodesRun;

	public PubSubViewModel(PubSubService pubsub, AccountManager accounts)
	{
		this.pubsub = pubsub;
		this.accounts = accounts;
		this.Settings = new NodeSettingsForm();
		this.NewSettings = new NodeSettingsForm();
		this.Settings.PropertyChanged += (_, _) => this.SettingsNote = null;
		this.newPersonRole = RoleChoices[0];

		pubsub.ItemEvent += (_, e) => OnUi(() => this.Happened(e));
		pubsub.Changed += (_, id) => OnUi(() =>
		{
			if (this.Account?.Id == id && this.roots.Count == 0 && !this.IsBusy)
				_ = this.LoadNodesAsync();
		});
	}

	private static void OnUi(Action act)
	{
		if (Dispatcher.UIThread.CheckAccess())
			act();
		else
			Dispatcher.UIThread.Post(act);
	}

	public ObservableCollection<DeviceAccountChoice> AccountChoices { get; } = [];

	/// <summary>The rows of the tree that show: collections that are open show what is inside them.</summary>
	public ObservableCollection<PubSubNodeRow> Nodes { get; } = [];

	[ObservableProperty] private DeviceAccountChoice? account;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsService), nameof(Title), nameof(NodesEmptyText))]
	private bool isPersonal;

	public bool IsService => !this.IsPersonal;

	/// <summary>What the list of nodes is called where it is shown.</summary>
	public string Title => !this.IsPinned ? "Publish/subscribe" : this.IsPersonal ? "Storage" : "Nodes";

	/// <summary>True on an account's or a Neuron's page, where the account and the kind of nodes are already chosen.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Title))]
	private bool isPinned;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNode), nameof(IsItems), nameof(IsSettings), nameof(IsPeople), nameof(ShowsPicker), nameof(NodeTitle), nameof(NodeMeta), nameof(CanPublishHere), nameof(ShowsPublish), nameof(HasLast))]
	private PubSubNodeRow? selectedNode;

	public bool HasNode => this.SelectedNode is not null;
	public string NodeTitle => this.SelectedNode?.Label ?? string.Empty;

	public string NodeMeta => this.SelectedNode is not { } n ? string.Empty
		: string.Join(" · ", new[] { n.Sub, n.IsCollection ? "A collection: it holds other nodes" : null, n.Info.IsPersonal ? "In your personal storage" : "On " + n.Info.Service }.Where(s => !string.IsNullOrWhiteSpace(s)));

	/// <summary>Nothing is open on the right: no node, and no new node being made.</summary>
	public bool ShowsPicker => !this.HasNode && !this.IsCreating;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(NodesEmptyText))]
	private bool hasNodes;

	[ObservableProperty] private bool isBusy;

	/// <summary>Why the nodes could not be listed, or a node could not be made.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(NodesEmptyText))]
	private string? error;

	[ObservableProperty] private string serviceText = string.Empty;

	public string NodesEmptyText => this.Error is not null ? string.Empty
		: this.looking ? "Looking for the publish/subscribe service…"
		: this.roots.Count == 0 ? this.IsPersonal ? "Nothing is stored here yet. Apps put your picture, your nickname and the like here." : "No nodes here yet. Make one with New node."
		: this.WatchingOnly ? "You watch no nodes. Open a node and switch Watch on, and what is published shows up as it happens."
		: "No node matches.";

	partial void OnAccountChanged(DeviceAccountChoice? value)
	{
		if (!this.loading)
			_ = this.LoadNodesAsync();
	}

	partial void OnIsPersonalChanged(bool value)
	{
		if (!this.loading)
			_ = this.LoadNodesAsync();
	}

	/// <summary>
	/// Shows one account's nodes: its personal storage (on the account's page) or the nodes of the
	/// publish/subscribe service on its Neuron (on the Neuron's page).
	/// </summary>
	public void Show(Guid accountId, bool personal)
	{
		this.IsPinned = true;
		this.Sync();
		DeviceAccountChoice? choice = this.AccountChoices.FirstOrDefault(c => c.Id == accountId);
		if (!ReferenceEquals(choice, this.Account) || this.IsPersonal != personal)
		{
			this.loading = true;
			try
			{
				this.Account = choice;
				this.IsPersonal = personal;
			}
			finally
			{
				this.loading = false;
			}

			_ = this.LoadNodesAsync();
		}
		else if (this.roots.Count == 0 && !this.IsBusy)
			_ = this.LoadNodesAsync();
	}

	private void Sync()
	{
		foreach (AccountSession s in this.accounts.Sessions)
		{
			if (!this.AccountChoices.Any(c => c.Id == s.Profile.Id))
				this.AccountChoices.Add(new DeviceAccountChoice(s.Profile.Id, s.Profile.Label));
		}
	}

	/// <summary>Called when the workspace opens.</summary>
	public void Activate()
	{
		this.Sync();
		if (this.Account is null)
			this.Account = this.AccountChoices.FirstOrDefault(c => this.pubsub.IsAvailable(c.Id)) ?? this.AccountChoices.FirstOrDefault();
		else if (this.roots.Count == 0 && !this.IsBusy)
			_ = this.LoadNodesAsync();
	}

	/// <summary>Set by the shell: the moves to other screens.</summary>
	public IShell? Shell { get; set; }

	private string? wantedNode;
	private PubSubTab wantedTab;

	/// <summary>Opens a node by its name, as soon as the nodes are read. From the Inbox and from search.</summary>
	public void Go(string node, PubSubTab tab = PubSubTab.Items)
	{
		this.wantedNode = node;
		this.wantedTab = tab;
		this.TryGo();
	}

	private void TryGo()
	{
		if (this.wantedNode is not string node || this.IsBusy || this.roots.Count == 0)
			return;

		this.wantedNode = null;
		if (this.Loaded().FirstOrDefault(r => r.Node == node) is not PubSubNodeRow row)
		{
			Ui.Say(node + " is not among the nodes that show here. It may be inside a collection that is closed.");
			return;
		}

		this.WatchingOnly = false;
		this.NodeFilter = string.Empty;
		this.SelectedNode = row;
		this.Tab = this.wantedTab;
	}

	/// <summary>"Chat with them", for an address on a node: who published, who has a role, who subscribes.</summary>
	private void ChatWith(ActionList list, string jid)
	{
		if (this.Shell is IShell shell && this.Account is { } a && !string.Equals(jid, this.Me, StringComparison.OrdinalIgnoreCase) && jid.Contains('@'))
			list.GoTo("Chat with them", () => shell.ShowChat(a.Id, jid), "Chat");
	}

	/// <summary>The account's own address, to tell "you" apart on a node's lists.</summary>
	private string? Me => this.Account is { } a && this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == a.Id) is AccountSession s
		? s.BareJid ?? s.Profile.UserName + "@" + s.Profile.EffectiveDomain
		: null;

	// ----- The tree -----

	/// <summary>What is typed above the tree: only nodes whose name or title has it show.</summary>
	[ObservableProperty] private string nodeFilter = string.Empty;

	/// <summary>Only the nodes you watch.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(NodesEmptyText))]
	private bool watchingOnly;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasWatched), nameof(WatchingText))]
	private int watchedCount;

	public bool HasWatched => this.WatchedCount > 0;
	public string WatchingText => "Watching " + this.WatchedCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

	partial void OnNodeFilterChanged(string value) => this.ShowTree();

	partial void OnWatchingOnlyChanged(bool value) => this.ShowTree();

	private async Task LoadNodesAsync()
	{
		int run = ++this.nodesRun;
		this.roots.Clear();
		this.strays.Clear();
		this.watched.Clear();
		this.Nodes.Clear();
		this.HasNodes = false;
		this.WatchedCount = 0;
		this.SelectedNode = null;
		this.IsCreating = false;
		this.Error = null;
		if (this.Account is not { } a)
			return;

		this.ServiceText = this.IsPersonal ? "Personal storage of " + a.Label : this.pubsub.Service(a.Id) is string svc ? "Service " + svc : this.pubsub.Problem(a.Id) ?? "Looking for the service…";
		// Personal storage is the account's own, so it is there without a service. Everything else waits for one.
		bool connected = this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == a.Id)?.Status == ConnectionStatus.Connected;
		this.looking = false;
		if (!this.pubsub.IsAvailable(a.Id) && !(this.IsPersonal && connected))
		{
			this.Error = this.pubsub.Problem(a.Id) ?? (connected ? null : "The account is not connected. Connect it to see what is here.");
			this.looking = this.Error is null;
			this.OnPropertyChanged(nameof(this.NodesEmptyText));
			return;
		}

		this.IsBusy = true;
		try
		{
			bool personal = this.IsPersonal;
			IReadOnlyList<PubSubNodeInfo> nodes = personal ? await this.pubsub.PersonalNodesAsync(a.Id) : await this.pubsub.NodesAsync(a.Id);
			IReadOnlyList<string> watching = [];
			try
			{
				watching = await this.pubsub.WatchedAsync(a.Id, personal);
			}
			catch (Exception)
			{
				// A service that does not list subscriptions still lists its nodes.
			}

			if (run != this.nodesRun)
				return;

			foreach (string w in watching)
				this.watched.Add(w);
			foreach (PubSubNodeInfo n in nodes)
				this.roots.Add(new PubSubNodeRow(n, 0) { IsWatched = this.watched.Contains(n.Node) });
			this.ShowTree();
		}
		catch (Exception ex)
		{
			if (run == this.nodesRun)
				this.Error = Words(ex);
		}
		finally
		{
			if (run == this.nodesRun)
			{
				this.IsBusy = false;
				this.OnPropertyChanged(nameof(this.NodesEmptyText));
				this.TryGo();
			}
		}
	}

	internal static string Words(Exception ex) => ex is TimeoutException ? "The service did not answer in time." : ex.Message;

	/// <summary>Every row of the tree that has been read, whether it shows or not.</summary>
	private static IEnumerable<PubSubNodeRow> Tree(IEnumerable<PubSubNodeRow> rows)
	{
		foreach (PubSubNodeRow r in rows)
		{
			yield return r;
			if (r.Children is not null)
			{
				foreach (PubSubNodeRow c in Tree(r.Children))
					yield return c;
			}
		}
	}

	private IEnumerable<PubSubNodeRow> Loaded() => Tree(this.roots).Concat(this.strays);

	/// <summary>Lays the tree out as the rows that show, keeping the rows that already do.</summary>
	private void ShowTree()
	{
		string filter = this.NodeFilter.Trim();
		List<PubSubNodeRow> shown = [];

		bool Matches(PubSubNodeRow r)
			=> (filter.Length == 0 || r.Node.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) || (r.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
				&& (!this.WatchingOnly || r.IsWatched);

		bool narrowed = filter.Length > 0 || this.WatchingOnly;

		// Narrowed: a flat pass over everything loaded, with the collections on the way down to a match.
		bool Walk(IEnumerable<PubSubNodeRow> rows)
		{
			bool any = false;
			foreach (PubSubNodeRow r in rows)
			{
				int at = shown.Count;
				shown.Add(r);
				bool inside = r.Children is not null && (narrowed || r.IsExpanded) && Walk(r.Children);
				if (!narrowed || Matches(r) || inside)
					any = true;
				else
					shown.RemoveRange(at, shown.Count - at);
			}

			return any;
		}

		Walk(this.roots);

		// What you watch but have not opened the collection of still belongs on the Watching list.
		if (this.WatchingOnly && this.Account is { } a)
		{
			HashSet<string> have = [.. Tree(this.roots).Select(r => r.Node)];
			foreach (string w in this.watched.Where(w => !have.Contains(w) && (filter.Length == 0 || w.Contains(filter, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.OrdinalIgnoreCase))
			{
				PubSubNodeRow? row = this.strays.FirstOrDefault(s => s.Node == w);
				if (row is null)
				{
					string service = this.IsPersonal ? this.Me ?? string.Empty : this.pubsub.Service(a.Id) ?? string.Empty;
					row = new PubSubNodeRow(new PubSubNodeInfo(service, w, null, this.IsPersonal), 0) { IsWatched = true };
					this.strays.Add(row);
				}

				shown.Add(row);
			}
		}

		Reconcile(this.Nodes, shown);
		this.HasNodes = this.Nodes.Count > 0;
		this.WatchedCount = this.watched.Count;
		this.OnPropertyChanged(nameof(this.NodesEmptyText));
	}

	/// <summary>Makes a list show exactly these rows, moving only what has to move so the selection stays.</summary>
	internal static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
		where T : class
	{
		HashSet<T> keep = new(wanted, ReferenceEqualityComparer.Instance);
		for (int i = target.Count - 1; i >= 0; i--)
		{
			if (!keep.Contains(target[i]))
				target.RemoveAt(i);
		}

		for (int i = 0; i < wanted.Count; i++)
		{
			if (i < target.Count && ReferenceEquals(target[i], wanted[i]))
				continue;

			int at = target.IndexOf(wanted[i]);
			if (at < 0)
				target.Insert(i, wanted[i]);
			else
				target.Move(at, i);
		}
	}

	/// <summary>Opens a collection to show the nodes inside it, or closes it again.</summary>
	[RelayCommand]
	private async Task ToggleExpand(PubSubNodeRow? row)
	{
		if (row is null || !row.IsCollection || this.Account is not { } a)
			return;

		if (row.IsExpanded)
		{
			row.IsExpanded = false;
			this.ShowTree();
			return;
		}

		if (row.Children is null)
		{
			try
			{
				this.Error = null;
				IReadOnlyList<PubSubNodeInfo> inside = await this.pubsub.NodesAsync(a.Id, row.Node);
				row.Children = [.. inside.Select(n => new PubSubNodeRow(n, row.Depth + 1) { IsWatched = this.watched.Contains(n.Node) })];
			}
			catch (Exception ex)
			{
				this.Error = Words(ex);
				return;
			}
		}

		row.IsExpanded = true;
		this.ShowTree();
	}

	[RelayCommand]
	private void SetPersonal(string? which) => this.IsPersonal = which == "personal";

	/// <summary>Reads the open node again, or the whole tree when none is open.</summary>
	[RelayCommand]
	private Task Reload()
	{
		if (this.SelectedNode is null)
			return this.LoadNodesAsync();

		return this.Tab switch
		{
			PubSubTab.Settings => this.LoadSettingsAsync(),
			PubSubTab.People => this.LoadPeopleAsync(),
			_ => this.LoadItemsAsync(),
		};
	}

	[RelayCommand]
	private Task ReloadNodes() => this.LoadNodesAsync();

	// ----- The open node -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsItems), nameof(IsSettings), nameof(IsPeople))]
	private PubSubTab tab;

	public bool IsItems => this.HasNode && this.Tab == PubSubTab.Items;
	public bool IsSettings => this.HasNode && this.Tab == PubSubTab.Settings;
	public bool IsPeople => this.HasNode && this.Tab == PubSubTab.People;

	[RelayCommand]
	private void SetTab(PubSubTab t) => this.Tab = t;

	partial void OnTabChanged(PubSubTab value)
	{
		if (this.SelectedNode is null)
			return;

		if (value == PubSubTab.Settings && !this.settingsLoaded)
			_ = this.LoadSettingsAsync();
		else if (value == PubSubTab.People && !this.peopleLoaded)
			_ = this.LoadPeopleAsync();
	}

	partial void OnSelectedNodeChanged(PubSubNodeRow? value)
	{
		this.Ask = null;
		this.IsEditorOpen = false;
		this.ForgetUndo();
		this.WatchNote = null;
		this.settingsLoaded = false;
		this.peopleLoaded = false;
		this.SettingsError = null;
		this.SettingsNote = null;
		this.PeopleError = null;
		this.People.Clear();
		this.Subscribers.Clear();
		this.HasPeople = this.HasSubscribers = false;
		this.shownItems = PageSize;
		this.ItemFilter = string.Empty;

		if (value is not null)
		{
			this.IsCreating = false;
			value.NewCount = 0;
			if (value.IsCollection && this.Tab == PubSubTab.Items)
				this.Tab = PubSubTab.Settings;
		}

		this.OnPropertyChanged(nameof(this.IsWatching));
		this.OnPropertyChanged(nameof(this.WatchTip));
		_ = this.LoadItemsAsync();
		if (value is not null)
			this.OnTabChanged(this.Tab);
	}

	// ----- Watching -----

	/// <summary>The open node is watched: the account subscribes to it.</summary>
	public bool IsWatching => this.SelectedNode?.IsWatched == true;

	public string WatchTip => this.IsWatching
		? "You subscribe to this node: what is published shows up here as it happens. Click to stop."
		: "Subscribe to this node, so what is published shows up here as it happens.";

	/// <summary>What became of asking to watch, when it was not a plain yes.</summary>
	[ObservableProperty] private string? watchNote;

	[RelayCommand]
	private async Task ToggleWatch()
	{
		if (this.Account is not { } a || this.SelectedNode is not { } row)
			return;

		this.WatchNote = null;
		try
		{
			if (row.IsWatched)
			{
				await this.pubsub.UnwatchAsync(a.Id, row.Info);
				row.IsWatched = false;
				this.watched.Remove(row.Node);
				Ui.Say("Stopped watching " + row.Node);
			}
			else
			{
				SubscriberState state = await this.pubsub.WatchAsync(a.Id, row.Info);
				if (state == SubscriberState.Pending)
					this.WatchNote = "Asked to watch. One of the node's owners has to say yes first.";
				else
				{
					row.IsWatched = true;
					this.watched.Add(row.Node);
					Ui.Say("Watching " + row.Node);
				}
			}
		}
		catch (Exception ex)
		{
			this.WatchNote = Words(ex);
		}

		this.WatchedCount = this.watched.Count;
		this.OnPropertyChanged(nameof(this.IsWatching));
		this.OnPropertyChanged(nameof(this.WatchTip));
		if (this.WatchingOnly)
			this.ShowTree();
	}

	/// <summary>Something was published or removed on a node this account watches.</summary>
	private void Happened(NodeItemEvent e)
	{
		if (this.Account?.Id != e.AccountId)
			return;

		PubSubNodeRow? row = this.Loaded().FirstOrDefault(r => r.Node == e.Node && string.Equals(r.Info.Service, e.Service, StringComparison.OrdinalIgnoreCase));
		if (row is null)
			return;

		if (ReferenceEquals(row, this.SelectedNode))
			this.Apply(e);
		else if (e.Item is not null)
			row.NewCount++;
	}

	// ----- A question that wants an answer first: a typed name before something is removed for good -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAsking))]
	private AskViewModel? ask;

	public bool IsAsking => this.Ask is not null;

	[RelayCommand]
	private async Task ConfirmAsk()
	{
		if (this.Ask is AskViewModel a && await a.ConfirmAsync() && ReferenceEquals(this.Ask, a))
			this.Ask = null;
	}

	[RelayCommand]
	private void CancelAsk() => this.Ask = null;

	// ----- Right-click, the "more" buttons and shortcuts -----

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		ActionList list = [];
		if (items.OfType<PubSubItemRow>().ToList() is { Count: > 0 } rows)
			this.ItemActions(list, rows);
		else if (items.Count == 1)
		{
			switch (items[0])
			{
				case PubSubNodeRow node:
					this.NodeActions(list, node);
					break;

				case NodePersonRow person:
					this.PersonActions(list, person);
					break;

				case NodeSubscriberRow subscriber:
					this.SubscriberActions(list, subscriber);
					break;

				case PubSubViewModel when this.SelectedNode is { } open:
					this.NodeActions(list, open);
					break;
			}
		}

		return list;
	}

	private void NodeActions(ActionList list, PubSubNodeRow node)
	{
		bool open = ReferenceEquals(node, this.SelectedNode);
		if (!open)
			list.Open("Open", () => this.SelectedNode = node);

		list.Copy("Copy name", () => node.Node, "the node's name", "Ctrl+C");
		list.Copy("Copy address", () => node.Info.Service, "the service's address");

		if (node.IsLeaf)
		{
			list.Do("Publish…", () =>
			{
				this.SelectedNode = node;
				this.OpenEditorCommand.Execute(null);
			}, icon: "Send");
			list.Do(node.IsWatched ? "Stop watching" : "Watch", () =>
			{
				this.SelectedNode = node;
				return this.ToggleWatchCommand.ExecuteAsync(null);
			}, icon: "Eye");
		}
		else if (this.IsService)
			list.Do("New node inside…", () =>
			{
				this.SelectedNode = node;
				return this.OpenCreateCommand.ExecuteAsync(null);
			}, icon: "Plus");

		list.Do("Settings", () =>
		{
			this.SelectedNode = node;
			this.Tab = PubSubTab.Settings;
		}, icon: "Settings");
		list.Do("People", () =>
		{
			this.SelectedNode = node;
			this.Tab = PubSubTab.People;
		}, icon: "People");

		if (node.IsLeaf)
			list.Do("Remove all items…", () =>
			{
				this.SelectedNode = node;
				this.AskClearCommand.Execute(null);
			}, danger: true, icon: "Eraser");

		if (this.IsService)
			list.Do("Delete node…", () =>
			{
				this.SelectedNode = node;
				this.AskDeleteCommand.Execute(null);
			}, danger: true, icon: "Trash");
	}
}
