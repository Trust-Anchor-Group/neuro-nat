namespace Neuro.AA.Engine.Chat;

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
}

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
	public static int Capacity { get; set; } = 500;

	public int Unread { get; internal set; }
	public DateTime LastActivity { get; internal set; }

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

	/// <summary>Applies a correction. Returns false when the corrected message is not known.</summary>
	internal bool Correct(string messageId, string markdown, string text, DateTime when)
	{
		lock (this.messages)
		{
			int i = this.messages.FindLastIndex(m => m.MessageId == messageId);
			if (i < 0)
				return false;

			this.messages[i] = this.messages[i] with { Markdown = markdown, Text = text, Corrected = when };
			return true;
		}
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
	/// <summary>The account that is asked, by its name in Neuro.AA.</summary>
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

	public string RoomId => this.RoomJid.Split('@')[0];
	public string Domain => this.RoomJid.Contains('@') ? this.RoomJid[(this.RoomJid.IndexOf('@') + 1)..] : string.Empty;
}

/// <summary>Someone in a room.</summary>
public sealed record RoomOccupant(string NickName, string? Role, string? Affiliation, string? RealJid);

/// <summary>An invitation to a room.</summary>
public sealed record RoomInvitation(Guid AccountId, string RoomJid, string From, string? Reason, DateTime Received)
{
	internal string? Password { get; init; }
}
