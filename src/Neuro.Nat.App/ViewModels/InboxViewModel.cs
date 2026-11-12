using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Devices;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Identity;
using Neuro.Nat.Engine.PubSub;

namespace Neuro.Nat.App.ViewModels;

/// <summary>Which part of the Inbox is showing.</summary>
public enum InboxTab
{
	Everything,
	Messages,
	Requests,
	Neurons,
}

public enum InboxRowKind
{
	Message,
	ContactRequest,
	RoomInvitation,
	DeviceQuestion,
	IdentityRequest,
	NodeRequest,
	Notification,
}

/// <summary>One line in the Inbox: something that waits for you, whatever kind it is.</summary>
public sealed partial class InboxRowViewModel(string key, InboxRowKind kind) : ObservableObject
{
	public string Key { get; } = key;
	public InboxRowKind Kind { get; } = kind;

	/// <summary>The name of the row's icon.</summary>
	public string IconKey => this.Kind switch
	{
		InboxRowKind.Message => this.IsRoom ? "Room" : "Chat",
		InboxRowKind.ContactRequest => "Person",
		InboxRowKind.RoomInvitation => "Room",
		InboxRowKind.DeviceQuestion => "Devices",
		InboxRowKind.IdentityRequest => "Identities",
		InboxRowKind.NodeRequest => "PubSub",
		_ => "Fleet",
	};

	public bool IsMessage => this.Kind == InboxRowKind.Message;
	public bool IsDeviceQuestion => this.Kind == InboxRowKind.DeviceQuestion;
	public bool IsNotification => this.Kind == InboxRowKind.Notification;
	public bool IsRequest => !this.IsMessage && !this.IsNotification;

	[ObservableProperty] private string kindLabel = string.Empty;
	[ObservableProperty] private string title = string.Empty;
	[ObservableProperty] private string? detail;
	[ObservableProperty] private string timeText = string.Empty;
	[ObservableProperty] private string accountLabel = string.Empty;
	[ObservableProperty] private int accountColorIndex;
	/// <summary>The colour of the row's icon: accent for people, busy for what needs an answer, bad for what is broken, info for the rest.</summary>
	[ObservableProperty] private string tone = "accent";
	[ObservableProperty] private bool isUnread;
	[ObservableProperty] private bool isDone;
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IconKey))]
	private bool isRoom;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasCount))]
	private int count;

	public bool HasCount => this.Count > 0 && this.IsMessage;

	/// <summary>The answer on the row: Accept, Join, Allow, Review, Done or Mark read.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasPrimary))]
	private string? primaryText;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSecondary))]
	private string? secondaryText;

	public bool HasPrimary => this.PrimaryText is not null;
	public bool HasSecondary => this.SecondaryText is not null;

	/// <summary>What a request means and what answering does, shown when the row is open.</summary>
	[ObservableProperty] private string? explanation;

	public DateTime When { get; set; }

	// What the row stands for.
	public Guid AccountId { get; set; }
	public string? Jid { get; set; }
	public AttentionItemViewModel? Notification { get; set; }
	public SubscriptionRequest? Request { get; set; }
	public RoomInvitation? Invitation { get; set; }
	public DeviceQuestion? Question { get; set; }
	public IdentityPetition? Petition { get; set; }
	public NodeRequest? NodeAsk { get; set; }
}

/// <summary>
/// The Inbox: one list of everything that waits for you. Unread messages, contact requests,
/// room invitations, questions from devices, requests for your identity, and notifications from
/// your Neurons. Each row is answered where it stands; done rows leave, and an answer can be
/// taken back for a few seconds.
/// </summary>
public sealed partial class InboxViewModel : ObservableObject, IDisposable, IActionSource
{
	private static readonly TimeSpan undoTime = TimeSpan.FromSeconds(6);

	private readonly ChatViewModel chatView;
	private readonly ChatService chat;
	private readonly DevicesService devices;
	private readonly IdentityService identities;
	private readonly AccountManager accounts;
	private readonly PubSubService? pubsub;
	private readonly DispatcherTimer timer;
	private readonly DispatcherTimer undoTimer;
	private readonly HashSet<string> hidden = [];
	private Func<Task>? pendingCommit;
	private Action? pendingUndo;
	private int dirty = 1;
	private int tick;
	private bool syncing;

	public InboxViewModel(AttentionViewModel notifications, ChatViewModel chatView, ChatService chat, DevicesService devices, IdentityService identities, AccountManager accounts, PubSubService? pubsub = null)
	{
		this.pubsub = pubsub;
		if (pubsub is not null)
			pubsub.RequestsChanged += this.Something_Changed;

		this.Notifications = notifications;
		this.chatView = chatView;
		this.chat = chat;
		this.devices = devices;
		this.identities = identities;
		this.accounts = accounts;

		notifications.Items.CollectionChanged += (_, _) => this.dirty = 1;
		notifications.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(AttentionViewModel.OpenCount) or nameof(AttentionViewModel.TotalCount))
				this.dirty = 1;
		};
		chat.ConversationChanged += this.Something_Changed;
		chat.RequestsChanged += this.Something_Changed;
		chat.RoomsChanged += this.Something_Changed;
		devices.QuestionsChanged += this.Something_Changed;
		identities.PetitionsChanged += this.Something_Changed;

		this.timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => this.Tick());
		this.timer.Start();
		this.undoTimer = new DispatcherTimer(undoTime, DispatcherPriority.Background, (_, _) => _ = this.CommitAsync());
	}

	/// <summary>Chat: its open conversation shows beside the list when a message row is open.</summary>
	public ChatViewModel Chat => this.chatView;

	/// <summary>The Neurons' notifications: their list, and the one that is open.</summary>
	public AttentionViewModel Notifications { get; }

	public ObservableCollection<InboxRowViewModel> Rows { get; } = [];

	/// <summary>Everything that is in the Inbox, whatever the tab and the filters show of it.</summary>
	public IReadOnlyList<InboxRowViewModel> Everything { get; private set; } = [];

	/// <summary>
	/// The bare JIDs of the Neurons to show; empty for all. Set by the shell from its Neuron filter.
	/// What a Neuron told you, and what comes from people and things on it, is "from" it.
	/// </summary>
	public IReadOnlySet<string> NeuronFilter
	{
		get => this.neuronFilter;
		set
		{
			value ??= NeuronSet.All;
			if (value.SameAs(this.neuronFilter))
				return;

			this.neuronFilter = value;
			this.Refresh();
		}
	}

	private IReadOnlySet<string> neuronFilter = NeuronSet.All;

	/// <summary>What the Neurons of the filter are called, for the empty list: a name, or "3 of 42 Neurons". Set by the shell before the filter.</summary>
	public string? NeuronFilterName { get; set; }

	/// <summary>"3 more from other Neurons": what the Neuron filter keeps out of the list. Empty when nothing.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasElsewhere))]
	private string elsewhereText = string.Empty;

	public bool HasElsewhere => this.ElsewhereText.Length > 0;

	/// <summary>Set by the shell: takes the Neuron filter away.</summary>
	public Action? ShowAllNeurons { get; set; }

	[RelayCommand]
	private void ShowElsewhere() => this.ShowAllNeurons?.Invoke();

	private static bool IsFrom(InboxRowViewModel r, IReadOnlySet<string> neurons)
	{
		if (r.NodeAsk is NodeRequest q && IsOn(q.Service, neurons))
			return true;

		// A room lives on a part of the Neuron: "muc.lab.tagroot.io" is on "lab.tagroot.io".
		return r.Jid is string jid && IsOn(jid[(jid.IndexOf('@') + 1)..], neurons);
	}

	/// <summary>True when the domain is one of the Neurons, or a part of one: each shorter ending is looked up.</summary>
	private static bool IsOn(string domain, IReadOnlySet<string> neurons)
	{
		for (string d = domain; d.Length > 0;)
		{
			if (neurons.Contains(d))
				return true;

			int dot = d.IndexOf('.');
			if (dot < 0)
				return false;

			d = d[(dot + 1)..];
		}

		return false;
	}

	/// <summary>Set by the shell: opens Identities, where a request for an identity is reviewed.</summary>
	public Action? OpenIdentities { get; set; }

	/// <summary>Set by the shell: opens Devices, where a question can be answered for a whole domain.</summary>
	public Action? OpenDevices { get; set; }

	private void Something_Changed(object? sender, object e) => Interlocked.Exchange(ref this.dirty, 1);

	private void Tick()
	{
		// Rebuild when something changed, and every 20 s so the times stay fresh.
		if (Interlocked.Exchange(ref this.dirty, 0) == 1 || ++this.tick % 50 == 0)
			this.Refresh();
	}

	// ----- Tabs, search and done -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsEverything), nameof(IsMessages), nameof(IsRequests), nameof(IsNeurons), nameof(EmptyTitle), nameof(EmptyText))]
	private InboxTab tab = InboxTab.Everything;

	public bool IsEverything => this.Tab == InboxTab.Everything;
	public bool IsMessages => this.Tab == InboxTab.Messages;
	public bool IsRequests => this.Tab == InboxTab.Requests;
	public bool IsNeurons => this.Tab == InboxTab.Neurons;

	[RelayCommand]
	private void SetTab(InboxTab t) => this.Tab = t;

	partial void OnTabChanged(InboxTab value) => this.Refresh();

	[ObservableProperty] private string searchText = string.Empty;

	partial void OnSearchTextChanged(string value)
	{
		this.Notifications.SearchText = value;
		this.Refresh();
	}

	/// <summary>Also show what is done: handled notifications and the ones that were only information.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyText))]
	private bool showDone;

	partial void OnShowDoneChanged(bool value)
	{
		this.Notifications.Filter = value ? AttentionFilter.All : AttentionFilter.Open;
		this.Refresh();
	}

	// ----- Counts -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasMessages))]
	private int messageCount;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasRequests))]
	private int requestCount;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNeurons))]
	private int neuronCount;

	public bool HasMessages => this.MessageCount > 0;
	public bool HasRequests => this.RequestCount > 0;
	public bool HasNeurons => this.NeuronCount > 0;

	/// <summary>Everything that waits for you: the number on the rail, in the title and on the tray icon.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasWaiting), nameof(Badge), nameof(Summary))]
	private int waitingCount;

	public bool HasWaiting => this.WaitingCount > 0;
	public string Badge => this.WaitingCount > 99 ? "99+" : this.WaitingCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
	public string Summary => this.WaitingCount == 0 ? "Nothing waits for you" : this.WaitingCount == 1 ? "1 thing waits for you" : $"{this.WaitingCount} things wait for you";

	[ObservableProperty] private bool hasRows;

	public string EmptyTitle => this.HasElsewhere ? "Nothing from " + (this.NeuronFilterName ?? NeuronSet.ToText(this.neuronFilter)) + " here" : this.Tab switch
	{
		InboxTab.Messages => "No unread messages",
		InboxTab.Requests => "No requests",
		InboxTab.Neurons => this.ShowDone ? "No notifications" : "Nothing from your Neurons needs you",
		_ => "Nothing waits for you",
	};

	public string EmptyText => this.HasElsewhere ? (this.neuronFilter.Count == 1 ? "The list shows one Neuron only. " : $"The list shows {this.neuronFilter.Count} Neurons only. ") + this.ElsewhereText + "." : string.Empty;

	// ----- Selection -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowsNotification), nameof(ShowsConversation), nameof(ShowsRequest))]
	private InboxRowViewModel? selectedRow;

	public bool HasSelection => this.SelectedRow is not null && !this.HasMany;
	public bool ShowsNotification => this.HasSelection && this.SelectedRow!.IsNotification;
	public bool ShowsConversation => this.HasSelection && this.SelectedRow!.IsMessage;
	public bool ShowsRequest => this.HasSelection && this.SelectedRow!.IsRequest;

	/// <summary>Raised when the conversation beside the list opens or closes, so the shell knows whether it is watched.</summary>
	public event EventHandler? ShowsConversationChanged;

	partial void OnSelectedRowChanged(InboxRowViewModel? value)
	{
		if (this.syncing)
			return;

		switch (value?.Kind)
		{
			case InboxRowKind.Notification:
				this.Notifications.SelectedItem = value.Notification;
				break;

			case InboxRowKind.Message when value.Jid is string jid:
				this.chatView.Open(value.AccountId, jid);
				break;
		}

		this.ShowsConversationChanged?.Invoke(this, EventArgs.Empty);
		this.dirty = 1;
	}

	/// <summary>Selects the row a pop-up was about. Returns false when it is not in the list.</summary>
	public bool Select(string key)
	{
		this.Refresh();
		InboxRowViewModel? row = this.Rows.FirstOrDefault(r => r.Key == key);
		if (row is not null)
			this.SelectedRow = row;
		return row is not null;
	}

	/// <summary>The rows picked with Ctrl or Shift. Set by the view.</summary>
	public IReadOnlyList<InboxRowViewModel> SelectedRows
	{
		get => this.selectedRows;
		set
		{
			this.selectedRows = value;
			this.OnPropertyChanged(nameof(this.HasMany));
			this.OnPropertyChanged(nameof(this.ManyText));
			this.OnPropertyChanged(nameof(this.HasSelection));
			this.OnPropertyChanged(nameof(this.ShowsNotification));
			this.OnPropertyChanged(nameof(this.ShowsConversation));
			this.OnPropertyChanged(nameof(this.ShowsRequest));
		}
	}

	private IReadOnlyList<InboxRowViewModel> selectedRows = [];

	public bool HasMany => this.selectedRows.Count > 1;
	public string ManyText => $"{this.selectedRows.Count} picked";

	/// <summary>Marks every picked notification done and every picked conversation read. Requests need their own answer.</summary>
	[RelayCommand]
	private async Task DoneMany()
	{
		await this.CommitAsync();
		List<InboxRowViewModel> rows = [.. this.selectedRows];
		List<Action> undo = [];
		foreach (InboxRowViewModel row in rows)
		{
			if (this.Finish(row) is Action u)
				undo.Add(u);
		}

		if (undo.Count > 0)
			this.Offer(undo.Count == 1 ? "Marked done." : $"Marked {undo.Count} done.", () => undo.ForEach(u => u()), null);
		this.Refresh();
	}

	// ----- Actions: the right-click menu and shortcuts of a row -----

	/// <summary>Set by the shell: the moves to other screens.</summary>
	public IShell? Shell { get; set; }

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		List<InboxRowViewModel> rows = [.. items.OfType<InboxRowViewModel>()];
		if (rows.Count == 0)
			return [];

		ActionList list = [];
		if (rows.Count > 1)
		{
			list.Copy("Copy as text", () => string.Join(Environment.NewLine, rows.Select(r => r.Title + ": " + r.Detail)), Ui.Count(rows.Count, "row"), "Ctrl+C");
			list.Do("Mark done", () => this.DoneManyCommand.Execute(null), icon: "Check");
			return list;
		}

		InboxRowViewModel row = rows[0];
		if (!ReferenceEquals(this.SelectedRow, row))
			list.Open("Open", () => this.SelectedRow = row);

		list.Copy("Copy text", () => string.IsNullOrEmpty(row.Detail) ? row.Title : row.Title + Environment.NewLine + row.Detail, "the text", "Ctrl+C");
		if (row.Jid is string jid)
			list.Copy("Copy address", () => jid, "the address");

		if (this.Shell is IShell shell)
		{
			if (row.Notification is AttentionItemViewModel n && !string.IsNullOrEmpty(n.NeuronJid))
				list.Sub("Go to " + n.NeuronLabel, ActionGroup.GoTo, shell.NeuronPlaces(n.NeuronJid));
			else if (row.IsMessage && row.Jid is string who)
				list.GoTo("Chat", () => shell.ShowChat(row.AccountId, who), "Chat");
			else if (row.NodeAsk is NodeRequest ask)
			{
				list.GoTo("The node, and who subscribes", () => shell.ShowNode(ask.AccountId, ask.Service, ask.Node, people: true), "PubSub");
				list.GoTo("Chat with them", () => shell.ShowChat(ask.AccountId, ask.Jid), "Chat");
			}
			else if (row.Request is not null && row.Jid is string asker)
				list.GoTo("Chat with them", () => shell.ShowChat(row.AccountId, asker), "Chat");
		}

		if (!row.IsDone)
		{
			if (!string.IsNullOrEmpty(row.PrimaryText))
				list.Do(row.PrimaryText, () => this.PrimaryCommand.Execute(row), icon: "Check");
			if (!string.IsNullOrEmpty(row.SecondaryText))
				list.Do(row.SecondaryText, () => this.SecondaryCommand.Execute(row));
		}
		else if (row.Notification is AttentionItemViewModel done)
			list.Do("Reopen", () =>
			{
				this.Notifications.ReopenCommand.Execute(done);
				this.Refresh();
			}, icon: "Undo");

		return list;
	}

	// ----- Building the list -----

	private static string ConversationKey(Guid account, string jid) => "m:" + account.ToString("N") + "|" + jid.ToLowerInvariant();

	/// <summary>Rebuilds the list. UI thread.</summary>
	public void Refresh()
	{
		DateTime now = DateTime.Now;
		Dictionary<Guid, AccountProfile> profiles = this.accounts.Sessions.ToDictionary(s => s.Profile.Id, s => s.Profile);
		Dictionary<string, InboxRowViewModel> existing = this.Rows.ToDictionary(r => r.Key);
		List<InboxRowViewModel> list = [];
		string? keep = this.SelectedRow?.Key;

		InboxRowViewModel Row(string key, InboxRowKind kind, Guid account, DateTime when)
		{
			if (!existing.TryGetValue(key, out InboxRowViewModel? r))
				r = new InboxRowViewModel(key, kind);

			AccountProfile? p = profiles.TryGetValue(account, out AccountProfile? x) ? x : null;
			r.AccountId = account;
			r.AccountLabel = profiles.Count > 1 ? p?.ShortLabel ?? string.Empty : string.Empty;
			r.AccountColorIndex = p?.ColorIndex ?? 0;
			r.When = when;
			r.TimeText = RelativeTime.Format(when, now);
			list.Add(r);
			return r;
		}

		// Messages you have not read. The open one stays while it is open, so it does not vanish as you read it.
		int messages = 0;
		foreach (Conversation c in this.chat.Conversations)
		{
			if (!profiles.ContainsKey(c.AccountId) || c.Last is not ChatMessage last)
				continue;

			string key = ConversationKey(c.AccountId, c.BareJid);
			bool waits = c.Unread > 0 && !c.IsMuted(now);
			if (waits)
				messages++;
			if (!waits && key != keep)
				continue;

			Contact? contact = c.IsRoom ? null : this.chat.Contacts(c.AccountId).FirstOrDefault(x => string.Equals(x.BareJid, c.BareJid, StringComparison.OrdinalIgnoreCase));
			InboxRowViewModel r = Row(key, InboxRowKind.Message, c.AccountId, last.Timestamp);
			r.Jid = c.BareJid;
			r.IsRoom = c.IsRoom;
			r.KindLabel = c.IsRoom ? "Room" : "Message";
			r.Title = c.IsRoom ? c.BareJid.Split('@')[0] : contact?.Label ?? c.BareJid;
			string text = Engine.Attention.NotificationClassifier.Plain((last.Text.Length > 0 ? last.Text : last.Markdown).ReplaceLineEndings(" "));
			r.Detail = (last.Direction == ChatDirection.Out ? "You: " : c.IsRoom && !string.IsNullOrEmpty(last.Sender) ? last.Sender + ": " : string.Empty) + text;
			r.Count = c.Unread;
			r.IsUnread = c.Unread > 0;
			r.IsDone = c.Unread == 0;
			r.Tone = "accent";
			r.PrimaryText = c.Unread > 0 ? "Mark read" : null;
			r.SecondaryText = null;
		}

		// Requests: each has its answer on the row.
		int requests = 0;
		foreach (SubscriptionRequest q in this.chat.Requests)
		{
			InboxRowViewModel r = Row("c:" + q.AccountId.ToString("N") + "|" + q.BareJid.ToLowerInvariant(), InboxRowKind.ContactRequest, q.AccountId, q.Received);
			r.Request = q;
			r.Jid = q.BareJid;
			r.KindLabel = "Contact request";
			r.Title = q.BareJid + " wants to be a contact";
			r.Detail = string.IsNullOrEmpty(r.AccountLabel) ? null : "Asks " + r.AccountLabel;
			(r.PrimaryText, r.SecondaryText) = ("Accept", "Decline");
		}

		foreach (RoomInvitation i in this.chat.Invitations)
		{
			InboxRowViewModel r = Row("inv:" + i.AccountId.ToString("N") + "|" + i.RoomJid.ToLowerInvariant(), InboxRowKind.RoomInvitation, i.AccountId, i.Received);
			r.Invitation = i;
			r.Jid = i.RoomJid;
			r.KindLabel = "Room invitation";
			r.Title = $"{i.From} invites you to {i.RoomJid.Split('@')[0]}";
			r.Detail = string.IsNullOrWhiteSpace(i.Reason) ? i.RoomJid : i.Reason;
			(r.PrimaryText, r.SecondaryText) = ("Join", "Decline");
		}

		foreach (DeviceQuestion q in this.devices.Questions)
		{
			InboxRowViewModel r = Row("q:" + q.Id, InboxRowKind.DeviceQuestion, q.AccountId, q.Received);
			r.Question = q;
			r.Jid = q.DeviceJid;
			r.KindLabel = "Device question";
			r.Tone = "busy";
			r.Title = q.Kind switch
			{
				DeviceQuestionKind.Friendship => $"{q.RemoteJid} wants to be a contact of a device",
				DeviceQuestionKind.Readout => $"{q.RemoteJid} wants to read a device",
				_ => $"{q.RemoteJid} wants to control a device",
			};
			r.Detail = q.Names.Count == 0 ? q.DeviceJid : q.DeviceJid + ": " + string.Join(", ", q.Names);
			r.Explanation = "Answers for this asker only.";
			(r.PrimaryText, r.SecondaryText) = ("Allow", "Deny");
		}

		foreach (IdentityPetition p in this.identities.Petitions)
		{
			InboxRowViewModel r = Row("p:" + p.PetitionId, InboxRowKind.IdentityRequest, p.AccountId, p.Received);
			r.Petition = p;
			r.KindLabel = "Identity request";
			r.Tone = "busy";
			r.Title = (p.RequestorName ?? p.From) + (p.Kind == "identity" ? " asks to see your identity" : " asks for your signature");
			r.Detail = string.IsNullOrWhiteSpace(p.Purpose) ? p.From : p.Purpose;
			(r.PrimaryText, r.SecondaryText) = ("Review", null);
		}

		foreach (NodeRequest q in this.pubsub?.Requests ?? [])
		{
			InboxRowViewModel r = Row("ps:" + q.AccountId.ToString("N") + "|" + q.Node + "|" + q.Jid.ToLowerInvariant(), InboxRowKind.NodeRequest, q.AccountId, q.Received);
			r.NodeAsk = q;
			r.Jid = q.Jid;
			r.KindLabel = "Subscription request";
			r.Tone = "busy";
			r.Title = $"{q.Jid} wants to subscribe to {q.Node}";
			r.Detail = "A node you own on " + q.Service;
			(r.PrimaryText, r.SecondaryText) = ("Allow", "Deny");
		}

		requests = list.Count(r => r.IsRequest);

		// What the Neurons told their administrators.
		foreach (AttentionItemViewModel n in this.Notifications.Items)
		{
			InboxRowViewModel r = Row("n:" + n.Id, InboxRowKind.Notification, n.Model?.AccountId ?? Guid.Empty, n.Model?.Updated ?? n.Model?.Received ?? now);
			r.Notification = n;
			r.Jid = n.NeuronJid;
			r.KindLabel = n.KindLabel;
			r.Title = n.Title;
			r.Detail = n.Meta;
			r.Tone = n.Severity >= EventSeverity.Error ? "bad" : n.Severity == EventSeverity.Warning || n.IsOpen ? "busy" : "info";
			r.IsUnread = n.IsUnread;
			r.IsDone = !n.IsOpen;
			r.TimeText = n.TimeText;
			(r.PrimaryText, r.SecondaryText) = (n.IsOpen ? "Done" : null, null);
		}

		this.MessageCount = messages;
		this.RequestCount = requests;
		this.NeuronCount = this.Notifications.OpenCount;
		this.WaitingCount = messages + requests + this.Notifications.OpenCount;

		this.Everything = list;
		string search = this.SearchText.Trim();
		List<InboxRowViewModel> inTab = [.. list
			.Where(r => !this.hidden.Contains(r.Key))
			.Where(r => this.Tab switch
			{
				InboxTab.Messages => r.IsMessage,
				InboxTab.Requests => r.IsRequest,
				InboxTab.Neurons => r.IsNotification,
				_ => true,
			})];

		// The Neuron chips: only what is from that Neuron, and a word about what is left out.
		int elsewhere = 0;
		if (this.neuronFilter.Count > 0)
		{
			IReadOnlySet<string> only = this.neuronFilter;
			elsewhere = inTab.Count(r => !r.IsDone && !IsFrom(r, only));
			inTab.RemoveAll(r => !IsFrom(r, only));
		}

		this.ElsewhereText = elsewhere == 0 ? string.Empty : elsewhere == 1 ? "1 more waits from elsewhere" : $"{elsewhere} more wait from elsewhere";
		this.OnPropertyChanged(nameof(this.EmptyTitle));
		this.OnPropertyChanged(nameof(this.EmptyText));

		List<InboxRowViewModel> shown = [.. inTab
			.Where(r => search.Length == 0 || r.IsNotification ||
				r.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
				(r.Detail?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
				r.KindLabel.Contains(search, StringComparison.OrdinalIgnoreCase))
			.OrderBy(r => r.IsDone)
			.ThenByDescending(r => r.When)];

		InboxRowViewModel? selected = this.SelectedRow;
		int selectedIndex = selected is null ? -1 : this.Rows.IndexOf(selected);
		this.syncing = true;
		try
		{
			for (int i = this.Rows.Count - 1; i >= 0; i--)
			{
				if (!shown.Contains(this.Rows[i]))
					this.Rows.RemoveAt(i);
			}

			for (int i = 0; i < shown.Count; i++)
			{
				int at = this.Rows.IndexOf(shown[i]);
				if (at < 0)
					this.Rows.Insert(Math.Min(i, this.Rows.Count), shown[i]);
				else if (at != i)
					this.Rows.Move(at, i);
			}

			if (selected is not null && shown.Contains(selected) && !ReferenceEquals(this.SelectedRow, selected))
				this.SelectedRow = selected;
		}
		finally
		{
			this.syncing = false;
		}

		// The open row was answered or done: move on to the next one.
		if (selected is not null && !shown.Contains(selected))
			this.SelectedRow = shown.Count == 0 ? null : shown[Math.Clamp(selectedIndex, 0, shown.Count - 1)];

		this.HasRows = this.Rows.Count > 0;
	}

	// ----- Answers -----

	/// <summary>The answer that can still be taken back: "Accepted kim@lab.local."</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasUndo))]
	private string? undoText;

	public bool HasUndo => this.UndoText is not null;

	[ObservableProperty] private string? error;

	/// <summary>The first answer of a row: Accept, Join, Allow, Review, Done or Mark read.</summary>
	[RelayCommand]
	private Task Primary(InboxRowViewModel? row) => this.Answer(row ?? this.SelectedRow, true);

	/// <summary>The other answer: Decline or Deny.</summary>
	[RelayCommand]
	private Task Secondary(InboxRowViewModel? row) => this.Answer(row ?? this.SelectedRow, false);

	private async Task Answer(InboxRowViewModel? row, bool yes)
	{
		if (row is null)
			return;

		// One answer can be taken back at a time: the one before this is final now.
		await this.CommitAsync();
		this.Error = null;

		switch (row.Kind)
		{
			case InboxRowKind.Message:
			case InboxRowKind.Notification:
				if (this.Finish(row) is Action undo)
					this.Offer(row.IsMessage ? "Marked as read." : "Marked done.", undo, null);
				break;

			case InboxRowKind.ContactRequest when row.Request is SubscriptionRequest q:
				this.Later(row, (yes ? "Accepted " : "Declined ") + q.BareJid + ".", () => yes ? this.chat.AcceptAsync(q) : this.chat.DeclineAsync(q));
				break;

			case InboxRowKind.RoomInvitation when row.Invitation is RoomInvitation i:
				this.Later(row, yes ? "Joining " + i.RoomJid.Split('@')[0] + "." : "Declined the invitation.", async () =>
				{
					if (yes)
					{
						string nick = this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == i.AccountId)?.Profile.UserName ?? "operator";
						await this.chat.AcceptInvitationAsync(i, nick);
					}
					else
						await this.chat.DeclineInvitationAsync(i);
				});
				break;

			case InboxRowKind.DeviceQuestion when row.Question is DeviceQuestion d:
				this.Later(row, (yes ? "Allowed " : "Denied ") + d.RemoteJid + ".", () => this.devices.AnswerAsync(d, yes, AnswerScope.Caller));
				break;

			case InboxRowKind.NodeRequest when row.NodeAsk is NodeRequest n && this.pubsub is PubSubService service:
				this.Later(row, (yes ? "Allowed " : "Denied ") + n.Jid + ".", () => service.AnswerAsync(n, yes));
				break;

			case InboxRowKind.IdentityRequest:
				this.OpenIdentities?.Invoke();
				break;
		}

		this.Refresh();
	}

	/// <summary>Marks a notification done or a conversation read, right away. Returns how to take it back.</summary>
	private Action? Finish(InboxRowViewModel row)
	{
		if (row.Notification is AttentionItemViewModel n && n.IsOpen)
		{
			this.Notifications.MarkHandledCommand.Execute(n);
			return () => this.Notifications.ReopenCommand.Execute(n);
		}

		if (row.IsMessage && row.Jid is string jid && this.chat.Find(row.AccountId, jid) is Conversation c && c.Unread > 0)
		{
			string? first = c.FirstUnreadId;
			this.chat.MarkRead(c);
			return first is null ? null : () => this.chat.MarkUnreadFrom(c, first);
		}

		return null;
	}

	/// <summary>Hides the row now and sends the answer when the time to take it back is over.</summary>
	private void Later(InboxRowViewModel row, string text, Func<Task> commit)
	{
		this.hidden.Add(row.Key);
		this.Offer(text, () => this.hidden.Remove(row.Key), async () =>
		{
			try
			{
				await commit();
			}
			catch (Exception ex)
			{
				this.Error = ex.Message;
			}
			finally
			{
				this.hidden.Remove(row.Key);
				this.dirty = 1;
			}
		});
	}

	private void Offer(string text, Action undo, Func<Task>? commit)
	{
		this.pendingUndo = undo;
		this.pendingCommit = commit;
		this.UndoText = text;
		this.undoTimer.Stop();
		this.undoTimer.Start();
	}

	/// <summary>Takes the last answer back.</summary>
	[RelayCommand]
	private void Undo()
	{
		this.undoTimer.Stop();
		Action? undo = this.pendingUndo;
		this.pendingUndo = null;
		this.pendingCommit = null;
		this.UndoText = null;
		undo?.Invoke();
		this.Refresh();
	}

	/// <summary>Makes the last answer final now. Also runs when the time to take it back is over, and before the app quits.</summary>
	public async Task CommitAsync()
	{
		this.undoTimer.Stop();
		Func<Task>? commit = this.pendingCommit;
		this.pendingUndo = null;
		this.pendingCommit = null;
		this.UndoText = null;
		if (commit is not null)
			await commit();
	}

	[RelayCommand]
	private void ToggleShowDone() => this.ShowDone = !this.ShowDone;

	/// <summary>Marks every unread conversation read and every notification read.</summary>
	[RelayCommand]
	private void MarkAllRead()
	{
		foreach (Conversation c in this.chat.Conversations)
			this.chat.MarkRead(c);
		this.Notifications.MarkAllReadCommand.Execute(null);
		this.Refresh();
	}

	[RelayCommand]
	private void ShowDevices() => this.OpenDevices?.Invoke();

	public void Dispose()
	{
		this.timer.Stop();
		this.undoTimer.Stop();
		this.chat.ConversationChanged -= this.Something_Changed;
		this.chat.RequestsChanged -= this.Something_Changed;
		this.chat.RoomsChanged -= this.Something_Changed;
		this.devices.QuestionsChanged -= this.Something_Changed;
		this.identities.PetitionsChanged -= this.Something_Changed;
	}
}
