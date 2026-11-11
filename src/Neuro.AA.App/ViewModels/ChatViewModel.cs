using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Chat;
using Waher.Networking.XMPP;

namespace Neuro.AA.App.ViewModels;

/// <summary>A contact or conversation in the chat list.</summary>
public sealed partial class PersonViewModel(Guid accountId, string bareJid) : ObservableObject
{
	public Guid AccountId { get; } = accountId;
	public string BareJid { get; } = bareJid;

	[ObservableProperty] private string label = bareJid;
	[ObservableProperty] private string accountLabel = string.Empty;
	[ObservableProperty] private int accountColorIndex;
	[ObservableProperty] private bool isOnline;
	[ObservableProperty] private bool isAway;
	[ObservableProperty] private string presenceText = string.Empty;
	[ObservableProperty] private bool isNeuron;
	[ObservableProperty] private bool inRoster;
	[ObservableProperty] private int unread;
	[ObservableProperty] private string? preview;
	[ObservableProperty] private string timeText = string.Empty;
	[ObservableProperty] private string? subscriptionNote;
	[ObservableProperty] private bool isRoom;
	[ObservableProperty] private bool isEncrypted;
	[ObservableProperty] private string? securityText;
	[ObservableProperty] private string? subject;
	[ObservableProperty] private int occupantCount;

	/// <summary>The name the contact has in the account's contact list. Empty when it has none.</summary>
	[ObservableProperty] private string name = string.Empty;

	/// <summary>The groups the contact is in.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasGroups), nameof(GroupsText))]
	private IReadOnlyList<string> groups = [];

	public bool HasGroups => this.Groups.Count > 0;
	public string GroupsText => string.Join(", ", this.Groups);

	/// <summary>A conversation with one person inside a room, which goes through the room.</summary>
	[ObservableProperty] private bool isPrivate;

	/// <summary>A room where you may read but not write.</summary>
	[ObservableProperty] private bool isReadOnly;

	/// <summary>A person, as opposed to a room, a Neuron or a conversation inside a room: someone who can be a contact.</summary>
	public bool IsPerson => !this.IsRoom && !this.IsPrivate;

	/// <summary>No pop-ups or sounds from this person or room for now.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasCountedUnread))]
	private bool isMuted;

	/// <summary>"Muted until 14:30", or what the room may interrupt you for.</summary>
	[ObservableProperty] private string notifyText = string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNotifyAll), nameof(IsNotifyMentions), nameof(IsNotifyNothing))]
	private RoomNotify notify;

	public bool IsNotifyAll => this.Notify == RoomNotify.All;
	public bool IsNotifyMentions => this.Notify == RoomNotify.Mentions;
	public bool IsNotifyNothing => this.Notify == RoomNotify.Muted;

	public DateTime LastActivity { get; set; }
	public bool HasUnread => this.Unread > 0;

	/// <summary>Unread, and not muted: it counts towards the totals.</summary>
	public bool HasCountedUnread => this.Unread > 0 && !this.IsMuted;

	partial void OnUnreadChanged(int value)
	{
		this.OnPropertyChanged(nameof(this.HasUnread));
		this.OnPropertyChanged(nameof(this.HasCountedUnread));
	}
}

/// <summary>A message in the open conversation.</summary>
public sealed partial class MessageViewModel(string id) : ObservableObject
{
	public string Id { get; } = id;

	[ObservableProperty] private string markdown = string.Empty;
	[ObservableProperty] private bool isMine;
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StampText))]
	private string timeText = string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StampText), nameof(ShowStamp))]
	private bool isCorrected;

	[ObservableProperty] private string? error;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StampText), nameof(ShowStamp))]
	private bool startsGroup = true;

	/// <summary>The line under a message: its time on the first of a run, and "edited" on any that was changed.</summary>
	public string StampText => this.StartsGroup ? this.TimeText + (this.IsCorrected ? " · edited" : string.Empty) : "edited";

	public bool ShowStamp => this.StartsGroup || this.IsCorrected;
	[ObservableProperty] private bool isEncrypted;
	[ObservableProperty] private string? sender;
	[ObservableProperty] private bool showSender;

	/// <summary>The first message you have not read: the "New" line sits above it.</summary>
	[ObservableProperty] private bool isFirstNew;

	/// <summary>In a room: the message names you.</summary>
	[ObservableProperty] private bool isMention;

	/// <summary>Its writer took it back: a line saying so is left in its place.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(DeletedText))]
	private bool isDeleted;

	public string DeletedText => this.IsMine ? "You deleted this message" : "This message was deleted";

	/// <summary>One of yours that went out and is still there: it can be changed or taken back.</summary>
	[ObservableProperty] private bool canChange;

	/// <summary>The one being answered, changed or asked about.</summary>
	[ObservableProperty] private bool isTarget;

	public bool IsFailed => !string.IsNullOrEmpty(this.Error);

	partial void OnErrorChanged(string? value) => this.OnPropertyChanged(nameof(this.IsFailed));
	partial void OnIsMineChanged(bool value) => this.OnPropertyChanged(nameof(this.DeletedText));
}

/// <summary>An account that can be used to add a contact.</summary>
public sealed record AccountChoice(Guid Id, string Label, int ColorIndex);

/// <summary>
/// The Chat workspace: contacts and conversations across all accounts, subscription requests,
/// and the open conversation with a composer.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, IActionSource
{
	private readonly ChatService chat;
	private readonly AccountManager accounts;
	private readonly DispatcherTimer timer;
	private int dirty = 1;
	private int conversationDirty;
	private string? newLineId;

	/// <summary>
	/// True while you can actually see the open conversation: the window is in front and the
	/// conversation is on screen. Only then do arriving messages count as read. Set by the shell.
	/// </summary>
	public bool IsWatched
	{
		get => this.isWatched;
		set
		{
			if (this.isWatched == value)
				return;

			// Coming back: first place the "New" line (while still not watching), then read.
			if (value && this.SelectedPerson is PersonViewModel p)
				this.LoadConversation(p, false);

			this.isWatched = value;
			if (value && this.SelectedPerson is PersonViewModel q)
				this.LoadConversation(q, false);
		}
	}

	private bool isWatched = true;

	public ChatViewModel(ChatService chat, AccountManager accounts)
	{
		this.chat = chat;
		this.accounts = accounts;
		chat.ConversationChanged += (_, c) =>
		{
			Interlocked.Exchange(ref this.dirty, 1);
			if (this.SelectedPerson is PersonViewModel p && p.AccountId == c.AccountId && string.Equals(p.BareJid, c.BareJid, StringComparison.OrdinalIgnoreCase))
				Interlocked.Exchange(ref this.conversationDirty, 1);
		};
		chat.RosterChanged += (_, _) => Interlocked.Exchange(ref this.dirty, 1);
		chat.RequestsChanged += (_, _) => Interlocked.Exchange(ref this.dirty, 1);
		chat.RoomsChanged += (_, _) => Interlocked.Exchange(ref this.dirty, 1);
		accounts.SessionsChanged += (_, _) => Interlocked.Exchange(ref this.dirty, 1);

		this.timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => this.Tick());
		this.timer.Start();
	}

	public ObservableCollection<PersonViewModel> People { get; } = [];
	public ObservableCollection<SubscriptionRequest> Requests { get; } = [];
	public ObservableCollection<RoomInvitation> Invitations { get; } = [];
	public ObservableCollection<OccupantViewModel> Occupants { get; } = [];
	public ObservableCollection<MessageViewModel> Messages { get; } = [];
	public ObservableCollection<AccountChoice> AccountChoices { get; } = [];

	[ObservableProperty] private string filterText = string.Empty;

	/// <summary>
	/// Which half of Chat the list shows: people, or rooms. Neurons are not people: they have a
	/// page of their own, and only show up among people while a message from one is unread, or
	/// when you type their name in the filter.
	/// </summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowPeople), nameof(FilterHint), nameof(EmptyTitle), nameof(EmptyText))]
	private bool showRooms;

	public bool ShowPeople => !this.ShowRooms;
	public string FilterHint => this.ShowRooms ? "Filter rooms…" : "Filter people…";
	public string EmptyTitle => this.FilterText.Trim().Length > 0 ? "Nothing matches" : this.ShowRooms ? "No rooms yet" : "No contacts yet";
	public string EmptyText => this.FilterText.Trim().Length > 0 ? "Nothing here matches \"" + this.FilterText.Trim() + "\"."
		: this.ShowRooms ? "A room is a group chat. Join one by its name, or create your own."
		: "Contacts of your connected accounts show up here. Add a coworker with their address.";

	[ObservableProperty] private int peopleUnread;
	[ObservableProperty] private int roomsUnread;
	[ObservableProperty] private bool hasPeopleUnread;
	[ObservableProperty] private bool hasRoomsUnread;

	private PersonViewModel? lastPerson;
	private PersonViewModel? lastRoom;
	private List<PersonViewModel> everyone = [];

	partial void OnShowRoomsChanged(bool value)
	{
		// Each half remembers the conversation you had open in it.
		this.IsJoinOpen = false;
		this.IsAddOpen = false;
		PersonViewModel? back = value ? this.lastRoom : this.lastPerson;
		this.SelectedPerson = null;
		this.Refresh();
		if (back is not null && this.everyone.Contains(back))
		{
			if (!this.People.Contains(back))
				this.People.Insert(0, back);

			this.SelectedPerson = back;
			this.Refresh();
		}
	}

	[RelayCommand]
	private void ShowPeopleTab() => this.ShowRooms = false;

	[RelayCommand]
	private void ShowRoomsTab() => this.ShowRooms = true;

	[ObservableProperty] private bool hasRequests;
	[ObservableProperty] private int requestCount;
	[ObservableProperty] private bool hasInvitations;
	[ObservableProperty] private bool canSendFiles;
	[ObservableProperty] private string? fileStatus;

	// Join a room
	[ObservableProperty] private bool isJoinOpen;
	[ObservableProperty] private AccountChoice? joinAccount;
	[ObservableProperty] private string joinRoom = string.Empty;
	[ObservableProperty] private string joinNick = string.Empty;
	[ObservableProperty] private string? joinError;
	[ObservableProperty] private string? joinHint;

	// Sharing from other workspaces
	[ObservableProperty] private string? shareTitle;
	private string? shareMarkdown;
	public bool IsSharing => this.ShareTitle is not null;

	partial void OnShareTitleChanged(string? value) => this.OnPropertyChanged(nameof(this.IsSharing));

	partial void OnJoinAccountChanged(AccountChoice? value)
	{
		string? service = value is null ? null : this.chat.MucService(value.Id);
		this.JoinHint = service is null ? "Give the room's full address." : $"A name alone joins it on {service}.";
		if (value is not null && string.IsNullOrWhiteSpace(this.JoinNick))
			this.JoinNick = value.Label.Split('@')[0];
	}
	[ObservableProperty] private bool hasPeople;
	[ObservableProperty] private int unreadTotal;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasConversation))]
	private PersonViewModel? selectedPerson;

	public bool HasConversation => this.SelectedPerson is not null;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SendCommand))]
	private string draft = string.Empty;

	[ObservableProperty] private string? sendError;

	// Add contact
	[ObservableProperty] private bool isAddOpen;
	[ObservableProperty] private AccountChoice? newContactAccount;
	[ObservableProperty] private string newContactJid = string.Empty;
	[ObservableProperty] private string newContactName = string.Empty;
	[ObservableProperty] private string? addError;

	/// <summary>Set by the shell: opens the console of a Neuron.</summary>
	public Action<Guid, string>? OpenConsoleFor { get; set; }

	partial void OnFilterTextChanged(string value)
	{
		Interlocked.Exchange(ref this.dirty, 1);
		this.OnPropertyChanged(nameof(this.EmptyTitle));
		this.OnPropertyChanged(nameof(this.EmptyText));
	}

	private bool reordering;

	partial void OnSelectedPersonChanged(PersonViewModel? value)
	{
		// While the list is being reordered the list box may drop its selection for a moment;
		// that is not the operator picking someone else.
		if (this.reordering)
			return;

		this.SendError = null;
		this.Messages.Clear();
		this.IsBannedOpen = false;
		this.RoomForm = null;
		this.RestoreDraft(value);
		if (value is not null)
		{
			if (value.IsRoom)
				this.lastRoom = value;
			else
				this.lastPerson = value;

			this.LoadConversation(value, true);
			this.CanSendFiles = this.chat.CanSendFiles(value.AccountId);

			// Sharing: the shared text goes into the composer of whoever is picked.
			if (this.shareMarkdown is not null)
			{
				this.Draft = this.shareMarkdown;
				this.shareMarkdown = null;
				this.ShareTitle = null;
			}
		}

		this.RefreshOccupants();
		this.ApplyPanel();
	}

	/// <summary>Called by the shell: lets the operator pick who gets the shared text.</summary>
	public void BeginShare(string title, string markdown)
	{
		this.ShareTitle = title;
		this.shareMarkdown = markdown;
		this.SelectedPerson = null;
		this.Draft = string.Empty;
	}

	[RelayCommand]
	private void CancelShare()
	{
		this.ShareTitle = null;
		this.shareMarkdown = null;
	}

	private void Tick()
	{
		if (Interlocked.Exchange(ref this.dirty, 0) == 1)
			this.Refresh();

		if (Interlocked.Exchange(ref this.conversationDirty, 0) == 1 && this.SelectedPerson is PersonViewModel p)
			this.LoadConversation(p, false);
	}

	/// <summary>Rebuilds the contact list. UI thread.</summary>
	public void Refresh()
	{
		DateTime now = DateTime.Now;
		Dictionary<Guid, AccountProfile> profiles = this.accounts.Sessions.ToDictionary(s => s.Profile.Id, s => s.Profile);
		// Everyone known so far, shown or not: the half you are not looking at keeps its rows.
		Dictionary<string, PersonViewModel> existing = [];
		foreach (PersonViewModel known in this.everyone.Concat(this.People))
			existing.TryAdd(known.AccountId.ToString("N") + "|" + known.BareJid.ToLowerInvariant(), known);
		List<PersonViewModel> list = [];
		HashSet<PersonViewModel> contacts = [];

		PersonViewModel Person(Guid account, string jid)
		{
			string key = account.ToString("N") + "|" + jid.ToLowerInvariant();
			if (!existing.TryGetValue(key, out PersonViewModel? p))
			{
				p = new PersonViewModel(account, jid);
				existing[key] = p;
			}

			if (!list.Contains(p))
				list.Add(p);

			AccountProfile? prof = profiles.TryGetValue(account, out AccountProfile? x) ? x : null;
			p.AccountLabel = prof?.Label ?? string.Empty;
			p.AccountColorIndex = prof?.ColorIndex ?? 0;
			return p;
		}

		foreach (Guid account in profiles.Keys)
		{
			foreach (Contact c in this.chat.Contacts(account))
			{
				PersonViewModel p = Person(account, c.BareJid);
				contacts.Add(p);
				p.Label = c.Label;
				p.Name = c.Name;
				string[] groups = [.. c.Groups.Order(StringComparer.CurrentCultureIgnoreCase)];
				if (!groups.SequenceEqual(p.Groups))
					p.Groups = groups;
				p.InRoster = true;
				p.IsNeuron = c.IsNeuron;
				p.IsOnline = c.IsOnline;
				p.IsAway = c.IsOnline && c.Availability is Availability.Away or Availability.ExtendedAway or Availability.DoNotDisturb;
				p.PresenceText = !c.IsOnline ? "Offline" : c.Availability switch
				{
					Availability.Away => "Away",
					Availability.ExtendedAway => "Away for a while",
					Availability.DoNotDisturb => "Busy",
					Availability.Chat => "Free to chat",
					_ => "Online",
				} + (string.IsNullOrWhiteSpace(c.Status) ? string.Empty : " · " + c.Status);
				p.SubscriptionNote = c.Subscription switch
				{
					SubscriptionState.None => "Not sharing presence",
					SubscriptionState.From => "They see you; you don't see them",
					_ => null,
				};
			}
		}

		foreach (RoomInfo room in this.chat.Rooms)
		{
			if (!profiles.ContainsKey(room.AccountId))
				continue;

			PersonViewModel p = Person(room.AccountId, room.RoomJid);
			Conversation conv = this.chat.GetRoom(room.AccountId, room.RoomJid);
			p.IsRoom = true;
			p.InRoster = false;
			p.Label = room.RoomId;
			p.Subject = conv.Subject;
			p.OccupantCount = room.Occupants.Count;
			p.IsOnline = room.Joined;
			p.IsReadOnly = room.Joined && room.Me?.IsVisitor == true;
			p.PresenceText = room.Joined
				? (room.Occupants.Count == 1 ? "1 person here" : $"{room.Occupants.Count} people here") + (string.IsNullOrWhiteSpace(conv.Subject) ? string.Empty : " · " + conv.Subject)
				: room.Error ?? "Not in the room right now";
			p.LastActivity = conv.LastActivity;
		}

		// A conversation inside a room, or a contact that is gone, is listed for what was written in it.
		foreach (Conversation conv in this.chat.Conversations)
		{
			if (conv.IsRoom || !profiles.ContainsKey(conv.AccountId))
				continue;
			if (conv.Messages.Count == 0 && !this.started.Contains(conv.AccountId.ToString("N") + "|" + conv.BareJid))
				continue;

			PersonViewModel p = Person(conv.AccountId, conv.BareJid);
			if (!ChatService.IsPrivate(conv.BareJid))
				continue;

			// It goes through the room, and lasts while both are in it.
			string roomJid = conv.BareJid.Split('/')[0];
			string nick = conv.BareJid[(roomJid.Length + 1)..];
			RoomInfo? room = this.chat.Room(conv.AccountId, roomJid);
			bool here = room?.Joined == true && room.Occupants.Any(o => string.Equals(o.NickName, nick, StringComparison.OrdinalIgnoreCase));
			p.IsPrivate = true;
			p.Label = ChatService.PrivateLabel(conv.BareJid);
			p.IsOnline = here;
			p.PresenceText = here ? "In the room now" : room?.Joined == true ? "Not in the room right now" : "You are not in the room right now";
			p.IsEncrypted = false;
			p.SecurityText = "Goes through the room to " + nick + " alone. Not end-to-end encrypted.";
		}

		// Someone who wrote without being a contact has no presence to show. A Neuron is a
		// Neuron whether or not it is a contact.
		foreach (PersonViewModel p in list.Where(p => p.IsPerson && !contacts.Contains(p)))
		{
			p.InRoster = false;
			if (p.Name.Length > 0)
				p.Label = p.BareJid;
			p.PresenceText = "Not a contact";
			p.IsOnline = false;
			p.IsAway = false;
			p.Name = string.Empty;
			p.SubscriptionNote = null;
			if (p.Groups.Count > 0)
				p.Groups = [];
		}

		foreach (PersonViewModel p in list.Where(p => p.IsPerson && !p.IsNeuron))
			p.IsNeuron = this.chat.IsNeuron(p.AccountId, p.BareJid);

		foreach (PersonViewModel p in list.Where(p => p.IsPerson))
		{
			ContactSecurity s = this.chat.Security(p.AccountId, p.BareJid);
			p.IsEncrypted = s.Encrypted;
			p.SecurityText = s.Encrypted
				? "End-to-end encrypted · their key " + s.Fingerprint
				: p.IsNeuron ? null : "Not encrypted: their app has not shared a key, or they are offline";
		}

		foreach (Conversation conv in this.chat.Conversations)
		{
			if (!profiles.ContainsKey(conv.AccountId))
				continue;
			if (conv.IsRoom && this.chat.Room(conv.AccountId, conv.BareJid) is null)
				continue;
			if (!conv.IsRoom && conv.Messages.Count == 0 && !this.started.Contains(conv.AccountId.ToString("N") + "|" + conv.BareJid))
				continue;

			PersonViewModel p = Person(conv.AccountId, conv.BareJid);
			ChatMessage? last = conv.Last;
			p.Unread = conv.Unread;
			p.Notify = conv.Notify;
			p.IsMuted = conv.IsMuted(now);
			p.NotifyText = conv.MutedUntil is DateTime until && until > now
				? (until == DateTime.MaxValue ? "Muted" : "Muted until " + until.ToString(until.Date == now.Date ? "HH:mm" : "ddd HH:mm", CultureInfo.CurrentCulture))
				: !conv.IsRoom ? string.Empty
				: conv.Notify switch
				{
					RoomNotify.All => "Tells you about every message",
					RoomNotify.Muted => "Muted",
					_ => "Tells you when you are mentioned",
				};
			p.LastActivity = conv.LastActivity;
			p.Preview = last is null ? null : (last.Direction == ChatDirection.Out ? "You: " : string.Empty)
				+ (last.Deleted is not null ? "Message deleted" : OneLine(WithoutQuote(last.Text.Length > 0 ? last.Text : last.Markdown)));
			p.TimeText = last is null ? string.Empty : RelativeTime.Format(last.Timestamp, now);
		}

		this.everyone = list;
		this.RefreshGroups(list);
		this.PeopleUnread = list.Where(p => !p.IsRoom && !p.IsMuted).Sum(p => p.Unread);
		this.RoomsUnread = list.Where(p => p.IsRoom && !p.IsMuted).Sum(p => p.Unread);
		this.HasPeopleUnread = this.PeopleUnread > 0;
		this.HasRoomsUnread = this.RoomsUnread > 0;

		// One half at a time. A Neuron is among the people only while it has something unread,
		// while its conversation is open, or when the filter asks for it by name.
		string filter = this.FilterText.Trim();
		bool rooms = this.ShowRooms;
		PersonViewModel? open = this.SelectedPerson;
		IEnumerable<PersonViewModel> shown = list.Where(p => p.IsRoom == rooms);
		if (filter.Length > 0)
		{
			shown = shown.Where(p => p.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
				p.BareJid.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
				p.AccountLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
				p.Groups.Any(g => g.Contains(filter, StringComparison.CurrentCultureIgnoreCase)));
		}
		else
			shown = shown.Where(p => !p.IsNeuron || p.Unread > 0 || ReferenceEquals(p, open));

		// One group at a time, when one is picked above the list.
		if (!rooms && this.GroupFilter is string group)
			shown = shown.Where(p => p.Groups.Contains(group, StringComparer.CurrentCultureIgnoreCase));

		List<PersonViewModel> ordered = [.. shown
			.OrderByDescending(p => p.Unread > 0)
			.ThenByDescending(p => p.LastActivity)
			.ThenByDescending(p => p.IsOnline)
			.ThenBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase)];

		// The open conversation stays in the list while you filter, as long as it belongs to this half.
		PersonViewModel? selected = this.SelectedPerson;
		if (selected is not null && selected.IsRoom != rooms)
			selected = null;

		this.reordering = true;
		try
		{
			for (int i = this.People.Count - 1; i >= 0; i--)
			{
				if (!ordered.Contains(this.People[i]) && !ReferenceEquals(this.People[i], selected))
					this.People.RemoveAt(i);
			}

			for (int i = 0; i < ordered.Count; i++)
			{
				int at = this.People.IndexOf(ordered[i]);
				if (at < 0)
					this.People.Insert(Math.Min(i, this.People.Count), ordered[i]);
				else if (at != i && i < this.People.Count)
					this.People.Move(at, i);
			}

			if (selected is not null && !ReferenceEquals(this.SelectedPerson, selected))
				this.SelectedPerson = selected;
		}
		finally
		{
			this.reordering = false;
		}

		this.HasPeople = this.People.Count > 0;
		this.UnreadTotal = this.chat.UnreadTotal;

		this.Requests.Clear();
		foreach (SubscriptionRequest r in this.chat.Requests)
			this.Requests.Add(r);
		this.HasRequests = this.Requests.Count > 0;
		this.RequestCount = this.Requests.Count;

		this.Invitations.Clear();
		foreach (RoomInvitation i in this.chat.Invitations)
			this.Invitations.Add(i);
		this.HasInvitations = this.Invitations.Count > 0;

		this.RefreshOccupants();
		this.LoadDetails();
		if (this.SelectedPerson is PersonViewModel sel)
			this.CanSendFiles = this.chat.CanSendFiles(sel.AccountId);

		List<AccountChoice> choices = [.. this.accounts.Sessions.Where(s => s.Status == ConnectionStatus.Connected)
			.Select(s => new AccountChoice(s.Profile.Id, s.Profile.Label, s.Profile.ColorIndex))];
		if (!choices.SequenceEqual(this.AccountChoices))
		{
			this.AccountChoices.Clear();
			foreach (AccountChoice c in choices)
				this.AccountChoices.Add(c);
			this.NewContactAccount ??= this.AccountChoices.FirstOrDefault();
			this.JoinAccount ??= this.AccountChoices.FirstOrDefault();
		}
	}

	/// <summary>A reply without the quote it starts with: what was actually said.</summary>
	private static string WithoutQuote(string s)
	{
		string[] lines = s.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		string said = string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith('>'))).Trim();
		return said.Length > 0 ? said : s;
	}

	private static string OneLine(string s)
	{
		s = Engine.Attention.NotificationClassifier.Plain(s.Replace('\r', ' ').Replace('\n', ' '));
		return s.Length > 90 ? s[..88] + "…" : s;
	}

	private void LoadConversation(PersonViewModel p, bool reset)
	{
		Conversation? conv = p.IsRoom ? this.chat.GetRoom(p.AccountId, p.BareJid) : this.chat.Find(p.AccountId, p.BareJid);
		IReadOnlyList<ChatMessage> all = conv?.Messages ?? [];

		if (reset)
		{
			this.Messages.Clear();
			this.keepUnread = false;
			this.newLineId = null;
			this.oldestShownId = null;
		}

		// The "New" line goes above the first message that arrived while you were not looking.
		// It stays there until you leave the conversation.
		if (conv is not null && conv.Unread > 0 && conv.FirstUnreadId is string first && (reset || !this.IsWatched))
			this.newLineId = first;

		// A page from the end, and further back when older ones were asked for.
		int from = this.FirstShown(all, conv);
		this.oldestShownId = all.Count > 0 ? all[from].Id : null;
		this.HasOlder = from > 0;

		// What was deleted from the history goes from the screen too.
		HashSet<string> wanted = [];
		for (int i = from; i < all.Count; i++)
			wanted.Add(all[i].Id);

		for (int i = this.Messages.Count - 1; i >= 0; i--)
		{
			if (!wanted.Contains(this.Messages[i].Id))
			{
				if (ReferenceEquals(this.Messages[i], this.Target))
					this.LeaveBanner();

				this.Messages.RemoveAt(i);
			}
		}

		Dictionary<string, MessageViewModel> shown = this.Messages.ToDictionary(m => m.Id);
		ChatMessage? previous = null;
		int index = 0;
		for (int i = from; i < all.Count; i++)
		{
			ChatMessage m = all[i];
			if (!shown.TryGetValue(m.Id, out MessageViewModel? vm))
			{
				vm = new MessageViewModel(m.Id);
				this.Messages.Insert(Math.Min(index, this.Messages.Count), vm);
			}

			vm.IsMine = m.Direction == ChatDirection.Out;
			vm.IsDeleted = m.Deleted is not null;
			vm.Markdown = m.Markdown;
			vm.TimeText = m.Timestamp.ToString(m.Timestamp.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture);
			vm.IsCorrected = m.Corrected is not null && m.Deleted is null;
			vm.Error = m.Error;
			vm.CanChange = vm.IsMine && m.MessageId is not null && m.Deleted is null && m.Error is null;
			vm.IsEncrypted = m.Encrypted;
			vm.Sender = m.Sender;
			vm.IsMention = m.Mention;
			vm.IsFirstNew = m.Id == this.newLineId;
			vm.StartsGroup = vm.IsFirstNew || previous is null || previous.Direction != m.Direction || previous.Sender != m.Sender || (m.Timestamp - previous.Timestamp) > TimeSpan.FromMinutes(5);
			vm.ShowSender = p.IsRoom && !vm.IsMine && vm.StartsGroup && !string.IsNullOrEmpty(m.Sender);

			previous = m;
			index++;
		}

		// Seen means read, but only while you can actually see it.
		if (conv is not null && conv.Unread > 0 && this.IsWatched && !this.keepUnread)
			this.chat.MarkRead(conv);
	}

	private bool keepUnread;

	/// <summary>Opens a conversation, from a pop-up, the Inbox or a search. Returns false when it is not known.</summary>
	public bool Open(Guid accountId, string bareJid)
	{
		this.FilterText = string.Empty;
		this.Refresh();
		PersonViewModel? p = this.everyone.FirstOrDefault(x => x.AccountId == accountId && string.Equals(x.BareJid, bareJid, StringComparison.OrdinalIgnoreCase));
		if (p is null)
			return false;

		// Go to the half it lives in, and put it in the list even when it is a quiet Neuron.
		if (p.IsRoom)
			this.lastRoom = p;
		else
			this.lastPerson = p;
		this.ShowRooms = p.IsRoom;
		if (!this.People.Contains(p))
			this.People.Insert(0, p);

		if (ReferenceEquals(this.SelectedPerson, p))
			this.LoadConversation(p, false);
		else
			this.SelectedPerson = p;
		this.Refresh();
		return true;
	}

	/// <summary>
	/// Opens the conversation with someone, starting an empty one when nothing was said yet.
	/// Used by "Chat with them" wherever an address shows.
	/// </summary>
	public bool Start(Guid accountId, string bareJid)
	{
		if (this.Open(accountId, bareJid))
			return true;

		Conversation c = this.chat.Get(accountId, bareJid);
		this.started.Add(c.AccountId.ToString("N") + "|" + c.BareJid);
		return this.Open(accountId, bareJid);
	}

	/// <summary>Every person and room Chat knows, whichever half the list shows.</summary>
	public IReadOnlyList<PersonViewModel> Everyone => this.everyone;

	private Conversation? SelectedConversation
		=> this.SelectedPerson is not PersonViewModel p ? null
			: p.IsRoom ? this.chat.GetRoom(p.AccountId, p.BareJid) : this.chat.Get(p.AccountId, p.BareJid);

	/// <summary>Mutes the open person or room: no pop-ups or sounds, and it stays out of the counts.</summary>
	[RelayCommand]
	private void MuteHour() => this.Mute(DateTime.Now.AddHours(1));

	[RelayCommand]
	private void MuteTomorrow() => this.Mute(DateTime.Today.AddDays(1).AddHours(8));

	[RelayCommand]
	private void MuteForGood() => this.Mute(DateTime.MaxValue);

	[RelayCommand]
	private void Unmute() => this.Mute(null);

	private Conversation? ConversationOf(PersonViewModel p)
		=> p.IsRoom ? this.chat.GetRoom(p.AccountId, p.BareJid) : this.chat.Get(p.AccountId, p.BareJid);

	private void Mute(DateTime? until, PersonViewModel? person = null)
	{
		if ((person is null ? this.SelectedConversation : this.ConversationOf(person)) is not Conversation c)
			return;

		this.chat.SetMuted(c, until);
		if (until is null && c.Notify == RoomNotify.Muted)
			this.chat.SetRoomNotify(c, RoomNotify.Mentions);
		this.Refresh();
	}

	/// <summary>Chooses what the open room may interrupt you for.</summary>
	[RelayCommand]
	private void SetNotify(RoomNotify notify)
	{
		if (this.SelectedConversation is not Conversation c || !c.IsRoom)
			return;

		this.chat.SetRoomNotify(c, notify);
		this.Refresh();
	}

	/// <summary>Marks a message, and everything after it, as not read, to come back to later.</summary>
	[RelayCommand]
	private void MarkUnread(MessageViewModel? m)
	{
		if (m is null || this.SelectedConversation is not Conversation c)
			return;

		// Leaving the conversation is what makes "unread" stick: while it stays open, do not read it again.
		this.keepUnread = true;
		this.chat.MarkUnreadFrom(c, m.Id);
		this.newLineId = c.FirstUnreadId;
		if (this.SelectedPerson is PersonViewModel p)
			this.LoadConversation(p, false);
		this.Refresh();
	}

	// ----- Actions: right-click on a person, a room or a message -----

	/// <summary>Set by the shell: the moves to other screens.</summary>
	public IShell? Shell { get; set; }

	/// <summary>A conversation as Markdown, oldest message first.</summary>
	public static string ToMarkdown(Conversation c, string title)
	{
		System.Text.StringBuilder sb = new();
		sb.Append("# ").AppendLine(title).AppendLine();
		DateTime day = DateTime.MinValue;
		foreach (ChatMessage m in c.Messages)
		{
			if (m.Timestamp.Date != day)
			{
				day = m.Timestamp.Date;
				sb.Append("## ").AppendLine(day.ToString("yyyy-MM-dd")).AppendLine();
			}

			string who = m.Direction == ChatDirection.Out ? "Me" : m.Sender ?? title;
			sb.Append("**").Append(who).Append("** ").Append(m.Timestamp.ToString("HH:mm")).AppendLine().AppendLine();
			sb.AppendLine(m.Markdown).AppendLine();
		}

		return sb.ToString();
	}

	private Task SaveConversation(PersonViewModel p)
		=> this.ConversationOf(p) is Conversation c
			? Ui.Save(Ui.FileName("Chat " + p.Label), [new SaveFormat("Markdown", "md", () => ToMarkdown(c, p.Label)), new SaveFormat("Text", "txt", () => ToMarkdown(c, p.Label))])
			: Task.CompletedTask;

	/// <summary>Header button: saves the open conversation.</summary>
	[RelayCommand]
	private Task SaveOpenConversation() => this.SelectedPerson is PersonViewModel p ? this.SaveConversation(p) : Task.CompletedTask;

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		if (items.Count != 1)
			return [];

		ActionList list = [];
		switch (items[0])
		{
			case PersonViewModel p:
				bool open = ReferenceEquals(this.SelectedPerson, p);
				if (!open)
					list.Open(p.IsRoom ? "Open room" : "Open conversation", () => this.SelectedPerson = p);

				list.Copy("Copy address", () => p.BareJid, "the address", "Ctrl+C");
				list.Add(new ItemAction("Save conversation…", ActionGroup.Copy, () => this.SaveConversation(p)) { Icon = "Save", Key = "Ctrl+S" });

				if (p.IsNeuron && this.Shell is IShell shell)
					list.AddRange(shell.NeuronPlaces(p.BareJid, p.AccountId, WorkspaceKind.Chat));

				list.Window("Open in a new window", () => Ui.NewWindow?.Invoke(string.Join('|', WorkspaceKind.Chat, p.AccountId.ToString("N"), p.BareJid, string.Empty)));

				if (p.Unread > 0)
				{
					list.Do("Mark as read", () =>
					{
						if (this.ConversationOf(p) is Conversation c)
							this.chat.MarkRead(c);
						this.Refresh();
					}, icon: "Check");
				}

				if (p.IsMuted)
					list.Do("Unmute", () => this.Mute(null, p), icon: "Bell");
				else
				{
					list.Sub("Mute", ActionGroup.Do,
					[
						new ItemAction("For an hour", ActionGroup.Do, () => this.Mute(DateTime.Now.AddHours(1), p)),
						new ItemAction("Until tomorrow morning", ActionGroup.Do, () => this.Mute(DateTime.Today.AddDays(1).AddHours(8), p)),
						new ItemAction("Until I turn it back on", ActionGroup.Do, () => this.Mute(DateTime.MaxValue, p)),
					], "BellOff");
				}

				if (p.IsRoom)
					this.RoomActions(list, p);
				else
					this.PersonActions(list, p);

				break;

			case OccupantViewModel who:
				this.OccupantActions(list, who);
				break;

			case MessageViewModel m when m.IsDeleted:
				// Nothing is left of it but its place.
				list.Do("Remove from my history", () => this.Delete(m), icon: "Trash");
				break;

			case MessageViewModel m:
				if (m.IsFailed && m.IsMine)
					list.Do("Send again", () => this.SendAgain(m), icon: "Refresh");
				else
					list.Add(new ItemAction("Reply", ActionGroup.Open, () => this.Reply(m)) { Icon = "Reply", Key = "R" });

				if (m.CanChange)
					list.Add(new ItemAction("Edit", ActionGroup.Open, () => this.Edit(m)) { Icon = "Edit", Key = "E" });

				list.Copy("Copy text", () => m.Markdown, "the message", "Ctrl+C");
				list.Copy("Copy as a quote", () => Export.Quote(m.Markdown), "the message as a quote", "Ctrl+Shift+C");

				if (this.SelectedPerson is PersonViewModel with)
				{
					if (with.IsNeuron && this.Shell is IShell s)
						list.GoTo("Console for " + with.Label, () => s.ShowConsole([(with.AccountId, with.BareJid)]), "Console");

					if (!m.IsMine)
						list.Do("Mark as unread from here", () => this.MarkUnread(m), icon: "Undo");

					string from = m.IsMine ? "me" : m.Sender ?? with.Label;
					list.Do("Share with someone else…", () => this.BeginShare("Message from " + from, Export.Quote(m.Markdown)), icon: "Share");
				}

				list.Do(m.CanChange ? "Delete for everyone…" : m.IsMine ? "Remove" : "Remove from my history…", () => this.Delete(m), danger: true, icon: "Trash", key: "Delete");
				break;
		}

		return list;
	}

	[RelayCommand(CanExecute = nameof(CanSend))]
	private async Task Send()
	{
		if (this.SelectedPerson is not PersonViewModel p || string.IsNullOrWhiteSpace(this.Draft))
			return;

		string text = Emoji.Replace(this.Draft.TrimEnd());

		// Saving a change to a message that was already sent.
		if (this.Mode == ComposerMode.Edit && this.Target is MessageViewModel changing && this.SelectedConversation is Conversation c)
		{
			if (text == changing.Markdown)
			{
				this.LeaveBanner();
				return;
			}

			try
			{
				await this.chat.EditAsync(c, changing.Id, text);
				this.SendError = null;
				this.LeaveBanner();
			}
			catch (Exception ex)
			{
				this.SendError = "The change was not sent: " + ex.Message;
			}

			this.LoadConversation(p, false);
			return;
		}

		if (this.Mode == ComposerMode.Reply && this.Target is MessageViewModel answered)
			text = this.ReplyQuote(answered) + "\n\n" + text;

		// One that does not go says so itself, with a way to send it again.
		this.SetMode(ComposerMode.Write, null);
		this.Draft = string.Empty;
		this.SendError = null;
		await this.chat.SendAsync(p.AccountId, p.BareJid, text);
		this.LoadConversation(p, false);
	}

	private bool CanSend() => !string.IsNullOrWhiteSpace(this.Draft);

	/// <summary>Sends files to the open conversation. Called by the view after a pick or a drop.</summary>
	public async Task SendFilesAsync(IEnumerable<string> paths)
	{
		if (this.SelectedPerson is not PersonViewModel p)
			return;

		foreach (string path in paths)
		{
			try
			{
				this.FileStatus = "Uploading " + Path.GetFileName(path) + "…";
				ChatMessage m = await this.chat.SendFileAsync(p.AccountId, p.BareJid, path);
				this.SendError = m.Error;
			}
			catch (Exception ex)
			{
				this.SendError = ex.Message;
			}
		}

		this.FileStatus = null;
		this.LoadConversation(p, false);
	}

	[RelayCommand]
	private void ToggleJoin()
	{
		this.IsJoinOpen = !this.IsJoinOpen;
		this.IsAddOpen = false;
		this.JoinError = null;
		this.JoinAccount ??= this.AccountChoices.FirstOrDefault();
		this.OnJoinAccountChanged(this.JoinAccount);
	}

	[RelayCommand]
	private async Task JoinRoomNow()
	{
		if (this.JoinAccount is not AccountChoice a || string.IsNullOrWhiteSpace(this.JoinRoom) || string.IsNullOrWhiteSpace(this.JoinNick))
		{
			this.JoinError = "Pick an account, and give a room and a nickname.";
			return;
		}

		try
		{
			RoomInfo room = await this.chat.JoinRoomAsync(a.Id, this.JoinRoom, this.JoinNick.Trim());
			this.IsJoinOpen = false;
			this.JoinRoom = string.Empty;
			this.Refresh();
			this.SelectedPerson = this.People.FirstOrDefault(p => p.AccountId == a.Id && string.Equals(p.BareJid, room.RoomJid, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception ex)
		{
			this.JoinError = ex.Message;
		}
	}

	[RelayCommand]
	private async Task AcceptInvitation(RoomInvitation? i)
	{
		if (i is null)
			return;

		string nick = this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == i.AccountId)?.Profile.UserName ?? "operator";
		try
		{
			await this.chat.AcceptInvitationAsync(i, nick);
			this.Refresh();
			this.SelectedPerson = this.People.FirstOrDefault(p => p.AccountId == i.AccountId && string.Equals(p.BareJid, i.RoomJid, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception ex)
		{
			this.SendError = ex.Message;
		}
	}

	[RelayCommand]
	private async Task DeclineInvitation(RoomInvitation? i)
	{
		if (i is not null)
			await this.chat.DeclineInvitationAsync(i);
	}

	[RelayCommand]
	private async Task Accept(SubscriptionRequest? r)
	{
		if (r is not null)
			await this.chat.AcceptAsync(r);
	}

	[RelayCommand]
	private async Task Decline(SubscriptionRequest? r)
	{
		if (r is not null)
			await this.chat.DeclineAsync(r);
	}

	[RelayCommand]
	private void ToggleAdd()
	{
		this.IsAddOpen = !this.IsAddOpen;
		this.IsJoinOpen = false;
		this.AddError = null;
		this.NewContactAccount ??= this.AccountChoices.FirstOrDefault();
	}

	[RelayCommand]
	private async Task AddContact()
	{
		if (this.NewContactAccount is not AccountChoice a)
		{
			this.AddError = "Connect an account first.";
			return;
		}

		string jid = this.NewContactJid.Trim();
		if (jid.Length == 0 || !jid.Contains('@'))
		{
			this.AddError = "Enter an address such as name@domain.";
			return;
		}

		try
		{
			await this.chat.AddContactAsync(a.Id, jid, this.NewContactName);
			this.NewContactJid = string.Empty;
			this.NewContactName = string.Empty;
			this.IsAddOpen = false;
			this.Refresh();
			this.SelectedPerson = this.People.FirstOrDefault(p => p.AccountId == a.Id && string.Equals(p.BareJid, jid, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception ex)
		{
			this.AddError = ex.Message;
		}
	}

	[RelayCommand]
	private void OpenConsole()
	{
		if (this.SelectedPerson is PersonViewModel p)
			this.OpenConsoleFor?.Invoke(p.AccountId, p.BareJid);
	}
}
