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
	[ObservableProperty] private string timeText = string.Empty;
	[ObservableProperty] private bool isCorrected;
	[ObservableProperty] private string? error;
	[ObservableProperty] private bool startsGroup = true;
	[ObservableProperty] private bool isEncrypted;
	[ObservableProperty] private string? sender;
	[ObservableProperty] private bool showSender;

	/// <summary>The first message you have not read: the "New" line sits above it.</summary>
	[ObservableProperty] private bool isFirstNew;

	/// <summary>In a room: the message names you.</summary>
	[ObservableProperty] private bool isMention;
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
	public ObservableCollection<RoomOccupant> Occupants { get; } = [];
	public ObservableCollection<MessageViewModel> Messages { get; } = [];
	public ObservableCollection<AccountChoice> AccountChoices { get; } = [];

	[ObservableProperty] private string filterText = string.Empty;
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

	// Room tools
	[ObservableProperty] private string inviteJid = string.Empty;
	[ObservableProperty] private string? roomError;

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
	[ObservableProperty] private bool removeArmed;

	// Add contact
	[ObservableProperty] private bool isAddOpen;
	[ObservableProperty] private AccountChoice? newContactAccount;
	[ObservableProperty] private string newContactJid = string.Empty;
	[ObservableProperty] private string newContactName = string.Empty;
	[ObservableProperty] private string? addError;

	/// <summary>Set by the shell: opens the console of a Neuron.</summary>
	public Action<Guid, string>? OpenConsoleFor { get; set; }

	partial void OnFilterTextChanged(string value) => Interlocked.Exchange(ref this.dirty, 1);

	private bool reordering;

	partial void OnSelectedPersonChanged(PersonViewModel? value)
	{
		// While the list is being reordered the list box may drop its selection for a moment;
		// that is not the operator picking someone else.
		if (this.reordering)
			return;

		this.RemoveArmed = false;
		this.SendError = null;
		this.RoomError = null;
		this.Messages.Clear();
		if (value is not null)
		{
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
	}

	private void RefreshOccupants()
	{
		this.Occupants.Clear();
		if (this.SelectedPerson is PersonViewModel p && p.IsRoom && this.chat.Room(p.AccountId, p.BareJid) is RoomInfo room)
		{
			foreach (RoomOccupant o in room.Occupants)
				this.Occupants.Add(o);
		}
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
		Dictionary<string, PersonViewModel> existing = this.People.ToDictionary(p => p.AccountId.ToString("N") + "|" + p.BareJid.ToLowerInvariant());
		List<PersonViewModel> list = [];

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
				p.Label = c.Label;
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
			p.PresenceText = room.Joined
				? (room.Occupants.Count == 1 ? "1 person here" : $"{room.Occupants.Count} people here") + (string.IsNullOrWhiteSpace(conv.Subject) ? string.Empty : " · " + conv.Subject)
				: room.Error ?? "Not in the room right now";
			p.LastActivity = conv.LastActivity;
		}

		// Someone who wrote without being a contact has no presence to show.
		foreach (PersonViewModel p in list.Where(p => !p.IsRoom && !p.InRoster && p.PresenceText.Length == 0))
			p.PresenceText = "Not a contact";

		foreach (PersonViewModel p in list.Where(p => !p.IsRoom))
		{
			ContactSecurity s = this.chat.Security(p.AccountId, p.BareJid);
			p.IsEncrypted = s.Encrypted;
			p.SecurityText = s.Encrypted
				? "End-to-end encrypted · their key " + s.Fingerprint
				: p.IsNeuron ? null : "Not encrypted: their app has not shared a key, or they are offline";
		}

		foreach (Conversation conv in this.chat.Conversations)
		{
			if (!profiles.ContainsKey(conv.AccountId) || (conv.Messages.Count == 0 && !conv.IsRoom))
				continue;
			if (conv.IsRoom && this.chat.Room(conv.AccountId, conv.BareJid) is null)
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
			p.Preview = last is null ? null : (last.Direction == ChatDirection.Out ? "You: " : string.Empty) + OneLine(last.Text.Length > 0 ? last.Text : last.Markdown);
			p.TimeText = last is null ? string.Empty : RelativeTime.Format(last.Timestamp, now);
		}

		string filter = this.FilterText.Trim();
		IEnumerable<PersonViewModel> shown = list;
		if (filter.Length > 0)
		{
			shown = shown.Where(p => p.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
				p.BareJid.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
				p.AccountLabel.Contains(filter, StringComparison.OrdinalIgnoreCase));
		}

		List<PersonViewModel> ordered = [.. shown
			.OrderByDescending(p => p.Unread > 0)
			.ThenByDescending(p => p.LastActivity)
			.ThenByDescending(p => p.IsOnline)
			.ThenBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase)];

		PersonViewModel? selected = this.SelectedPerson;
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

	private static string OneLine(string s)
	{
		s = Engine.Attention.NotificationClassifier.Plain(s.Replace('\r', ' ').Replace('\n', ' '));
		return s.Length > 90 ? s[..88] + "…" : s;
	}

	private void LoadConversation(PersonViewModel p, bool reset)
	{
		Conversation? conv = p.IsRoom ? this.chat.GetRoom(p.AccountId, p.BareJid) : this.chat.Find(p.AccountId, p.BareJid);
		IReadOnlyList<ChatMessage> messages = conv?.Messages ?? [];
		Dictionary<string, MessageViewModel> shown = this.Messages.ToDictionary(m => m.Id);

		if (reset)
		{
			this.Messages.Clear();
			this.keepUnread = false;
			this.newLineId = null;
		}

		// The "New" line goes above the first message that arrived while you were not looking.
		// It stays there until you leave the conversation.
		if (conv is not null && conv.Unread > 0 && conv.FirstUnreadId is string first && (reset || !this.IsWatched))
			this.newLineId = first;

		ChatMessage? previous = null;
		int index = 0;
		foreach (ChatMessage m in messages)
		{
			if (!shown.TryGetValue(m.Id, out MessageViewModel? vm) || reset)
			{
				vm = new MessageViewModel(m.Id);
				this.Messages.Insert(Math.Min(index, this.Messages.Count), vm);
			}

			vm.Markdown = m.Markdown;
			vm.IsMine = m.Direction == ChatDirection.Out;
			vm.TimeText = m.Timestamp.ToString(m.Timestamp.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture);
			vm.IsCorrected = m.Corrected is not null;
			vm.Error = m.Error;
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
		PersonViewModel? p = this.People.FirstOrDefault(x => x.AccountId == accountId && string.Equals(x.BareJid, bareJid, StringComparison.OrdinalIgnoreCase));
		if (p is null)
			return false;

		if (ReferenceEquals(this.SelectedPerson, p))
			this.LoadConversation(p, false);
		else
			this.SelectedPerson = p;
		return true;
	}

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

				break;

			case MessageViewModel m:
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

				break;
		}

		return list;
	}

	[RelayCommand(CanExecute = nameof(CanSend))]
	private async Task Send()
	{
		if (this.SelectedPerson is not PersonViewModel p || string.IsNullOrWhiteSpace(this.Draft))
			return;

		string text = this.Draft.TrimEnd();
		this.Draft = string.Empty;
		ChatMessage m = await this.chat.SendAsync(p.AccountId, p.BareJid, text);
		this.SendError = m.Error;
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
	private async Task LeaveRoom()
	{
		if (this.SelectedPerson is not PersonViewModel p || !p.IsRoom)
			return;

		if (!this.RemoveArmed)
		{
			this.RemoveArmed = true;
			return;
		}

		this.RemoveArmed = false;
		await this.chat.LeaveRoomAsync(p.AccountId, p.BareJid);
		this.SelectedPerson = null;
		this.Refresh();
	}

	[RelayCommand]
	private async Task Invite()
	{
		if (this.SelectedPerson is not PersonViewModel p || !p.IsRoom || string.IsNullOrWhiteSpace(this.InviteJid))
			return;

		try
		{
			await this.chat.InviteAsync(p.AccountId, p.BareJid, this.InviteJid.Trim());
			this.RoomError = "Invitation sent to " + this.InviteJid.Trim() + ".";
			this.InviteJid = string.Empty;
		}
		catch (Exception ex)
		{
			this.RoomError = ex.Message;
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
	private async Task RemoveContact()
	{
		if (this.SelectedPerson is not PersonViewModel p)
			return;

		if (!this.RemoveArmed)
		{
			this.RemoveArmed = true;
			return;
		}

		this.RemoveArmed = false;
		try
		{
			await this.chat.RemoveContactAsync(p.AccountId, p.BareJid);
			this.SelectedPerson = null;
			this.Refresh();
		}
		catch (Exception ex)
		{
			this.SendError = ex.Message;
		}
	}

	[RelayCommand]
	private void OpenConsole()
	{
		if (this.SelectedPerson is PersonViewModel p)
			this.OpenConsoleFor?.Invoke(p.AccountId, p.BareJid);
	}
}
