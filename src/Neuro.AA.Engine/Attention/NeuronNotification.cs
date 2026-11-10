using Neuro.AA.Engine.Events;

namespace Neuro.AA.Engine.Attention;

/// <summary>What a Neuron's notification is about. Recognized from its wording.</summary>
public enum NotificationKind
{
	/// <summary>Not recognized. Shown as it was sent.</summary>
	Other,

	/// <summary>An Alert or Emergency event the Neuron forwards to its administrators.</summary>
	Alert,

	/// <summary>Code tried something it is not allowed to, and the Neuron stopped it.</summary>
	Security,

	/// <summary>Someone asks to add the Neuron to their contacts.</summary>
	RosterRequest,

	/// <summary>A legal identity application waits for review.</summary>
	LegalIdApplication,

	/// <summary>A legal identity was approved or rejected without a person.</summary>
	LegalIdDecision,

	/// <summary>The Neuron's own legal identity is missing or about to expire.</summary>
	NeuronIdentity,

	/// <summary>A contract template was proposed.</summary>
	ContractProposal,

	/// <summary>A smart contract waits for the Neuron's signature.</summary>
	SignatureRequest,

	/// <summary>Someone asks for an account on the Neuron.</summary>
	AccountRequest,

	/// <summary>Someone asks to recover an account.</summary>
	AccountRecovery,

	/// <summary>Someone asks for an API key.</summary>
	ApiKeyRequest,

	/// <summary>Someone sent feedback from the Neuron's web pages.</summary>
	Feedback,

	/// <summary>An XMPP account was created.</summary>
	AccountCreated,

	/// <summary>An XMPP account was deleted.</summary>
	AccountDeleted,

	/// <summary>An API key was created.</summary>
	ApiKeyCreated,

	/// <summary>A backup was uploaded, or could not be.</summary>
	Backup,

	/// <summary>New software was downloaded and will be, or can be, installed.</summary>
	SoftwareUpdate,

	/// <summary>The Neuron was updated or restarted.</summary>
	Restarted,

	/// <summary>Someone logged on, off or locked the machine the Neuron service runs on.</summary>
	MachineSession,

	/// <summary>The Neuron joined or left a chat room.</summary>
	ChatRoom,

	/// <summary>Housekeeping, such as importing the IP location database.</summary>
	Maintenance,

	/// <summary>A diagram, graph or image, such as a startup profile.</summary>
	Diagram,
}

/// <summary>Where a notification is in the operator's hands.</summary>
public enum AttentionState
{
	Unread,
	Read,
	Handled,
}

/// <summary>A label and value from the first table in a notification.</summary>
public sealed record NotificationField(string Key, string Value);

/// <summary>A link in a notification, usually to the Neuron's admin pages.</summary>
public sealed record NotificationLink(string Text, string Url);

/// <summary>
/// A notification as it arrives from a Neuron: the chat message it sends to its notification
/// addresses.
/// </summary>
/// <param name="MessageId">The XMPP message id, when the Neuron set one. Later corrections refer to it.</param>
/// <param name="Replaces">Set when this message corrects an earlier one (XEP-0308).</param>
public sealed record IncomingNotification(
	Guid AccountId,
	string NeuronJid,
	string? MessageId,
	string? Replaces,
	DateTime Timestamp,
	string Markdown,
	string Text);

/// <summary>One item in the Attention list.</summary>
public sealed record NeuronNotification
{
	public required string Id { get; init; }
	public required Guid AccountId { get; init; }
	public required string NeuronJid { get; init; }
	public string? MessageId { get; init; }
	public required DateTime Received { get; init; }

	/// <summary>When the Neuron last corrected the message, if it did.</summary>
	public DateTime? Updated { get; init; }

	public required string Markdown { get; init; }
	public string Text { get; init; } = string.Empty;

	public NotificationKind Kind { get; init; }
	public EventSeverity Severity { get; init; }

	/// <summary>True when a person has to do something: review, approve, renew, answer.</summary>
	public bool NeedsAction { get; init; }

	/// <summary>One line that says what happened.</summary>
	public string Title { get; init; } = string.Empty;

	/// <summary>Who or what it concerns, when the notification says: a JID, an identity, a file.</summary>
	public string? Subject { get; init; }

	public IReadOnlyList<NotificationField> Fields { get; init; } = [];
	public IReadOnlyList<NotificationLink> Links { get; init; } = [];

	public AttentionState State { get; init; }
	public DateTime? HandledAt { get; init; }

	/// <summary>Still waiting for a person.</summary>
	public bool IsOpen => this.NeedsAction && this.State != AttentionState.Handled;

	/// <summary>Builds an item from an incoming message.</summary>
	public static NeuronNotification From(IncomingNotification m, string? id = null)
		=> Classify(new NeuronNotification
		{
			Id = id ?? Guid.NewGuid().ToString("N"),
			AccountId = m.AccountId,
			NeuronJid = m.NeuronJid,
			MessageId = string.IsNullOrEmpty(m.MessageId) ? null : m.MessageId,
			Received = m.Timestamp,
			Markdown = m.Markdown,
			Text = m.Text,
		});

	/// <summary>Fills in kind, title, fields and links from the Markdown.</summary>
	public static NeuronNotification Classify(NeuronNotification n)
	{
		NotificationClassifier.Result r = NotificationClassifier.Classify(n.Markdown);
		return n with
		{
			Kind = r.Kind,
			Severity = r.Severity,
			NeedsAction = r.NeedsAction,
			Title = r.Title,
			Subject = r.Subject,
			Fields = r.Fields,
			Links = r.Links,
		};
	}
}

public static class NotificationKindText
{
	/// <summary>Short label for a kind, as the Attention list shows it.</summary>
	public static string Label(this NotificationKind kind) => kind switch
	{
		NotificationKind.Alert => "Alert",
		NotificationKind.Security => "Security",
		NotificationKind.RosterRequest => "Contact request",
		NotificationKind.LegalIdApplication => "ID application",
		NotificationKind.LegalIdDecision => "ID decision",
		NotificationKind.NeuronIdentity => "Neuron identity",
		NotificationKind.ContractProposal => "Contract proposal",
		NotificationKind.SignatureRequest => "Signature",
		NotificationKind.AccountRequest => "Account request",
		NotificationKind.AccountRecovery => "Account recovery",
		NotificationKind.ApiKeyRequest => "API key request",
		NotificationKind.Feedback => "Feedback",
		NotificationKind.AccountCreated => "Account created",
		NotificationKind.AccountDeleted => "Account deleted",
		NotificationKind.ApiKeyCreated => "API key created",
		NotificationKind.Backup => "Backup",
		NotificationKind.SoftwareUpdate => "Software update",
		NotificationKind.Restarted => "Restart",
		NotificationKind.MachineSession => "Machine session",
		NotificationKind.ChatRoom => "Chat room",
		NotificationKind.Maintenance => "Maintenance",
		NotificationKind.Diagram => "Diagram",
		_ => "Notification",
	};
}
