using Waher.Persistence.Attributes;

namespace Neuro.Nat.Engine.Storage;

// What NAT keeps in its encrypted database. Each class has a fixed collection name and stores no
// type name, so renaming a namespace or a class never makes saved data unreadable (a test checks
// this). The payload is JSON in one field, so the stored form does not follow every change to the
// app's own records either. The indexed fields are what the payload is looked up by.

/// <summary>Marks a class whose objects are kept in the database.</summary>
public interface IStoredObject
{
	string? ObjectId { get; set; }
}

/// <summary>One account: the profile, without secrets.</summary>
[CollectionName("Accounts")]
[TypeName(TypeNameSerialization.None)]
[Index("AccountId")]
public sealed class StoredAccount : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	public string AccountId { get; set; } = string.Empty;

	public string Json { get; set; } = string.Empty;
}

/// <summary>An account's sign-in: after the first sign-in, what was agreed with the Neuron rather than the password.</summary>
[CollectionName("SignIns")]
[TypeName(TypeNameSerialization.None)]
[Index("AccountId")]
public sealed class StoredSignIn : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	public string AccountId { get; set; } = string.Empty;

	public string Json { get; set; } = string.Empty;
}

/// <summary>A conversation's own state: unread count, subject, notifications. Its messages are kept one by one.</summary>
[CollectionName("ChatConversations")]
[TypeName(TypeNameSerialization.None)]
[Index("AccountId", "Jid")]
public sealed class StoredConversation : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	public string AccountId { get; set; } = string.Empty;

	public string Jid { get; set; } = string.Empty;

	public string Json { get; set; } = string.Empty;
}

/// <summary>One chat message. Read a conversation at a time, in the order it was written.</summary>
[CollectionName("ChatMessages")]
[TypeName(TypeNameSerialization.None)]
[Index("AccountId", "Jid", "Timestamp")]
public sealed class StoredChatMessage : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	public string AccountId { get; set; } = string.Empty;

	public string Jid { get; set; } = string.Empty;

	public DateTime Timestamp { get; set; }

	/// <summary>The message's id in NAT.</summary>
	public string MessageKey { get; set; } = string.Empty;

	public string Json { get; set; } = string.Empty;
}

/// <summary>A room an account has joined, with its nickname and password.</summary>
[CollectionName("ChatRooms")]
[TypeName(TypeNameSerialization.None)]
[Index("AccountId", "Jid")]
public sealed class StoredRoom : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	public string AccountId { get; set; } = string.Empty;

	public string Jid { get; set; } = string.Empty;

	public string Json { get; set; } = string.Empty;
}

/// <summary>One Inbox item that came from a Neuron.</summary>
[CollectionName("InboxItems")]
[TypeName(TypeNameSerialization.None)]
[Index("ItemId")]
public sealed class StoredInboxItem : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	public string ItemId { get; set; } = string.Empty;

	public string Json { get; set; } = string.Empty;
}

/// <summary>A group of settings, by name ("ui", "alerts").</summary>
[CollectionName("AppSettings")]
[TypeName(TypeNameSerialization.None)]
[Index("Name")]
public sealed class StoredSetting : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	public string Name { get; set; } = string.Empty;

	public string Json { get; set; } = string.Empty;
}

/// <summary>What a contact of an account answered when asked whether it is a Neuron.</summary>
[CollectionName("KnownNeurons")]
[TypeName(TypeNameSerialization.None)]
[Index("AccountId", "Jid")]
public sealed class StoredKnownNeuron : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	public string AccountId { get; set; } = string.Empty;

	/// <summary>The contact's bare JID, in lower case.</summary>
	public string Jid { get; set; } = string.Empty;

	public bool IsNeuron { get; set; }

	/// <summary>What the Neuron calls itself (its own domain). Empty until it has said.</summary>
	[DefaultValueStringEmpty]
	public string OwnName { get; set; } = string.Empty;

	/// <summary>When it answered (UTC).</summary>
	public DateTime Answered { get; set; }
}

/// <summary>
/// An answer a feature fetched from a Neuron's web API and keeps between runs (app telemetry's
/// numbers, for one): the body as it came, with its ETag and when it was asked.
/// </summary>
[CollectionName("FeatureAnswers")]
[TypeName(TypeNameSerialization.None)]
[Index("Feature", "Key")]
public sealed class StoredFeatureAnswer : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	/// <summary>The feature's key, such as "telemetry".</summary>
	public string Feature { get; set; } = string.Empty;

	/// <summary>What was asked, as the feature names it (account, Neuron and path).</summary>
	public string Key { get; set; } = string.Empty;

	public string Json { get; set; } = string.Empty;
}
