using System.Collections.Concurrent;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Events;

namespace Neuro.Nat.Engine.Chat;

/// <summary>A roster contact of an account, with what is known about its presence.</summary>
public sealed record Contact(
	Guid AccountId,
	string BareJid,
	string Name,
	IReadOnlyList<string> Groups,
	SubscriptionState Subscription,
	bool IsOnline,
	Availability Availability,
	string? Status,
	bool IsNeuron)
{
	/// <summary>What a Neuron calls itself, once it has said. See <see cref="WatchedNeuron.OwnName"/>.</summary>
	public string? OwnName { get; init; }

	/// <summary>The name on screen. A Neuron without a roster name goes by what it calls itself, else by the domain of its address.</summary>
	public string Label => this.IsNeuron ? NeuronNames.Of(this.Name, this.OwnName, this.BareJid) : string.IsNullOrWhiteSpace(this.Name) ? this.BareJid : this.Name;
}

/// <summary>
/// Chat for every account: conversations, the roster and subscription requests, and the chat
/// admin consoles of Neurons. A Neuron's notifications (sent to the bare JID) belong to the
/// Attention list and are left out here; its answers to console commands (sent to this client's
/// full JID, in the command's thread) go to the console.
/// </summary>
public sealed partial class ChatService : IAsyncDisposable
{
	private readonly AccountManager accounts;
	private readonly MonitorService monitors;
	private readonly IChatStore? store;
	private readonly ConcurrentDictionary<string, Conversation> conversations = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, ConsoleSession> consoles = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<Guid, AccountChat> attached = new();
	private readonly HashSet<AccountSession> watched = [];
	private readonly List<SubscriptionRequest> requests = [];
	private readonly Timer saveTimer;
	private int dirty;

	public ChatService(AccountManager accounts, MonitorService monitors, IChatStore? store = null)
	{
		this.accounts = accounts;
		this.monitors = monitors;
		this.store = store;
		this.saveTimer = new Timer(_ => this.SaveIfDirty(), null, Timeout.Infinite, Timeout.Infinite);

		monitors.SubscriptionRequested += this.Monitors_SubscriptionRequested;
		monitors.NeuronsChanged += this.Monitors_NeuronsChanged;
		accounts.SessionsChanged += (_, _) => this.SyncSessions();
		this.SyncSessions();
		this.Monitors_NeuronsChanged(monitors, EventArgs.Empty);
	}

	/// <summary>Raised when a conversation gets a message or changes. Any thread.</summary>
	public event EventHandler<Conversation>? ConversationChanged;

	/// <summary>Raised for each new message from someone else (not history, not corrections). Any thread.</summary>
	public event EventHandler<IncomingMessage>? MessageReceived;

	/// <summary>Raised when an account's roster or a contact's presence changes. Any thread.</summary>
	public event EventHandler<Guid>? RosterChanged;

	/// <summary>Raised when subscription requests arrive or are answered. Any thread.</summary>
	public event EventHandler? RequestsChanged;

	// ---- Conversations ---------------------------------------------------------------------

	private static string Key(Guid accountId, string bareJid) => accountId.ToString("N") + "|" + bareJid.ToLowerInvariant();

	public IReadOnlyList<Conversation> Conversations => [.. this.conversations.Values.OrderByDescending(c => c.LastActivity)];

	public Conversation Get(Guid accountId, string bareJid)
		=> this.conversations.GetOrAdd(Key(accountId, bareJid), _ => new Conversation(accountId, bareJid));

	public Conversation? Find(Guid accountId, string bareJid)
		=> this.conversations.TryGetValue(Key(accountId, bareJid), out Conversation? c) ? c : null;

	public void MarkRead(Conversation c)
	{
		if (c.Unread == 0)
			return;

		c.Unread = 0;
		c.FirstUnreadId = null;
		this.Touch();
		this.ConversationChanged?.Invoke(this, c);
	}

	/// <summary>Marks a message, and everything after it, as not read.</summary>
	public void MarkUnreadFrom(Conversation c, string messageId)
	{
		if (!c.MarkUnreadFrom(messageId))
			return;

		this.Touch();
		this.ConversationChanged?.Invoke(this, c);
	}

	/// <summary>Mutes a person or room until a time, for good (<see cref="DateTime.MaxValue"/>), or not at all (null).</summary>
	public void SetMuted(Conversation c, DateTime? until)
	{
		c.MutedUntil = until;
		this.Touch();
		this.ConversationChanged?.Invoke(this, c);
	}

	/// <summary>Chooses what a room may interrupt you for.</summary>
	public void SetRoomNotify(Conversation c, RoomNotify notify)
	{
		c.Notify = notify;
		this.Touch();
		this.ConversationChanged?.Invoke(this, c);
	}

	/// <summary>Unread messages in everything that is not muted.</summary>
	public int UnreadTotal
	{
		get
		{
			DateTime now = DateTime.Now;
			return this.conversations.Values.Where(c => !c.IsMuted(now)).Sum(c => c.Unread);
		}
	}

	// ---- Your own status -------------------------------------------------------------------

	/// <summary>What contacts see: online, away or do not disturb.</summary>
	public Availability OwnAvailability { get; private set; } = Availability.Online;

	/// <summary>Shows your status to the contacts of every connected account, now and when accounts connect.</summary>
	public async Task SetOwnAvailabilityAsync(Availability availability)
	{
		this.OwnAvailability = availability;
		foreach (AccountChat a in this.attached.Values)
			await ApplyAvailabilityAsync(a.Client, availability);
	}

	private static async Task ApplyAvailabilityAsync(XmppClient client, Availability availability)
	{
		try
		{
			if (client.State == XmppState.Connected)
				await client.SetPresenceAsync(availability);
		}
		catch (Exception)
		{
			// The next connect sets it again.
		}
	}

	/// <summary>Sends a Markdown chat message to a contact.</summary>
	public async Task<ChatMessage> SendAsync(Guid accountId, string bareJid, string markdown)
	{
		Conversation c = this.Get(accountId, bareJid);
		string id = Guid.NewGuid().ToString("N");
		ChatMessage m = new()
		{
			Id = id,
			MessageId = id,
			Direction = ChatDirection.Out,
			Timestamp = DateTime.Now,
			Markdown = markdown,
			Text = markdown,
		};
		c.Add(m);

		try
		{
			bool encrypted = await this.DeliverAsync(accountId, bareJid, c.IsRoom, id, MarkdownXml(markdown), markdown);
			if (encrypted)
			{
				m = m with { Encrypted = true };
				c.Replace(m);
			}
		}
		catch (Exception ex)
		{
			m = m with { Error = ex.Message };
			c.Replace(m);
		}

		this.Touch();
		this.ConversationChanged?.Invoke(this, c);
		return m;
	}

	/// <summary>
	/// Changes the text of a message you sent (XEP-0308). The other side sees the change when
	/// their app understands corrections; an app that does not shows it as a new message.
	/// Returns the message as it is now, or null when it is not one that can be changed.
	/// Throws when the change could not be sent, and leaves the message as it was.
	/// </summary>
	public async Task<ChatMessage?> EditAsync(Conversation c, string id, string markdown)
	{
		ChatMessage? m = c.Message(id);
		if (m is null || m.Direction != ChatDirection.Out || m.MessageId is null || m.Deleted is not null)
			return null;

		string xml = MarkdownXml(markdown) + "<replace id=\"" + SecurityElement.Escape(m.MessageId) + "\" xmlns=\"urn:xmpp:message-correct:0\"/>";
		await this.DeliverAsync(c.AccountId, c.BareJid, c.IsRoom, Guid.NewGuid().ToString("N"), xml, markdown);
		ChatMessage changed = m with { Markdown = markdown, Text = markdown, Corrected = DateTime.Now };
		c.Replace(changed);
		this.Touch();
		this.ConversationChanged?.Invoke(this, c);
		return changed;
	}

	/// <summary>What the other side reads in place of a message that was taken back, when their app does not hide it.</summary>
	public const string DeletedText = "This message was deleted.";

	/// <summary>
	/// Deletes a message. One you wrote is taken back for everyone (XEP-0424, and as a
	/// correction for apps that only know those): its text goes, here and there. One somebody
	/// else wrote is only removed from your own history. Throws when a message of yours could
	/// not be taken back, and leaves it as it was.
	/// </summary>
	public async Task DeleteAsync(Conversation c, string id)
	{
		ChatMessage? m = c.Message(id);
		if (m is null)
			return;

		if (m.Direction == ChatDirection.Out && m.MessageId is not null && m.Deleted is null && m.Error is null)
		{
			string of = SecurityElement.Escape(m.MessageId);
			string xml = MarkdownXml("*" + DeletedText + "*") +
				"<replace id=\"" + of + "\" xmlns=\"urn:xmpp:message-correct:0\"/>" +
				"<retract id=\"" + of + "\" xmlns=\"urn:xmpp:message-retract:1\"/>" +
				"<fallback xmlns=\"urn:xmpp:fallback:0\" for=\"urn:xmpp:message-retract:1\"/>";
			await this.DeliverAsync(c.AccountId, c.BareJid, c.IsRoom, Guid.NewGuid().ToString("N"), xml, DeletedText);
			c.Retract(m.MessageId, DateTime.Now);
		}
		else
			c.Remove(id);

		this.Touch();
		this.ConversationChanged?.Invoke(this, c);
	}

	/// <summary>What is about to be sent, for tests that stand in for the connection.</summary>
	internal sealed record Delivery(Guid AccountId, string Jid, bool Room, string Id, string CustomXml, string Body);

	/// <summary>Set by tests: takes what would be sent in place of the connection. Returns true for "went encrypted".</summary>
	internal Func<Delivery, Task<bool>>? Deliver { get; set; }

	/// <summary>
	/// Sends a message to a contact, end-to-end encrypted to every resource that published keys,
	/// or in the clear to the bare JID when none did; to a room as group chat. Returns true when
	/// it went encrypted.
	/// </summary>
	private async Task<bool> DeliverAsync(Guid accountId, string jid, bool room, string id, string customXml, string body)
	{
		if (this.Deliver is Func<Delivery, Task<bool>> stand)
			return await stand(new Delivery(accountId, jid, room, id, customXml, body));

		AccountChat a = this.ChatOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		XmppClient client = a.Client;
		if (client.State != XmppState.Connected)
			throw new InvalidOperationException("The account is not connected.");

		if (room)
		{
			await client.SendMessage(QoSLevel.Unacknowledged, MessageType.GroupChat, id, jid, customXml, body,
				string.Empty, string.Empty, string.Empty, string.Empty, null, null);
			return false;
		}

		// A Neuron is the far end of its own chat admin: messages to it travel in the clear over the
		// account's TLS connection, like the console's, so its chat bot always gets them.
		bool neuron = this.monitors.For(accountId)?.Neurons.Any(n => n.State is WatchState.Live or WatchState.Subscribing or WatchState.Forbidden && string.Equals(n.BareJid, jid, StringComparison.OrdinalIgnoreCase)) == true;
		List<string> secure = neuron ? [] : this.SecureResources(a, jid);
		if (secure.Count == 0)
		{
			await client.SendMessage(QoSLevel.Unacknowledged, MessageType.Chat, id, jid, customXml, body,
				string.Empty, string.Empty, string.Empty, string.Empty, null, null);
			return false;
		}

		foreach (string full in secure)
		{
			await a.E2e.SendMessage(client, E2ETransmission.AssertE2E, QoSLevel.Unacknowledged, MessageType.Chat,
				id, full, customXml, body, string.Empty, string.Empty, string.Empty, string.Empty, null, null);
		}

		return true;
	}

	internal static string MarkdownXml(string markdown)
		=> "<content xmlns=\"urn:xmpp:content\" type=\"text/markdown\">" + SecurityElement.Escape(markdown) + "</content>";

	// ---- Roster ----------------------------------------------------------------------------

	/// <summary>Contacts of an account, as the server's roster has them now.</summary>
	private readonly List<Contact> previewContacts = [];

	/// <summary>Adds a contact as it is, for previews and UI tests.</summary>
	internal void Preview(Contact c)
	{
		lock (this.previewContacts)
			this.previewContacts.Add(c);

		this.RosterChanged?.Invoke(this, c.AccountId);
	}

	/// <summary>True when the address is one of the Neurons the account watches, contact or not.</summary>
	public bool IsNeuron(Guid accountId, string bareJid)
		=> this.monitors.For(accountId)?.Neurons.Any(n => string.Equals(n.BareJid, bareJid, StringComparison.OrdinalIgnoreCase)) == true;

	public IReadOnlyList<Contact> Contacts(Guid accountId)
	{
		XmppClient? c = this.ClientOf(accountId);
		if (c is null)
		{
			lock (this.previewContacts)
				return [.. this.previewContacts.Where(x => x.AccountId == accountId)];
		}

		NeuronMonitor? m = this.monitors.For(accountId);
		Dictionary<string, string?> neurons = new(StringComparer.OrdinalIgnoreCase);
		foreach (WatchedNeuron n in m?.Neurons ?? [])
			neurons[n.BareJid] = n.OwnName;

		return [.. c.Roster.Select(r =>
		{
			PresenceEventArgs? p = r.HasLastPresence ? r.LastPresence : null;
			bool online = p?.IsOnline == true;
			bool neuron = neurons.TryGetValue(r.BareJid, out string? ownName);
			return new Contact(accountId, r.BareJid, r.Name ?? string.Empty, r.Groups ?? [], r.State, online,
				online ? p!.Availability : Availability.Offline, p?.Status, neuron) { OwnName = ownName };
		})];
	}

	public async Task AddContactAsync(Guid accountId, string bareJid, string? name)
	{
		XmppClient c = this.ClientOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		bareJid = bareJid.Trim();
		await c.AddRosterItem(new RosterItem(bareJid, name?.Trim() ?? string.Empty));
		await c.RequestPresenceSubscription(bareJid);
		this.RosterChanged?.Invoke(this, accountId);
	}

	public async Task RemoveContactAsync(Guid accountId, string bareJid)
	{
		XmppClient c = this.ClientOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		await c.RemoveRosterItem(bareJid);
		this.RosterChanged?.Invoke(this, accountId);
	}

	public async Task RenameContactAsync(Guid accountId, string bareJid, string name)
	{
		if (this.ChangePreview(accountId, bareJid, c => c with { Name = name.Trim() }))
			return;

		XmppClient c = this.ClientOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		RosterItem? item = c.GetRosterItem(bareJid);
		await c.UpdateRosterItem(bareJid, name.Trim(), item?.Groups ?? []);
		this.RosterChanged?.Invoke(this, accountId);
	}

	/// <summary>Puts a contact in these groups, and in no others. The groups live in the account's contact list on its Neuron.</summary>
	public async Task SetGroupsAsync(Guid accountId, string bareJid, IEnumerable<string> groups)
	{
		// A group that exists keeps its spelling, so "on call" joins "On call" and does not start a second group.
		IReadOnlyList<string> known = this.Groups(accountId);
		string[] clean = [.. groups
			.Select(g => g.Trim())
			.Where(g => g.Length > 0)
			.Select(g => known.FirstOrDefault(k => string.Equals(k, g, StringComparison.CurrentCultureIgnoreCase)) ?? g)
			.Distinct(StringComparer.CurrentCultureIgnoreCase)];
		if (this.ChangePreview(accountId, bareJid, c => c with { Groups = clean }))
			return;

		XmppClient client = this.ClientOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		RosterItem? item = client.GetRosterItem(bareJid);
		await client.UpdateRosterItem(bareJid, item?.Name ?? string.Empty, clean);
		this.RosterChanged?.Invoke(this, accountId);
	}

	/// <summary>The groups the account's contacts are in, in alphabetical order.</summary>
	public IReadOnlyList<string> Groups(Guid accountId)
		=> [.. this.Contacts(accountId).SelectMany(c => c.Groups).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];

	/// <summary>Changes a contact that only exists for previews and UI tests. Returns false when it is a real one.</summary>
	private bool ChangePreview(Guid accountId, string bareJid, Func<Contact, Contact> change)
	{
		lock (this.previewContacts)
		{
			int i = this.previewContacts.FindIndex(c => c.AccountId == accountId && string.Equals(c.BareJid, bareJid, StringComparison.OrdinalIgnoreCase));
			if (i < 0 || this.ClientOf(accountId) is not null)
				return false;

			this.previewContacts[i] = change(this.previewContacts[i]);
		}

		this.RosterChanged?.Invoke(this, accountId);
		return true;
	}

	// ---- Subscription requests -------------------------------------------------------------

	public IReadOnlyList<SubscriptionRequest> Requests
	{
		get
		{
			lock (this.requests)
				return [.. this.requests];
		}
	}

	private void Monitors_SubscriptionRequested(object? sender, (Guid AccountId, PresenceEventArgs Presence) e)
	{
		lock (this.requests)
		{
			this.requests.RemoveAll(r => r.AccountId == e.AccountId && string.Equals(r.BareJid, e.Presence.FromBareJID, StringComparison.OrdinalIgnoreCase));
			this.requests.Add(new SubscriptionRequest(e.AccountId, e.Presence.FromBareJID, DateTime.Now)
			{
				Presence = e.Presence,
				AccountLabel = this.accounts.Sessions.Count > 1 ? this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == e.AccountId)?.Profile.ShortLabel : null,
			});
		}

		this.RequestsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Lets the requester see the account's presence, and asks to see theirs.</summary>
	public async Task AcceptAsync(SubscriptionRequest r)
	{
		if (r.Presence is not null)
			await r.Presence.Accept();

		XmppClient? c = this.ClientOf(r.AccountId);
		RosterItem? item = c?.GetRosterItem(r.BareJid);
		if (c is not null && (item is null || item.State is SubscriptionState.None or SubscriptionState.From))
			await c.RequestPresenceSubscription(r.BareJid);

		this.Forget(r);
	}

	public async Task DeclineAsync(SubscriptionRequest r)
	{
		if (r.Presence is not null)
			await r.Presence.Decline();

		this.Forget(r);
	}

	/// <summary>Adds a request as it is, for previews and UI tests.</summary>
	internal void Preview(SubscriptionRequest r)
	{
		lock (this.requests)
			this.requests.Add(r);

		this.RequestsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Adds an invitation as it is, for previews and UI tests.</summary>
	internal void Preview(RoomInvitation i)
	{
		lock (this.invitations)
			this.invitations.Add(i);

		this.RoomsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Receives a message as if it just arrived, for previews and UI tests.</summary>
	internal ChatMessage Preview(Guid accountId, string bareJid, string text, string? senderLabel = null, bool room = false, bool mention = false)
	{
		Conversation c = room ? this.GetRoom(accountId, bareJid) : this.Get(accountId, bareJid);
		ChatMessage m = new()
		{
			Id = Guid.NewGuid().ToString("N"),
			MessageId = Guid.NewGuid().ToString("N"),
			Direction = ChatDirection.In,
			Timestamp = DateTime.Now,
			Markdown = text,
			Text = text,
			Sender = room ? senderLabel : null,
			Mention = mention,
		};
		c.Add(m);
		c.CountUnread(m);
		this.ConversationChanged?.Invoke(this, c);
		this.MessageReceived?.Invoke(this, new IncomingMessage(c, m, senderLabel ?? bareJid));
		return m;
	}

	private void Forget(SubscriptionRequest r)
	{
		lock (this.requests)
			this.requests.Remove(r);

		this.RequestsChanged?.Invoke(this, EventArgs.Empty);
		this.RosterChanged?.Invoke(this, r.AccountId);
	}

	// ---- Consoles --------------------------------------------------------------------------

	/// <summary>The chat admin console of a Neuron through an account.</summary>
	public ConsoleSession Console(Guid accountId, string neuronJid)
		=> this.consoles.GetOrAdd(Key(accountId, neuronJid), _ => new ConsoleSession(accountId, neuronJid,
			() => this.monitors.For(accountId)?.Concentrator(neuronJid)?.FullJid,
			async (to, command, thread) =>
			{
				XmppClient c = this.ClientOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
				// The body carries the command as typed; the Markdown element tells the Neuron to answer in Markdown.
				await c.SendMessage(QoSLevel.Unacknowledged, MessageType.Chat, Guid.NewGuid().ToString("N"), to, MarkdownXml(command), command,
					string.Empty, string.Empty, thread, string.Empty, null, null);
			}));

	// ---- Connections -----------------------------------------------------------------------

	private XmppClient? ClientOf(Guid accountId)
		=> this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == accountId)?.Client;

	private AccountChat? ChatOf(Guid accountId)
		=> this.attached.TryGetValue(accountId, out AccountChat? a) && ReferenceEquals(a.Client, this.ClientOf(accountId)) ? a : null;

	private void SyncSessions()
	{
		foreach (AccountSession s in this.accounts.Sessions)
		{
			bool added;
			lock (this.watched)
				added = this.watched.Add(s);

			if (!added)
				continue;

			s.ClientCreated += (_, c) => this.Attach(s.Profile.Id, c);
			if (s.Client is XmppClient existing)
				this.Attach(s.Profile.Id, existing);
		}
	}

	private void Attach(Guid accountId, XmppClient c)
	{
		if (this.attached.TryGetValue(accountId, out AccountChat? old))
		{
			if (ReferenceEquals(old.Client, c))
				return;
			this.Detach(old);
		}

		AccountSession? session = this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == accountId);
		AccountChat a = new(accountId, c, session?.Profile.TrustServerCertificate ?? false);
		this.attached[accountId] = a;
		this.AttachRooms(a);
		c.OnChatMessage += this.Client_OnChatMessage;
		c.OnPresence += this.Client_OnPresence;
		c.OnRosterItemAdded += this.Client_OnRoster;
		c.OnRosterItemUpdated += this.Client_OnRoster;
		c.OnRosterItemRemoved += this.Client_OnRoster;
		c.OnStateChanged += this.Client_OnStateChanged;
	}

	private void Detach(AccountChat a)
	{
		XmppClient c = a.Client;
		this.DetachRooms(a);
		a.Dispose();
		c.OnChatMessage -= this.Client_OnChatMessage;
		c.OnPresence -= this.Client_OnPresence;
		c.OnRosterItemAdded -= this.Client_OnRoster;
		c.OnRosterItemUpdated -= this.Client_OnRoster;
		c.OnRosterItemRemoved -= this.Client_OnRoster;
		c.OnStateChanged -= this.Client_OnStateChanged;
	}

	private Guid? AccountOf(object sender)
	{
		foreach (KeyValuePair<Guid, AccountChat> p in this.attached)
		{
			if (ReferenceEquals(p.Value.Client, sender))
				return p.Key;
		}

		return null;
	}

	private Task Client_OnStateChanged(object sender, XmppState state)
	{
		if (this.AccountOf(sender) is Guid id)
		{
			if (state == XmppState.Connected && this.attached.TryGetValue(id, out AccountChat? a))
				_ = this.OnConnectedAsync(a);

			this.RosterChanged?.Invoke(this, id);
		}
		return Task.CompletedTask;
	}

	/// <summary>Finds the upload and group chat services, and enters the rooms the account was in.</summary>
	private async Task OnConnectedAsync(AccountChat a)
	{
		try
		{
			await a.DiscoverAsync();
		}
		catch (Exception)
		{
			// Without the services, files and rooms are simply not offered.
		}

		if (this.OwnAvailability != Availability.Online)
			await ApplyAvailabilityAsync(a.Client, this.OwnAvailability);

		await this.RejoinRoomsAsync(a);
		this.RosterChanged?.Invoke(this, a.AccountId);
	}

	private Task Client_OnPresence(object sender, PresenceEventArgs e)
	{
		if (this.AccountOf(sender) is Guid id)
			this.RosterChanged?.Invoke(this, id);
		return Task.CompletedTask;
	}

	private Task Client_OnRoster(object sender, RosterItem item)
	{
		if (this.AccountOf(sender) is Guid id)
			this.RosterChanged?.Invoke(this, id);
		return Task.CompletedTask;
	}

	private async Task Client_OnChatMessage(object sender, MessageEventArgs e)
	{
		try
		{
			if (this.AccountOf(sender) is not Guid accountId || sender is not XmppClient client)
				return;

			string bare = e.FromBareJID;
			if (string.IsNullOrEmpty(bare) || string.Equals(bare, client.BareJID, StringComparison.OrdinalIgnoreCase))
				return;

			IncomingNotification? n = NeuronMonitor.ReadNotification(accountId, e);
			if (n is null)
				return;

			// Someone in a room writing to you alone: a conversation of its own, beside the room's.
			bool inRoom = this.Room(accountId, bare) is not null;
			string peer = inRoom ? e.From : bare;
			if (inRoom && !peer.Contains('/'))
				return;

			bool toFull = !string.IsNullOrEmpty(e.To) && e.To.Contains('/');

			if (!inRoom && toFull && this.consoles.TryGetValue(Key(accountId, bare), out ConsoleSession? console) && console.Receive(n, e.ThreadID))
				return;

			// A Neuron's notifications go to the Attention list.
			if (!inRoom && !toFull && this.monitors.For(accountId) is NeuronMonitor m && await m.IsNeuronSenderAsync(e.From))
				return;

			// Taking a message back only ever touches a conversation that is already there.
			if (n.Retracts is string gone)
			{
				if (this.Find(accountId, peer) is Conversation had && had.Retract(gone, n.Timestamp, x => x.Direction == ChatDirection.In))
				{
					this.Touch();
					this.ConversationChanged?.Invoke(this, had);
				}

				return;
			}

			Conversation conv = this.Get(accountId, peer);
			if (inRoom && !string.IsNullOrEmpty(e.Id) && conv.Contains(e.Id))
				return;

			n = WithOutOfBand(n, e);
			ChatMessage? fresh = null;

			// Neurons answer Markdown with Markdown only; keep a plain version for previews.
			string text = string.IsNullOrWhiteSpace(n.Text) ? NotificationClassifier.Plain(n.Markdown) : n.Text;

			if (n.Replaces is string replaces && conv.Correct(replaces, n.Markdown, text, n.Timestamp, x => x.Direction == ChatDirection.In))
			{
				// Corrected in place.
			}
			else
			{
				fresh = new ChatMessage
				{
					Id = Guid.NewGuid().ToString("N"),
					MessageId = n.MessageId,
					Direction = ChatDirection.In,
					Timestamp = n.Timestamp,
					Markdown = n.Markdown,
					Text = text,
					Encrypted = e.UsesE2eEncryption,
				};
				conv.Add(fresh);
				conv.CountUnread(fresh);
			}

			this.Touch();
			this.ConversationChanged?.Invoke(this, conv);

			// Messages that waited on the server while the account was offline are not news any more.
			if (fresh is not null && fresh.Timestamp > DateTime.Now.AddMinutes(-2))
			{
				RosterItem? item = inRoom ? null : client.GetRosterItem(bare);
				string from = inRoom ? PrivateLabel(peer) : string.IsNullOrWhiteSpace(item?.Name) ? bare : item!.Name;
				this.MessageReceived?.Invoke(this, new IncomingMessage(conv, fresh, from));
			}
		}
		catch (Exception)
		{
			// A malformed message must not stop the client's message loop.
		}
	}

	// ---- Persistence -----------------------------------------------------------------------

	/// <summary>Loads stored conversations.</summary>
	public void Load()
	{
		if (this.store is null)
			return;

		(IReadOnlyList<Conversation> conversations, IReadOnlyList<RoomInfo> rooms) = this.store.Load();
		foreach (Conversation c in conversations)
			this.conversations[Key(c.AccountId, c.BareJid)] = c;
		foreach (RoomInfo r in rooms)
			this.rooms[Key(r.AccountId, r.RoomJid)] = r;
	}

	private void Touch()
	{
		if (this.store is null)
			return;

		Interlocked.Exchange(ref this.dirty, 1);
		this.saveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
	}

	private void SaveIfDirty()
	{
		if (this.store is null || Interlocked.Exchange(ref this.dirty, 0) == 0)
			return;

		try
		{
			this.store.Save([.. this.conversations.Values], [.. this.rooms.Values]);
		}
		catch (Exception)
		{
			Interlocked.Exchange(ref this.dirty, 1);
		}
	}

	public ValueTask DisposeAsync()
	{
		this.monitors.SubscriptionRequested -= this.Monitors_SubscriptionRequested;
		this.monitors.NeuronsChanged -= this.Monitors_NeuronsChanged;
		foreach (AccountChat a in this.attached.Values)
			this.Detach(a);

		this.saveTimer.Dispose();
		this.SaveIfDirty();
		return ValueTask.CompletedTask;
	}
}

/// <summary>Keeps conversations between runs.</summary>
public interface IChatStore
{
	(IReadOnlyList<Conversation> Conversations, IReadOnlyList<RoomInfo> Rooms) Load();

	void Save(IReadOnlyList<Conversation> conversations, IReadOnlyList<RoomInfo> rooms);
}

/// <summary>Stores conversations as JSON.</summary>
public sealed class JsonChatStore(string filePath) : IChatStore
{
	private static readonly JsonSerializerOptions options = new()
	{
		Converters = { new JsonStringEnumConverter() },
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly object gate = new();

	private sealed record Stored(Guid AccountId, string BareJid, int Unread, List<ChatMessage> Messages, bool IsRoom = false, string? Subject = null,
		RoomNotify Notify = RoomNotify.Mentions, DateTime? MutedUntil = null, string? FirstUnreadId = null);
	private sealed record StoredRoom(Guid AccountId, string RoomJid, string NickName, string? Password);
	private sealed record StoredFile(List<Stored> Conversations, List<StoredRoom> Rooms);

	public (IReadOnlyList<Conversation> Conversations, IReadOnlyList<RoomInfo> Rooms) Load()
	{
		lock (this.gate)
		{
			if (!File.Exists(filePath))
				return ([], []);

			try
			{
				string json = File.ReadAllText(filePath);
				StoredFile file = json.TrimStart().StartsWith('[')
					? new StoredFile(JsonSerializer.Deserialize<List<Stored>>(json, options) ?? [], [])
					: JsonSerializer.Deserialize<StoredFile>(json, options) ?? new StoredFile([], []);

				List<Conversation> result = [];
				foreach (Stored s in file.Conversations ?? [])
				{
					Conversation c = new(s.AccountId, s.BareJid)
					{
						Unread = s.Unread,
						IsRoom = s.IsRoom,
						Subject = s.Subject,
						Notify = s.Notify,
						MutedUntil = s.MutedUntil,
						FirstUnreadId = s.FirstUnreadId,
					};
					foreach (ChatMessage m in s.Messages)
						c.Add(m);
					result.Add(c);
				}

				List<RoomInfo> rooms = [.. (file.Rooms ?? []).Select(r => new RoomInfo(r.AccountId, r.RoomJid, r.NickName) { Password = r.Password })];
				return (result, rooms);
			}
			catch (JsonException)
			{
				File.Copy(filePath, filePath + ".damaged", true);
				return ([], []);
			}
		}
	}

	public void Save(IReadOnlyList<Conversation> conversations, IReadOnlyList<RoomInfo> rooms)
	{
		lock (this.gate)
		{
			string? folder = Path.GetDirectoryName(filePath);
			if (!string.IsNullOrEmpty(folder))
				Directory.CreateDirectory(folder);

			StoredFile file = new(
				[.. conversations.Where(c => c.Messages.Count > 0 || c.IsRoom || c.MutedUntil is not null)
					.Select(c => new Stored(c.AccountId, c.BareJid, c.Unread, [.. c.Messages], c.IsRoom, c.Subject, c.Notify, c.MutedUntil, c.FirstUnreadId))],
				[.. rooms.Select(r => new StoredRoom(r.AccountId, r.RoomJid, r.NickName, r.Password))]);
			string tmp = filePath + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(file, options));
			File.Move(tmp, filePath, true);
		}
	}
}
