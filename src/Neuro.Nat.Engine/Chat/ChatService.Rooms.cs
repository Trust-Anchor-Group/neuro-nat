using System.Collections.Concurrent;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.MUC;

namespace Neuro.Nat.Engine.Chat;

public sealed partial class ChatService
{
	private readonly ConcurrentDictionary<string, RoomInfo> rooms = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<RoomInvitation> invitations = [];

	/// <summary>Raised when rooms, their occupants or invitations change. Any thread.</summary>
	public event EventHandler? RoomsChanged;

	public IReadOnlyList<RoomInfo> Rooms => [.. this.rooms.Values];

	/// <summary>Adds a room you are in, with the people in it, for previews and UI tests.</summary>
	internal RoomInfo PreviewRoom(Guid accountId, string roomJid, string nickName, params string[] others)
	{
		RoomInfo room = this.rooms.GetOrAdd(Key(accountId, roomJid), _ => new RoomInfo(accountId, roomJid, nickName));
		room.Joined = true;
		lock (room.occupants)
		{
			room.occupants[nickName] = new RoomOccupant(nickName, "Participant", "Member", null);
			foreach (string o in others)
				room.occupants[o] = new RoomOccupant(o, "Participant", "Member", null);
		}

		this.GetRoom(accountId, roomJid);
		this.RoomsChanged?.Invoke(this, EventArgs.Empty);
		return room;
	}

	/// <summary>Puts someone in a room as they are, or changes them, for previews and UI tests.</summary>
	internal void Preview(Guid accountId, string roomJid, RoomOccupant occupant)
	{
		if (this.Room(accountId, roomJid) is not RoomInfo room)
			return;

		lock (room.occupants)
			room.occupants[occupant.NickName] = occupant;

		this.RoomsChanged?.Invoke(this, EventArgs.Empty);
	}

	public RoomInfo? Room(Guid accountId, string roomJid)
		=> this.rooms.TryGetValue(Key(accountId, roomJid), out RoomInfo? r) ? r : null;

	/// <summary>Something asked of a room, for tests that stand in for the connection.</summary>
	internal sealed record RoomOperation(string What, Guid AccountId, string RoomJid, string? Who = null, string? Text = null);

	/// <summary>
	/// Set by tests and previews: takes what would be asked of the room in place of the
	/// connection. What it returns is the answer, for the operations that have one.
	/// </summary>
	internal Func<RoomOperation, Task<object?>>? RoomStandIn { get; set; }

	/// <summary>Hands an operation to the stand-in, when there is one, and shows its effect on the room kept here.</summary>
	private async Task<(bool Done, object? Result)> StandInAsync(string what, Guid accountId, string roomJid, string? who = null, string? text = null)
	{
		if (this.RoomStandIn is not Func<RoomOperation, Task<object?>> standIn)
			return (false, null);

		RoomInfo room = this.Room(accountId, roomJid) ?? throw new InvalidOperationException("The account is not in that room.");
		object? result = await standIn(new RoomOperation(what, accountId, roomJid, who, text));

		switch (what)
		{
			case "remove":
				lock (room.occupants)
					room.occupants.Remove(who!);
				break;

			case "ban":
				lock (room.occupants)
				{
					foreach (string nick in room.occupants.Values.Where(o => string.Equals(o.BareJid, who, StringComparison.OrdinalIgnoreCase)).Select(o => o.NickName).ToArray())
						room.occupants.Remove(nick);
				}

				break;

			case "role":
				lock (room.occupants)
				{
					if (room.occupants.TryGetValue(who!, out RoomOccupant? o))
						room.occupants[who!] = o with { Role = text };
				}

				break;

			case "subject":
				this.GetRoom(accountId, roomJid).Subject = text;
				break;

			case "delete":
				this.rooms.TryRemove(Key(accountId, roomJid), out _);
				this.conversations.TryRemove(Key(accountId, roomJid), out _);
				break;
		}

		this.RoomsChanged?.Invoke(this, EventArgs.Empty);
		return (true, result);
	}

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
		if ((await this.StandInAsync("invite", accountId, roomJid, bareJid.Trim(), reason)).Done)
			return;

		AccountChat a = this.ChatOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		RoomInfo room = this.Room(accountId, roomJid) ?? throw new InvalidOperationException("The account is not in that room.");
		// A direct invitation (XEP-0249) goes from person to person, so it works whatever the room allows.
		await a.Muc.InviteDirect(room.RoomId, room.Domain, bareJid.Trim(), reason ?? string.Empty, string.Empty, room.Password ?? string.Empty);
	}

	public async Task SetSubjectAsync(Guid accountId, string roomJid, string subject)
	{
		if ((await this.StandInAsync("subject", accountId, roomJid, null, subject)).Done)
			return;

		AccountChat a = this.ChatOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		RoomInfo room = this.Room(accountId, roomJid) ?? throw new InvalidOperationException("The account is not in that room.");
		await a.Muc.ChangeRoomSubject(room.RoomId, room.Domain, subject);
	}

	// ---- Running a room ---------------------------------------------------------------------

	private (AccountChat Chat, RoomInfo Room) InRoom(Guid accountId, string roomJid)
	{
		AccountChat a = this.ChatOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		RoomInfo room = this.Room(accountId, roomJid) ?? throw new InvalidOperationException("The account is not in that room.");
		return (a, room);
	}

	private static void Check(Waher.Networking.XMPP.Events.IqResultEventArgs e, string otherwise)
	{
		if (!e.Ok)
			throw new InvalidOperationException(string.IsNullOrWhiteSpace(e.ErrorText) ? otherwise : e.ErrorText);
	}

	private static readonly TimeSpan roomWait = TimeSpan.FromSeconds(20);

	/// <summary>Puts someone out of the room. They can come back in. Needs a moderator.</summary>
	public async Task RemoveFromRoomAsync(Guid accountId, string roomJid, string nickName, string? reason = null)
	{
		if ((await this.StandInAsync("remove", accountId, roomJid, nickName, reason)).Done)
			return;

		(AccountChat a, RoomInfo room) = this.InRoom(accountId, roomJid);
		Check(await a.Muc.KickAsync(room.RoomId, room.Domain, nickName, reason ?? string.Empty).WaitAsync(roomWait), "The room did not remove " + nickName + ".");
	}

	/// <summary>Puts someone out of the room for good: their address is not let in again. Needs an admin or owner.</summary>
	public async Task BanAsync(Guid accountId, string roomJid, string bareJid, string? reason = null)
	{
		if ((await this.StandInAsync("ban", accountId, roomJid, bareJid.Trim(), reason)).Done)
			return;

		(AccountChat a, RoomInfo room) = this.InRoom(accountId, roomJid);
		Check(await a.Muc.BanAsync(room.RoomId, room.Domain, bareJid.Trim(), reason ?? string.Empty).WaitAsync(roomWait), "The room did not ban " + bareJid + ".");
	}

	/// <summary>Lets a banned address in again.</summary>
	public async Task UnbanAsync(Guid accountId, string roomJid, string bareJid)
	{
		if ((await this.StandInAsync("unban", accountId, roomJid, bareJid.Trim())).Done)
			return;

		(AccountChat a, RoomInfo room) = this.InRoom(accountId, roomJid);
		Check(await a.Muc.ConfigureOccupantAsync(room.RoomId, room.Domain, bareJid.Trim(), Affiliation.None, string.Empty).WaitAsync(roomWait), "The room did not lift the ban on " + bareJid + ".");
	}

	/// <summary>The addresses that are banned from the room. Needs an admin or owner.</summary>
	public async Task<IReadOnlyList<string>> BannedAsync(Guid accountId, string roomJid)
	{
		if (await this.StandInAsync("banned", accountId, roomJid) is (true, object answer))
			return answer as IReadOnlyList<string> ?? [];

		(AccountChat a, RoomInfo room) = this.InRoom(accountId, roomJid);
		OccupantListEventArgs e = await a.Muc.GetBannedOccupantsAsync(room.RoomId, room.Domain).WaitAsync(roomWait);
		Check(e, "The room did not give its banned list.");
		return [.. (e.Occupants ?? []).Select(o => o.Jid).Where(j => !string.IsNullOrEmpty(j)).Order(StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>Changes what someone may do while they are in the room. Needs a moderator; making moderators needs an admin or owner.</summary>
	public async Task SetRoleAsync(Guid accountId, string roomJid, string nickName, RoomRole role, string? reason = null)
	{
		if ((await this.StandInAsync("role", accountId, roomJid, nickName, role.ToString())).Done)
			return;

		(AccountChat a, RoomInfo room) = this.InRoom(accountId, roomJid);
		Role to = role switch
		{
			RoomRole.Moderator => Role.Moderator,
			RoomRole.Visitor => Role.Visitor,
			_ => Role.Participant,
		};
		Check(await a.Muc.ConfigureOccupantAsync(room.RoomId, room.Domain, nickName, to, reason ?? string.Empty).WaitAsync(roomWait), "The room did not change what " + nickName + " may do.");
	}

	/// <summary>Asks the moderators to let this account write in a room where it may only read.</summary>
	public async Task AskToSpeakAsync(Guid accountId, string roomJid)
	{
		if ((await this.StandInAsync("speak", accountId, roomJid)).Done)
			return;

		(AccountChat a, RoomInfo room) = this.InRoom(accountId, roomJid);
		await a.Muc.RequestVoice(room.RoomId, room.Domain);
	}

	/// <summary>The room's settings, as the form its service gives. Needs an owner.</summary>
	public async Task<Waher.Networking.XMPP.DataForms.DataForm> RoomSettingsAsync(Guid accountId, string roomJid)
	{
		if (await this.StandInAsync("settings", accountId, roomJid) is (true, Waher.Networking.XMPP.DataForms.DataForm given))
			return given;

		(AccountChat a, RoomInfo room) = this.InRoom(accountId, roomJid);
		Waher.Networking.XMPP.DataForms.DataForm? form = await a.Muc.GetRoomConfigurationAsync(room.RoomId, room.Domain).WaitAsync(roomWait);
		return form ?? throw new InvalidOperationException("The room did not give its settings. Only an owner of the room can change them.");
	}

	/// <summary>Saves the settings form from <see cref="RoomSettingsAsync"/>.</summary>
	public async Task SaveRoomSettingsAsync(Guid accountId, string roomJid, Waher.Networking.XMPP.DataForms.DataForm form)
	{
		if ((await this.StandInAsync("save", accountId, roomJid)).Done)
			return;

		(AccountChat a, _) = this.InRoom(accountId, roomJid);
		await a.Muc.SubmitConfigurationForm(form);
	}

	/// <summary>Deletes the room for everyone in it. Needs an owner.</summary>
	public async Task DeleteRoomAsync(Guid accountId, string roomJid, string? reason = null)
	{
		if ((await this.StandInAsync("delete", accountId, roomJid, null, reason)).Done)
			return;

		(AccountChat a, RoomInfo room) = this.InRoom(accountId, roomJid);
		Check(await a.Muc.DestroyRoomAsync(room.RoomId, room.Domain, reason ?? string.Empty, string.Empty).WaitAsync(roomWait), "The room was not deleted.");
		this.rooms.TryRemove(Key(accountId, roomJid), out _);
		this.conversations.TryRemove(Key(accountId, roomJid), out _);
		this.Touch();
		this.RoomsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// The address of a conversation with one person in a room: the room's address, a slash and
	/// their nickname there. It is a conversation of its own, and it lasts while both are in.
	/// </summary>
	public static string PrivateJid(string roomJid, string nickName) => roomJid + "/" + nickName;

	/// <summary>True for the address of a conversation with one person in a room.</summary>
	public static bool IsPrivate(string jid) => jid.Contains('/');

	/// <summary>"kim in ops-team", from the address of a conversation with one person in a room.</summary>
	public static string PrivateLabel(string privateJid)
	{
		int slash = privateJid.IndexOf('/');
		if (slash < 0)
			return privateJid;

		return privateJid[(slash + 1)..] + " in " + privateJid[..slash].Split('@')[0];
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
		a.Muc.PrivateMessageReceived += this.Muc_PrivateMessageReceived;
		a.Muc.RoomDestroyed += this.Muc_RoomDestroyed;
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
		a.Muc.PrivateMessageReceived -= this.Muc_PrivateMessageReceived;
		a.Muc.RoomDestroyed -= this.Muc_RoomDestroyed;
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

	/// <summary>Someone in a room writes to this account alone. Goes the same way as any chat message; a copy that already arrived is dropped there.</summary>
	private Task Muc_PrivateMessageReceived(object sender, RoomOccupantMessageEventArgs e)
		=> this.AccountOfMuc(sender) is Guid accountId && this.ClientOf(accountId) is XmppClient client ? this.Client_OnChatMessage(client, e) : Task.CompletedTask;

	/// <summary>An owner deleted the room: it is gone here too, with a line in its place saying so.</summary>
	private Task Muc_RoomDestroyed(object sender, UserPresenceEventArgs e)
	{
		if (this.AccountOfMuc(sender) is Guid accountId && this.Room(accountId, (e.RoomId + "@" + e.Domain).ToLowerInvariant()) is RoomInfo room)
		{
			room.Joined = false;
			room.Error = "The room was deleted" + (string.IsNullOrWhiteSpace(e.Reason) ? "." : ": " + e.Reason);
			lock (room.occupants)
				room.occupants.Clear();

			this.RoomsChanged?.Invoke(this, EventArgs.Empty);
		}

		return Task.CompletedTask;
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

		// In a room anyone can send a correction: only the one who wrote a message may change it or take it back.
		bool WrittenBy(ChatMessage m) => mine ? m.Direction == ChatDirection.Out
			: m.Direction == ChatDirection.In && string.Equals(m.Sender, nick, StringComparison.OrdinalIgnoreCase);

		if (n.Retracts is string gone)
		{
			if (conv.Retract(gone, n.Timestamp, WrittenBy))
			{
				this.Touch();
				this.ConversationChanged?.Invoke(this, conv);
			}

			return Task.CompletedTask;
		}

		if (n.Replaces is string replaces && conv.Correct(replaces, n.Markdown, n.Text, n.Timestamp, WrittenBy))
		{
		}
		else if (n.Replaces is not null && mine)
		{
			// The room sending back a change of ours that is already made.
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
