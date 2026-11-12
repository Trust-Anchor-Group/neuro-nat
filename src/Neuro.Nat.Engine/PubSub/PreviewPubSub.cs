namespace Neuro.Nat.Engine.PubSub;

/// <summary>
/// A publish/subscribe service that lives in memory, for previews and UI tests: nodes and
/// collections, items, settings, people, subscribers and watching all behave as on a real
/// service, including what an account that is not an owner is refused.
/// </summary>
internal sealed class PreviewPubSub : IPubSubStandIn
{
	private sealed class Node
	{
		public required string Service { get; init; }
		public required string Name { get; init; }
		public string? Parent { get; init; }
		public bool Personal { get; init; }
		public NodeSettings Settings { get; set; } = new();
		public List<PubSubItemInfo> Items { get; } = [];
		public Dictionary<string, NodeRole> People { get; } = new(StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, SubscriberState> Subscribers { get; } = new(StringComparer.OrdinalIgnoreCase);
	}

	private readonly PubSubService owner;
	private readonly Dictionary<Guid, string> accounts = [];
	private readonly List<Node> nodes = [];
	private int next = 1;

	public PreviewPubSub(PubSubService owner, string serviceAddress)
	{
		this.owner = owner;
		this.ServiceAddress = serviceAddress;
	}

	public string ServiceAddress { get; }

	/// <summary>Every call, in order: "publish ops/alerts a1". Tests read it.</summary>
	public List<string> Calls { get; } = [];

	/// <summary>Set to make a kind of call fail: given "publish", "items", "settings" and so on, returns what to throw.</summary>
	public Func<string, Exception?>? Fail { get; set; }

	/// <summary>The settings a new node gets when none are given.</summary>
	public NodeSettings Defaults { get; set; } = new();

	/// <summary>Tells the service which address an account has.</summary>
	public PreviewPubSub Account(Guid accountId, string bareJid)
	{
		this.accounts[accountId] = bareJid;
		return this;
	}

	/// <summary>Adds a node on the service. Its first owner is <paramref name="ownerJid"/>.</summary>
	public PreviewPubSub Add(string name, string ownerJid, string? title = null, string? parent = null, bool collection = false, NodeSettings? settings = null)
	{
		Node n = new()
		{
			Service = this.ServiceAddress,
			Name = name,
			Parent = parent,
			Settings = (settings ?? this.Defaults) with { Title = title ?? settings?.Title ?? string.Empty, IsCollection = collection },
		};
		n.People[ownerJid] = NodeRole.Owner;
		this.nodes.Add(n);
		return this;
	}

	/// <summary>Adds a node to an account's personal storage.</summary>
	public PreviewPubSub AddPersonal(Guid accountId, string name)
	{
		string me = this.accounts[accountId];
		Node n = new() { Service = me, Name = name, Personal = true, Settings = new NodeSettings { Access = NodeAccess.Presence } };
		n.People[me] = NodeRole.Owner;
		this.nodes.Add(n);
		return this;
	}

	/// <summary>Puts an item on a node without telling anyone: what was there before the app looked.</summary>
	public PreviewPubSub Item(string service, string node, string itemId, string payloadXml, string? publisher = null)
	{
		Node n = this.Find(service, node);
		n.Items.RemoveAll(i => i.ItemId == itemId);
		n.Items.Insert(0, new PubSubItemInfo(node, itemId, publisher, payloadXml));
		return this;
	}

	public PreviewPubSub Person(string service, string node, string jid, NodeRole role)
	{
		this.Find(service, node).People[jid] = role;
		return this;
	}

	public PreviewPubSub Subscriber(string service, string node, string jid, SubscriberState state)
	{
		this.Find(service, node).Subscribers[jid] = state;
		return this;
	}

	/// <summary>Somebody else publishes: the item is stored, and whoever watches the node is told.</summary>
	public void Arrive(string service, string node, string itemId, string payloadXml, string publisher)
	{
		Node n = this.Find(service, node);
		PubSubItemInfo item = new(node, itemId, publisher, payloadXml) { Published = DateTime.Now };
		n.Items.RemoveAll(i => i.ItemId == itemId);
		n.Items.Insert(0, item);
		this.Tell(n, id => new NodeItemEvent(id, n.Service, n.Name, item));
	}

	/// <summary>Somebody else removes an item.</summary>
	public void Leave(string service, string node, string itemId)
	{
		Node n = this.Find(service, node);
		n.Items.RemoveAll(i => i.ItemId == itemId);
		this.Tell(n, id => new NodeItemEvent(id, n.Service, n.Name, null, itemId));
	}

	public int Count(string service, string node) => this.Find(service, node).Items.Count;

	public bool Has(string service, string node)
		=> this.nodes.Any(n => n.Service == service && n.Name == node);

	private Node Find(string service, string node)
		=> this.nodes.FirstOrDefault(n => n.Service == service && n.Name == node)
			?? throw new InvalidOperationException(PubSubService.Explain("ItemNotFoundException", null, "The node did not answer."));

	private string Me(Guid accountId)
		=> this.accounts.TryGetValue(accountId, out string? jid) ? jid : throw new InvalidOperationException("The account is not connected.");

	private NodeRole Role(Node n, Guid accountId)
		=> n.People.TryGetValue(this.Me(accountId), out NodeRole r) ? r : NodeRole.None;

	private void Called(string what, string? detail = null)
	{
		this.Calls.Add(detail is null ? what : what + " " + detail);
		if (this.Fail?.Invoke(what) is Exception ex)
			throw ex;
	}

	private void OwnerOnly(Node n, Guid accountId, string refused)
	{
		if (this.Role(n, accountId) != NodeRole.Owner)
			throw new InvalidOperationException(PubSubService.Explain("ForbiddenException", null, refused));
	}

	private void Tell(Node n, Func<Guid, NodeItemEvent> what)
	{
		foreach ((Guid id, string jid) in this.accounts)
		{
			if (n.Subscribers.TryGetValue(jid, out SubscriberState s) && s == SubscriberState.Subscribed)
				this.owner.Raise(what(id));
		}
	}

	private PubSubNodeInfo Info(Node n)
		=> new(n.Service, n.Name, string.IsNullOrWhiteSpace(n.Settings.Title) ? null : n.Settings.Title, n.Personal) { Parent = n.Parent, IsCollection = n.Settings.IsCollection };

	public Task<IReadOnlyList<PubSubNodeInfo>> NodesAsync(Guid accountId, bool personal, string? parent)
	{
		this.Called("nodes", parent);
		string service = personal ? this.Me(accountId) : this.ServiceAddress;
		IReadOnlyList<PubSubNodeInfo> list = [.. this.nodes
			.Where(n => n.Service == service && n.Personal == personal && n.Parent == parent)
			.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
			.Select(this.Info)];
		return Task.FromResult(list);
	}

	public Task<IReadOnlyList<PubSubItemInfo>> ItemsAsync(Guid accountId, PubSubNodeInfo node, int? latest)
	{
		this.Called("items", node.Node + (latest is int k ? " " + k : string.Empty));
		Node n = this.Find(node.Service, node.Node);
		NodeRole role = this.Role(n, accountId);
		if (role == NodeRole.Blocked || (n.Settings.Access == NodeAccess.Whitelist && role == NodeRole.None))
			throw new InvalidOperationException(PubSubService.Explain("ForbiddenException", null, "The node did not give its items."));

		IReadOnlyList<PubSubItemInfo> list = latest is int count ? [.. n.Items.Take(count)] : [.. n.Items];
		return Task.FromResult(list);
	}

	public Task<string> PublishAsync(Guid accountId, PubSubNodeInfo node, string? itemId, string payloadXml)
	{
		this.Called("publish", node.Node + " " + itemId);
		Node n = this.Find(node.Service, node.Node);
		NodeRole role = this.Role(n, accountId);
		bool may = role is NodeRole.Owner or NodeRole.Publisher or NodeRole.PublishOnly
			|| n.Settings.Publishers == NodePublishers.Open
			|| (n.Settings.Publishers == NodePublishers.Subscribers && n.Subscribers.ContainsKey(this.Me(accountId)));
		if (!may)
			throw new InvalidOperationException(PubSubService.Explain("ForbiddenException", null, "The item was not published."));

		string id = string.IsNullOrWhiteSpace(itemId) ? "item-" + this.next++ : itemId.Trim();
		PubSubItemInfo item = new(node.Node, id, this.Me(accountId), payloadXml) { Published = DateTime.Now };
		n.Items.RemoveAll(i => i.ItemId == id);
		n.Items.Insert(0, item);
		if (n.Settings.MaxItems is int max && n.Items.Count > max)
			n.Items.RemoveRange(max, n.Items.Count - max);

		this.Tell(n, a => new NodeItemEvent(a, n.Service, n.Name, item));
		return Task.FromResult(id);
	}

	public Task RetractAsync(Guid accountId, PubSubNodeInfo node, string itemId)
	{
		this.Called("retract", node.Node + " " + itemId);
		Node n = this.Find(node.Service, node.Node);
		if (this.Role(n, accountId) is not (NodeRole.Owner or NodeRole.Publisher))
			throw new InvalidOperationException(PubSubService.Explain("ForbiddenException", null, "The item was not removed."));
		if (n.Items.RemoveAll(i => i.ItemId == itemId) == 0)
			throw new InvalidOperationException(PubSubService.Explain("ItemNotFoundException", null, "The item was not removed."));

		this.Tell(n, a => new NodeItemEvent(a, n.Service, n.Name, null, itemId));
		return Task.CompletedTask;
	}

	public Task<PubSubNodeInfo> CreateNodeAsync(Guid accountId, string name, NodeSettings? settings, string? parent)
	{
		this.Called("create", name);
		if (this.nodes.Any(n => n.Service == this.ServiceAddress && n.Name == name))
			throw new InvalidOperationException(PubSubService.Explain("ConflictException", null, "The node was not created."));

		Node n = new() { Service = this.ServiceAddress, Name = name, Parent = parent, Settings = settings ?? this.Defaults };
		n.People[this.Me(accountId)] = NodeRole.Owner;
		this.nodes.Add(n);
		return Task.FromResult(this.Info(n));
	}

	public Task DeleteNodeAsync(Guid accountId, PubSubNodeInfo node)
	{
		this.Called("delete", node.Node);
		Node n = this.Find(node.Service, node.Node);
		this.OwnerOnly(n, accountId, "The node was not deleted.");
		this.nodes.RemoveAll(x => ReferenceEquals(x, n) || (x.Service == n.Service && x.Parent == n.Name));
		return Task.CompletedTask;
	}

	public Task ClearAsync(Guid accountId, PubSubNodeInfo node)
	{
		this.Called("clear", node.Node);
		Node n = this.Find(node.Service, node.Node);
		this.OwnerOnly(n, accountId, "The items were not removed.");
		n.Items.Clear();
		this.Tell(n, a => new NodeItemEvent(a, n.Service, n.Name, null, null, true));
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<string>> WatchedAsync(Guid accountId, bool personal)
	{
		this.Called("watched");
		string me = this.Me(accountId);
		string service = personal ? me : this.ServiceAddress;
		IReadOnlyList<string> list = [.. this.nodes
			.Where(n => n.Service == service && n.Subscribers.TryGetValue(me, out SubscriberState s) && s is SubscriberState.Subscribed or SubscriberState.Unconfigured)
			.Select(n => n.Name)];
		return Task.FromResult(list);
	}

	public Task<SubscriberState> WatchAsync(Guid accountId, PubSubNodeInfo node)
	{
		this.Called("watch", node.Node);
		Node n = this.Find(node.Service, node.Node);
		NodeRole role = this.Role(n, accountId);
		if (role == NodeRole.Blocked || (n.Settings.Access == NodeAccess.Whitelist && role == NodeRole.None))
			throw new InvalidOperationException(PubSubService.Explain("ForbiddenException", null, "The node did not let you watch it."));

		SubscriberState state = n.Settings.Access == NodeAccess.Authorize && role != NodeRole.Owner ? SubscriberState.Pending : SubscriberState.Subscribed;
		n.Subscribers[this.Me(accountId)] = state;
		return Task.FromResult(state);
	}

	public Task UnwatchAsync(Guid accountId, PubSubNodeInfo node)
	{
		this.Called("unwatch", node.Node);
		this.Find(node.Service, node.Node).Subscribers.Remove(this.Me(accountId));
		return Task.CompletedTask;
	}

	public Task<NodeSettings> SettingsAsync(Guid accountId, PubSubNodeInfo? node)
	{
		this.Called("settings", node?.Node);
		if (node is null)
			return Task.FromResult(this.Defaults);

		Node n = this.Find(node.Service, node.Node);
		this.OwnerOnly(n, accountId, "The node did not give its settings.");
		return Task.FromResult(n.Settings);
	}

	public Task SaveSettingsAsync(Guid accountId, PubSubNodeInfo node, NodeSettings settings)
	{
		this.Called("save", node.Node);
		Node n = this.Find(node.Service, node.Node);
		this.OwnerOnly(n, accountId, "The settings were not saved.");
		n.Settings = settings with { IsCollection = n.Settings.IsCollection };
		return Task.CompletedTask;
	}

	public Task<(IReadOnlyList<NodePerson> People, IReadOnlyList<NodeSubscriber> Subscribers)> PeopleAsync(Guid accountId, PubSubNodeInfo node)
	{
		this.Called("people", node.Node);
		Node n = this.Find(node.Service, node.Node);
		this.OwnerOnly(n, accountId, "The node did not say who may do what.");
		IReadOnlyList<NodePerson> people = [.. n.People.Select(p => new NodePerson(p.Key, p.Value)).OrderBy(p => p.Role != NodeRole.Owner).ThenBy(p => p.Jid, StringComparer.OrdinalIgnoreCase)];
		IReadOnlyList<NodeSubscriber> subscribers = [.. n.Subscribers.Select(s => new NodeSubscriber(s.Key, s.Value)).OrderBy(s => s.State != SubscriberState.Pending).ThenBy(s => s.Jid, StringComparer.OrdinalIgnoreCase)];
		return Task.FromResult((people, subscribers));
	}

	public Task SetRoleAsync(Guid accountId, PubSubNodeInfo node, string jid, NodeRole role)
	{
		this.Called("role", node.Node + " " + jid + " " + role);
		Node n = this.Find(node.Service, node.Node);
		this.OwnerOnly(n, accountId, "The role was not changed.");
		if (role != NodeRole.Owner && n.People.TryGetValue(jid, out NodeRole was) && was == NodeRole.Owner && n.People.Count(p => p.Value == NodeRole.Owner) == 1)
			throw new InvalidOperationException("The role was not changed. A node needs at least one owner.");

		if (role == NodeRole.None)
			n.People.Remove(jid);
		else
			n.People[jid] = role;
		return Task.CompletedTask;
	}

	public Task SetSubscriberAsync(Guid accountId, PubSubNodeInfo node, string jid, SubscriberState state)
	{
		this.Called("subscriber", node.Node + " " + jid + " " + state);
		Node n = this.Find(node.Service, node.Node);
		this.OwnerOnly(n, accountId, "The subscription was not changed.");
		if (state == SubscriberState.None)
			n.Subscribers.Remove(jid);
		else
			n.Subscribers[jid] = state;
		return Task.CompletedTask;
	}
}
