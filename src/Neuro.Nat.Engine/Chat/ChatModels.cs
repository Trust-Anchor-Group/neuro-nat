namespace Neuro.Nat.Engine.Chat;

public enum ChatDirection
{
	In,
	Out,
}

/// <summary>One chat message, sent or received.</summary>
public sealed record ChatMessage
{
	public required string Id { get; init; }

	/// <summary>The XMPP message id, when known. Corrections (XEP-0308) refer to it.</summary>
	public string? MessageId { get; init; }

	public required ChatDirection Direction { get; init; }
	public required DateTime Timestamp { get; init; }
	public required string Markdown { get; init; }
	public string Text { get; init; } = string.Empty;

	/// <summary>When the sender last corrected the message.</summary>
	public DateTime? Corrected { get; init; }

	/// <summary>Set when an outgoing message could not be sent.</summary>
	public string? Error { get; init; }

	/// <summary>Sent or received end-to-end encrypted.</summary>
	public bool Encrypted { get; init; }

	/// <summary>In a room: the nickname of who wrote it.</summary>
	public string? Sender { get; init; }

	/// <summary>In a room: the message names this account's nickname.</summary>
	public bool Mention { get; init; }

	/// <summary>When its writer took the message back. The text is gone; the place it had stays.</summary>
	public DateTime? Deleted { get; init; }
}

/// <summary>What a room may interrupt you for.</summary>
public enum RoomNotify
{
	/// <summary>Only messages that name you. The rest raise the count.</summary>
	Mentions,

	/// <summary>Every message.</summary>
	All,

	/// <summary>Nothing, and its count stays out of the totals.</summary>
	Muted,
}

/// <summary>A message that just arrived from someone else: news, as opposed to history.</summary>
public sealed record IncomingMessage(Conversation Conversation, ChatMessage Message, string SenderLabel);

/// <summary>A conversation with one contact through one account.</summary>
public sealed class Conversation
{
	private readonly List<ChatMessage> messages = [];

	public Conversation(Guid accountId, string bareJid)
	{
		this.AccountId = accountId;
		this.BareJid = bareJid;
	}

	public Guid AccountId { get; }
	public string BareJid { get; }

	/// <summary>A group chat room rather than a person.</summary>
	public bool IsRoom { get; init; }

	/// <summary>The room's subject, for rooms.</summary>
	public string? Subject { get; internal set; }

	/// <summary>Most messages kept per conversation.</summary>
	public static int Capacity { get; set; } = 2000;

	public int Unread { get; internal set; }
	public DateTime LastActivity { get; internal set; }

	/// <summary>The first message that has not been read: where the "New" line goes.</summary>
	public string? FirstUnreadId { get; internal set; }

	/// <summary>For rooms: what may interrupt you.</summary>
	public RoomNotify Notify { get; internal set; }

	/// <summary>No pop-ups or sounds until then. <see cref="DateTime.MaxValue"/> means until you say otherwise.</summary>
	public DateTime? MutedUntil { get; internal set; }

	/// <summary>True while the person or room is muted.</summary>
	public bool IsMuted(DateTime now) => this.Notify == RoomNotify.Muted || (this.MutedUntil is DateTime t && t > now);

	/// <summary>Counts an incoming message as unread, and remembers where reading stopped.</summary>
	internal void CountUnread(ChatMessage m)
	{
		if (this.Unread == 0)
			this.FirstUnreadId = m.Id;
		this.Unread++;
	}

	/// <summary>Marks everything from a message on as unread. Returns false when it is not known.</summary>
	internal bool MarkUnreadFrom(string id)
	{
		lock (this.messages)
		{
			int i = this.messages.FindIndex(m => m.Id == id);
			if (i < 0)
				return false;

			// Start at the first message from someone else at or after the one picked.
			while (i < this.messages.Count && this.messages[i].Direction != ChatDirection.In)
				i++;
			if (i >= this.messages.Count)
				return false;

			this.FirstUnreadId = this.messages[i].Id;
			this.Unread = this.messages.Skip(i).Count(m => m.Direction == ChatDirection.In);
			return true;
		}
	}

	/// <summary>Removes a message. Returns false when it is not known.</summary>
	internal bool Remove(string id)
	{
		lock (this.messages)
			return this.messages.RemoveAll(m => m.Id == id) > 0;
	}

	public IReadOnlyList<ChatMessage> Messages
	{
		get
		{
			lock (this.messages)
				return [.. this.messages];
		}
	}

	public ChatMessage? Last
	{
		get
		{
			lock (this.messages)
				return this.messages.Count == 0 ? null : this.messages[^1];
		}
	}

	internal void Add(ChatMessage m)
	{
		lock (this.messages)
		{
			int i = this.messages.Count;
			while (i > 0 && this.messages[i - 1].Timestamp > m.Timestamp)
				i--;
			this.messages.Insert(i, m);

			if (this.messages.Count > Capacity)
				this.messages.RemoveRange(0, this.messages.Count - Capacity);
		}

		if (m.Timestamp > this.LastActivity)
			this.LastActivity = m.Timestamp;
	}

	/// <summary>
	/// Applies a correction. Returns false when the corrected message is not known, is already
	/// taken back, or (in a room) was written by somebody else than the one correcting it.
	/// </summary>
	internal bool Correct(string messageId, string markdown, string text, DateTime when, Func<ChatMessage, bool>? writtenBy = null)
	{
		lock (this.messages)
		{
			int i = this.messages.FindLastIndex(m => m.MessageId == messageId);
			if (i < 0 || this.messages[i].Deleted is not null || (writtenBy is not null && !writtenBy(this.messages[i])))
				return false;

			this.messages[i] = this.messages[i] with { Markdown = markdown, Text = text, Corrected = when };
			return true;
		}
	}

	/// <summary>
	/// Takes a message back: its text goes, and a line saying so stays in its place. Returns
	/// false when the message is not known or (in a room) was written by somebody else.
	/// </summary>
	internal bool Retract(string messageId, DateTime when, Func<ChatMessage, bool>? writtenBy = null)
	{
		lock (this.messages)
		{
			int i = this.messages.FindLastIndex(m => m.MessageId == messageId);
			if (i < 0 || (writtenBy is not null && !writtenBy(this.messages[i])))
				return false;

			this.messages[i] = this.messages[i] with { Markdown = string.Empty, Text = string.Empty, Deleted = when, Error = null };
			return true;
		}
	}

	/// <summary>A message by the id it has in this app.</summary>
	public ChatMessage? Message(string id)
	{
		lock (this.messages)
			return this.messages.Find(m => m.Id == id);
	}

	internal bool Contains(string messageId)
	{
		lock (this.messages)
			return this.messages.Any(m => m.MessageId == messageId);
	}

	internal void Replace(ChatMessage m)
	{
		lock (this.messages)
		{
			int i = this.messages.FindIndex(x => x.Id == m.Id);
			if (i >= 0)
				this.messages[i] = m;
		}
	}
}

/// <summary>Someone asks to see an account's presence (to be added as a contact).</summary>
public sealed record SubscriptionRequest(Guid AccountId, string BareJid, DateTime Received)
{
	/// <summary>The account that is asked, by its short name. Null when there is only one account.</summary>
	public string? AccountLabel { get; init; }

	internal Waher.Networking.XMPP.Events.PresenceEventArgs? Presence { get; init; }
}

/// <summary>Whether a contact can be reached end-to-end encrypted, and with which key.</summary>
/// <param name="Fingerprint">Short hash of the contact's public key, to compare with what they see.</param>
public sealed record ContactSecurity(bool Encrypted, string? Fingerprint, string? Algorithm, int Resources);

/// <summary>A group chat room joined through an account.</summary>
public sealed class RoomInfo(Guid accountId, string roomJid, string nickName)
{
	public Guid AccountId { get; } = accountId;
	public string RoomJid { get; } = roomJid;
	public string NickName { get; internal set; } = nickName;
	public string? Password { get; internal set; }
	public bool Joined { get; internal set; }
	public string? Error { get; internal set; }

	internal readonly Dictionary<string, RoomOccupant> occupants = new(StringComparer.OrdinalIgnoreCase);

	public IReadOnlyList<RoomOccupant> Occupants
	{
		get
		{
			lock (this.occupants)
				return [.. this.occupants.Values.OrderBy(o => o.NickName, StringComparer.CurrentCultureIgnoreCase)];
		}
	}

	/// <summary>This account in the room, with what it may do there. Null until the room has let it in.</summary>
	public RoomOccupant? Me
	{
		get
		{
			lock (this.occupants)
				return this.occupants.TryGetValue(this.NickName, out RoomOccupant? me) ? me : null;
		}
	}

	public string RoomId => this.RoomJid.Split('@')[0];
	public string Domain => this.RoomJid.Contains('@') ? this.RoomJid[(this.RoomJid.IndexOf('@') + 1)..] : string.Empty;
}

/// <summary>Someone in a room.</summary>
/// <param name="Role">What they may do while they are in: Moderator, Participant or Visitor.</param>
/// <param name="Affiliation">Their standing with the room, in or out of it: Owner, Admin, Member or None.</param>
/// <param name="RealJid">Their own address, when the room shows it.</param>
public sealed record RoomOccupant(string NickName, string? Role, string? Affiliation, string? RealJid)
{
	public bool IsModerator => string.Equals(this.Role, "Moderator", StringComparison.OrdinalIgnoreCase);
	public bool IsVisitor => string.Equals(this.Role, "Visitor", StringComparison.OrdinalIgnoreCase);
	public bool IsOwner => string.Equals(this.Affiliation, "Owner", StringComparison.OrdinalIgnoreCase);
	public bool IsAdmin => this.IsOwner || string.Equals(this.Affiliation, "Admin", StringComparison.OrdinalIgnoreCase);

	/// <summary>Their own address without the resource, when the room shows it.</summary>
	public string? BareJid => string.IsNullOrEmpty(this.RealJid) ? null : this.RealJid.Split('/')[0];
}

/// <summary>What someone may do while they are in a room.</summary>
public enum RoomRole
{
	/// <summary>May read, not write.</summary>
	Visitor,

	/// <summary>May read and write.</summary>
	Participant,

	/// <summary>May also remove people and decide who may write.</summary>
	Moderator,
}

/// <summary>An invitation to a room.</summary>
public sealed record RoomInvitation(Guid AccountId, string RoomJid, string From, string? Reason, DateTime Received)
{
	internal string? Password { get; init; }
}
