using System.Collections.Concurrent;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.MUC;

namespace Neuro.AA.Engine.Chat;

public sealed partial class ChatService
{
	private readonly ConcurrentDictionary<string, RoomInfo> rooms = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<RoomInvitation> invitations = [];

	/// <summary>Raised when rooms, their occupants or invitations change. Any thread.</summary>
	public event EventHandler? RoomsChanged;

	public IReadOnlyList<RoomInfo> Rooms => [.. this.rooms.Values];

	public RoomInfo? Room(Guid accountId, string roomJid)
		=> this.rooms.TryGetValue(Key(accountId, roomJid), out RoomInfo? r) ? r : null;

	public IReadOnlyList<RoomInvitation> Invitations
	{
		get
		{
			lock (this.invitations)
				return [.. this.invitations];
		}
	}

	/// <summary>The group chat service of an account's server, when it has one.</summary>
	public string? MucService(Guid accountId) => this.ChatOf(accountId)?.MucService;

	/// <summary>The conversation of a room.</summary>
	public Conversation GetRoom(Guid accountId, string roomJid)
		=> this.conversations.GetOrAdd(Key(accountId, roomJid), _ => new Conversation(accountId, roomJid) { IsRoom = true });

	/// <summary>
	/// Enters a room; creates it when it does not exist. The room is entered again whenever the
	/// account connects, until it is left.
	/// </summary>
	public async Task<RoomInfo> JoinRoomAsync(Guid accountId, string roomJid, string nickName, string? password = null)
	{
		AccountChat a = this.ChatOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		roomJid = roomJid.Trim().ToLowerInvariant();
		if (!roomJid.Contains('@'))
		{
			if (a.MucService is null)
				throw new InvalidOperationException("Give the room's full address, such as ops@muc.example.com.");
			roomJid += "@" + a.MucService;
		}

		RoomInfo room = this.rooms.GetOrAdd(Key(accountId, roomJid), _ => new RoomInfo(accountId, roomJid, nickName));
		room.NickName = nickName;
		room.Password = string.IsNullOrEmpty(password) ? null : password;
		this.GetRoom(accountId, roomJid);

		await this.EnterAsync(a, room);
		if (!room.Joined)
			throw new InvalidOperationException(room.Error ?? "The room did not let the account in.");

		this.Touch();
		return room;
	}

	private async Task EnterAsync(AccountChat a, RoomInfo room)
	{
		try
		{
			UserPresenceEventArgs e = await (room.Password is null
				? a.Muc.EnterRoomAsync(room.RoomId, room.Domain, room.NickName)
				: a.Muc.EnterRoomAsync(room.RoomId, room.Domain, room.NickName, room.Password)).WaitAsync(TimeSpan.FromSeconds(30));

			if (!e.Ok)
			{
				room.Joined = false;
				room.Error = string.IsNullOrEmpty(e.ErrorText) ? "The room did not let the account in." : e.ErrorText;
			}
			else
			{
				// A new room stays locked until configured; accept the default configuration.
				if (e.HasStatus(MucStatus.Created))
					await a.Muc.CreateInstantRoomAsync(room.RoomId, room.Domain);

				room.Joined = true;
				room.Error = null;

				lock (room.occupants)
					room.occupants[room.NickName] = new RoomOccupant(room.NickName, e.Role?.ToString(), e.Affiliation?.ToString(), a.Client.FullJID);
			}
		}
		catch (Exception ex)
		{
			room.Joined = false;
			room.Error = ex.Message;
		}

		this.RoomsChanged?.Invoke(this, EventArgs.Empty);
	}

	public async Task LeaveRoomAsync(Guid accountId, string roomJid)
	{
		if (this.rooms.TryRemove(Key(accountId, roomJid), out RoomInfo? room) && this.ChatOf(accountId) is AccountChat a && room.Joined)
		{
			try
			{
				await a.Muc.LeaveRoomAsync(room.RoomId, room.Domain, room.NickName).WaitAsync(TimeSpan.FromSeconds(15));
			}
			catch (Exception)
			{
				// Gone either way.
			}
		}

		this.conversations.TryRemove(Key(accountId, roomJid), out _);
		this.Touch();
		this.RoomsChanged?.Invoke(this, EventArgs.Empty);
	}

	public async Task InviteAsync(Guid accountId, string roomJid, string bareJid, string? reason = null)
	{
		AccountChat a = this.ChatOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		RoomInfo room = this.Room(accountId, roomJid) ?? throw new InvalidOperationException("The account is not in that room.");
		// A direct invitation (XEP-0249) goes from person to person, so it works whatever the room allows.
		await a.Muc.InviteDirect(room.RoomId, room.Domain, bareJid.Trim(), reason ?? string.Empty, string.Empty, room.Password ?? string.Empty);
	}

	public async Task SetSubjectAsync(Guid accountId, string roomJid, string subject)
	{
		AccountChat a = this.ChatOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		RoomInfo room = this.Room(accountId, roomJid) ?? throw new InvalidOperationException("The account is not in that room.");
		await a.Muc.ChangeRoomSubject(room.RoomId, room.Domain, subject);
	}

	public async Task AcceptInvitationAsync(RoomInvitation invitation, string nickName)
	{
		lock (this.invitations)
			this.invitations.Remove(invitation);

		await this.JoinRoomAsync(invitation.AccountId, invitation.RoomJid, nickName, invitation.Password);
	}

	public async Task DeclineInvitationAsync(RoomInvitation invitation)
	{
		lock (this.invitations)
			this.invitations.Remove(invitation);

		if (this.ChatOf(invitation.AccountId) is AccountChat a)
		{
			string[] parts = invitation.RoomJid.Split('@', 2);
			await a.Muc.DeclineInvitation(parts[0], parts.Length > 1 ? parts[1] : string.Empty, invitation.From, string.Empty, string.Empty);
		}

		this.RoomsChanged?.Invoke(this, EventArgs.Empty);
	}

	// ---- Wiring ----------------------------------------------------------------------------

	private void AttachRooms(AccountChat a)
	{
		a.Muc.RoomOccupantMessage += this.Muc_RoomOccupantMessage;
		a.Muc.RoomMessage += this.Muc_RoomMessage;
		a.Muc.RoomSubject += this.Muc_RoomSubject;
		a.Muc.OccupantPresence += this.Muc_OccupantPresence;
		a.Muc.RoomPresence += this.Muc_OccupantPresence;
		a.Muc.RoomInvitationReceived += this.Muc_RoomInvitationReceived;
		a.Muc.DirectInvitationReceived += this.Muc_DirectInvitationReceived;
	}

	private void DetachRooms(AccountChat a)
	{
		a.Muc.RoomOccupantMessage -= this.Muc_RoomOccupantMessage;
		a.Muc.RoomMessage -= this.Muc_RoomMessage;
		a.Muc.RoomSubject -= this.Muc_RoomSubject;
		a.Muc.OccupantPresence -= this.Muc_OccupantPresence;
		a.Muc.RoomPresence -= this.Muc_OccupantPresence;
		a.Muc.RoomInvitationReceived -= this.Muc_RoomInvitationReceived;
		a.Muc.DirectInvitationReceived -= this.Muc_DirectInvitationReceived;
	}

	private async Task RejoinRoomsAsync(AccountChat a)
	{
		foreach (RoomInfo room in this.rooms.Values.Where(r => r.AccountId == a.AccountId).ToList())
		{
			lock (room.occupants)
				room.occupants.Clear();
			await this.EnterAsync(a, room);
		}
	}

	private Guid? AccountOfMuc(object sender)
	{
		foreach (KeyValuePair<Guid, AccountChat> p in this.attached)
		{
			if (ReferenceEquals(p.Value.Muc, sender))
				return p.Key;
		}

		return null;
	}

	private Task Muc_RoomOccupantMessage(object sender, RoomOccupantMessageEventArgs e)
		=> this.RoomMessageAsync(sender, e, e.NickName);

	private Task Muc_RoomMessage(object sender, RoomMessageEventArgs e)
		=> this.RoomMessageAsync(sender, e, null);

	private Task RoomMessageAsync(object sender, RoomMessageEventArgs e, string? nick)
	{
		if (this.AccountOfMuc(sender) is not Guid accountId)
			return Task.CompletedTask;

		string roomJid = (e.RoomId + "@" + e.Domain).ToLowerInvariant();
		Conversation conv = this.GetRoom(accountId, roomJid);
		RoomInfo? room = this.Room(accountId, roomJid);

		// The room sends our own messages back; they are already in the conversation.
		if (!string.IsNullOrEmpty(e.Id) && conv.Contains(e.Id))
			return Task.CompletedTask;

		Attention.IncomingNotification? n = Events.NeuronMonitor.ReadNotification(accountId, e);
		if (n is null)
			return Task.CompletedTask;

		n = WithOutOfBand(n, e);
		bool mine = room is not null && string.Equals(nick, room.NickName, StringComparison.OrdinalIgnoreCase);
		ChatMessage? fresh = null;

		if (n.Replaces is string replaces && conv.Correct(replaces, n.Markdown, n.Text, n.Timestamp))
		{
		}
		else
		{
			string text = string.IsNullOrWhiteSpace(n.Text) ? Attention.NotificationClassifier.Plain(n.Markdown) : n.Text;
			ChatMessage m = new()
			{
				Id = Guid.NewGuid().ToString("N"),
				MessageId = n.MessageId,
				Direction = mine ? ChatDirection.Out : ChatDirection.In,
				Timestamp = n.Timestamp,
				Markdown = n.Markdown,
				Text = text,
				Sender = nick,
				Mention = !mine && room is not null && Mentions(text, room.NickName),
			};
			conv.Add(m);

			// History the room replays on entry is not news.
			if (!mine && n.Timestamp > DateTime.Now.AddMinutes(-2))
			{
				conv.CountUnread(m);
				fresh = m;
			}
		}

		this.Touch();
		this.ConversationChanged?.Invoke(this, conv);
		if (fresh is not null)
			this.MessageReceived?.Invoke(this, new IncomingMessage(conv, fresh, string.IsNullOrEmpty(nick) ? conv.BareJid : nick));
		return Task.CompletedTask;
	}

	/// <summary>True when a message names a nickname as a word of its own, with or without an @.</summary>
	internal static bool Mentions(string text, string nickName)
	{
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(nickName))
			return false;

		int at = 0;
		while ((at = text.IndexOf(nickName, at, StringComparison.OrdinalIgnoreCase)) >= 0)
		{
			bool startOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
			int end = at + nickName.Length;
			bool endOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
			if (startOk && endOk)
				return true;
			at = end;
		}

		return false;
	}

	private Task Muc_RoomSubject(object sender, RoomOccupantMessageEventArgs e)
	{
		if (this.AccountOfMuc(sender) is Guid accountId)
		{
			Conversation conv = this.GetRoom(accountId, (e.RoomId + "@" + e.Domain).ToLowerInvariant());
			conv.Subject = e.Subject;
			this.Touch();
			this.ConversationChanged?.Invoke(this, conv);
		}

		return Task.CompletedTask;
	}

	private Task Muc_OccupantPresence(object sender, UserPresenceEventArgs e)
	{
		if (this.AccountOfMuc(sender) is Guid accountId && this.Room(accountId, (e.RoomId + "@" + e.Domain).ToLowerInvariant()) is RoomInfo room)
		{
			lock (room.occupants)
			{
				if (e.Type == PresenceType.Unavailable)
					room.occupants.Remove(e.NickName);
				else
					room.occupants[e.NickName] = new RoomOccupant(e.NickName, e.Role?.ToString(), e.Affiliation?.ToString(), string.IsNullOrEmpty(e.FullJid) ? null : e.FullJid);
			}

			this.RoomsChanged?.Invoke(this, EventArgs.Empty);
		}

		return Task.CompletedTask;
	}

	private Task Muc_RoomInvitationReceived(object sender, RoomInvitationMessageEventArgs e)
	{
		this.AddInvitation(sender, e.RoomId, e.Domain, XmppClient.GetBareJID(e.InviteFrom), e.Reason, e.Password);
		return Task.CompletedTask;
	}

	private Task Muc_DirectInvitationReceived(object sender, DirectInvitationMessageEventArgs e)
	{
		this.AddInvitation(sender, e.RoomId, e.Domain, e.FromBareJID, e.Reason, e.Password);
		return Task.CompletedTask;
	}

	private void AddInvitation(object sender, string roomId, string domain, string from, string? reason, string? password)
	{
		if (this.AccountOfMuc(sender) is not Guid accountId)
			return;

		string roomJid = (roomId + "@" + domain).ToLowerInvariant();
		if (this.Room(accountId, roomJid)?.Joined == true)
			return;

		lock (this.invitations)
		{
			this.invitations.RemoveAll(i => i.AccountId == accountId && i.RoomJid == roomJid);
			this.invitations.Add(new RoomInvitation(accountId, roomJid, from, string.IsNullOrEmpty(reason) ? null : reason, DateTime.Now)
			{
				Password = string.IsNullOrEmpty(password) ? null : password,
			});
		}

		this.RoomsChanged?.Invoke(this, EventArgs.Empty);
	}
}
