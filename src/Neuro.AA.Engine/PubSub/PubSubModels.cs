using Waher.Networking.XMPP.PubSub.Events;

namespace Neuro.AA.Engine.PubSub;

/// <summary>A node on a publish/subscribe service, or in an account's personal (PEP) storage.</summary>
public sealed record PubSubNodeInfo(string Service, string Node, string? Name, bool IsPersonal)
{
	/// <summary>The collection the node was listed under. Null at the top.</summary>
	public string? Parent { get; init; }

	/// <summary>A node that holds other nodes and no items of its own.</summary>
	public bool IsCollection { get; init; }
}

/// <summary>An item published on a node.</summary>
public sealed record PubSubItemInfo(string Node, string ItemId, string? Publisher, string PayloadXml)
{
	/// <summary>When it was published, for items that arrived while watching.</summary>
	public DateTime? Published { get; init; }
}

/// <summary>Who may read a node and subscribe to it.</summary>
public enum NodeAccess
{
	/// <summary>Anyone.</summary>
	Open,

	/// <summary>Whoever sees the owner's presence: the owner's contacts.</summary>
	Presence,

	/// <summary>The owner's contacts in certain groups.</summary>
	Roster,

	/// <summary>Whoever an owner approves, one request at a time.</summary>
	Authorize,

	/// <summary>Only those an owner has put on the node's list.</summary>
	Whitelist,
}

/// <summary>Who may publish to a node.</summary>
public enum NodePublishers
{
	/// <summary>Owners and those named as publishers.</summary>
	Publishers,

	/// <summary>Everyone who subscribes.</summary>
	Subscribers,

	/// <summary>Anyone.</summary>
	Open,
}

/// <summary>Whether somebody new gets the latest item right away.</summary>
public enum NodeLastItem
{
	Never,
	OnSubscribe,
	OnSubscribeAndPresence,
}

/// <summary>The settings of a node that matter day to day, in the app's own terms.</summary>
public sealed record NodeSettings
{
	public string Title { get; init; } = string.Empty;
	public string Description { get; init; } = string.Empty;
	public NodeAccess Access { get; init; } = NodeAccess.Open;
	public NodePublishers Publishers { get; init; } = NodePublishers.Publishers;

	/// <summary>Items stay on the node after they are sent out. Without it, only whoever is subscribed at the time sees them.</summary>
	public bool KeepItems { get; init; } = true;

	/// <summary>The most items kept. Null leaves it to the service.</summary>
	public int? MaxItems { get; init; }

	public NodeLastItem SendLast { get; init; } = NodeLastItem.OnSubscribe;

	/// <summary>Subscribers are told when an item is removed.</summary>
	public bool TellWhenRemoved { get; init; } = true;

	/// <summary>A node that holds other nodes.</summary>
	public bool IsCollection { get; init; }
}

/// <summary>What somebody may do on a node.</summary>
public enum NodeRole
{
	/// <summary>Nothing beyond what the node gives everyone.</summary>
	None,

	/// <summary>May read and subscribe, where the node is closed to others.</summary>
	Member,

	/// <summary>May publish, and read.</summary>
	Publisher,

	/// <summary>May publish, but not read.</summary>
	PublishOnly,

	/// <summary>May do everything, including changing the node and removing it.</summary>
	Owner,

	/// <summary>Is kept out.</summary>
	Blocked,
}

/// <summary>Somebody with a role on a node.</summary>
public sealed record NodePerson(string Jid, NodeRole Role);

/// <summary>Where a subscription stands.</summary>
public enum SubscriberState
{
	/// <summary>Not subscribed.</summary>
	None,

	/// <summary>Gets what is published.</summary>
	Subscribed,

	/// <summary>Asked, and waits for an owner to say yes.</summary>
	Pending,

	/// <summary>Subscribed, but has to choose its options before anything is sent.</summary>
	Unconfigured,
}

/// <summary>Somebody who subscribes to a node, or has asked to.</summary>
public sealed record NodeSubscriber(string Jid, SubscriberState State, string? SubscriptionId = null);

/// <summary>Somebody asks to subscribe to a node this account owns.</summary>
public sealed record NodeRequest(Guid AccountId, string Service, string Node, string Jid, DateTime Received)
{
	internal SubscriptionRequestEventArgs? Args { get; init; }
}

/// <summary>Something happened on a watched node: an item came, an item went, or everything went.</summary>
/// <param name="Item">The item that was published, when one was.</param>
/// <param name="RemovedItemId">The id of the item that was removed, when one was.</param>
/// <param name="Cleared">All items were removed.</param>
public sealed record NodeItemEvent(Guid AccountId, string Service, string Node, PubSubItemInfo? Item, string? RemovedItemId = null, bool Cleared = false);

/// <summary>
/// Stands in for the connection: tests and previews answer what would be asked of a
/// publish/subscribe service. Each call names the account it is made through.
/// </summary>
internal interface IPubSubStandIn
{
	string ServiceAddress { get; }
	Task<IReadOnlyList<PubSubNodeInfo>> NodesAsync(Guid accountId, bool personal, string? parent);
	Task<IReadOnlyList<PubSubItemInfo>> ItemsAsync(Guid accountId, PubSubNodeInfo node, int? latest);
	Task<string> PublishAsync(Guid accountId, PubSubNodeInfo node, string? itemId, string payloadXml);
	Task RetractAsync(Guid accountId, PubSubNodeInfo node, string itemId);
	Task<PubSubNodeInfo> CreateNodeAsync(Guid accountId, string name, NodeSettings? settings, string? parent);
	Task DeleteNodeAsync(Guid accountId, PubSubNodeInfo node);
	Task ClearAsync(Guid accountId, PubSubNodeInfo node);
	Task<IReadOnlyList<string>> WatchedAsync(Guid accountId, bool personal);
	Task<SubscriberState> WatchAsync(Guid accountId, PubSubNodeInfo node);
	Task UnwatchAsync(Guid accountId, PubSubNodeInfo node);
	Task<NodeSettings> SettingsAsync(Guid accountId, PubSubNodeInfo? node);
	Task SaveSettingsAsync(Guid accountId, PubSubNodeInfo node, NodeSettings settings);
	Task<(IReadOnlyList<NodePerson> People, IReadOnlyList<NodeSubscriber> Subscribers)> PeopleAsync(Guid accountId, PubSubNodeInfo node);
	Task SetRoleAsync(Guid accountId, PubSubNodeInfo node, string jid, NodeRole role);
	Task SetSubscriberAsync(Guid accountId, PubSubNodeInfo node, string jid, SubscriberState state);
}
