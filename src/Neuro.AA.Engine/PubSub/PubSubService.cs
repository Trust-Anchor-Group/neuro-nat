using System.Collections.Concurrent;
using Neuro.AA.Engine.Accounts;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Events;
using Waher.Networking.XMPP.PubSub;
using Waher.Networking.XMPP.PubSub.Events;
using Waher.Networking.XMPP.ServiceDiscovery;

namespace Neuro.AA.Engine.PubSub;

/// <summary>A node on a publish/subscribe service, or in an account's personal (PEP) storage.</summary>
public sealed record PubSubNodeInfo(string Service, string Node, string? Name, bool IsPersonal);

/// <summary>An item published on a node.</summary>
public sealed record PubSubItemInfo(string Node, string ItemId, string? Publisher, string PayloadXml);

/// <summary>
/// Publish/subscribe through each account: the server's pubsub service and the account's own
/// personal storage (PEP): list nodes, read items, publish and retract items, create and
/// delete nodes.
/// </summary>
public sealed class PubSubService : IAsyncDisposable
{
	private readonly AccountManager accounts;
	private readonly ConcurrentDictionary<Guid, PubSubClient> clients = new();
	private readonly ConcurrentDictionary<Guid, string> problems = new();
	private readonly HashSet<AccountSession> watched = [];

	public PubSubService(AccountManager accounts)
	{
		this.accounts = accounts;
		accounts.SessionsChanged += (_, _) => this.Sync();
		this.Sync();
	}

	/// <summary>Raised when the service becomes available for an account, or goes away. Any thread.</summary>
	public event EventHandler<Guid>? Changed;

	public bool IsAvailable(Guid accountId) => this.clients.ContainsKey(accountId);

	public string? Problem(Guid accountId) => this.problems.TryGetValue(accountId, out string? p) ? p : null;

	/// <summary>The pubsub service's address for an account.</summary>
	public string? Service(Guid accountId) => this.clients.TryGetValue(accountId, out PubSubClient? c) ? c.ComponentAddress : null;

	private PubSubClient Client(Guid accountId)
		=> this.clients.TryGetValue(accountId, out PubSubClient? c) ? c : throw new InvalidOperationException(this.Problem(accountId) ?? "The account is not connected.");

	private XmppClient Xmpp(Guid accountId)
		=> this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == accountId)?.Client ?? throw new InvalidOperationException("The account is not connected.");

	/// <summary>Nodes on the pubsub service (top level, or below a collection node).</summary>
	public Task<IReadOnlyList<PubSubNodeInfo>> NodesAsync(Guid accountId, string? parent = null)
		=> this.ListAsync(accountId, this.Client(accountId).ComponentAddress, parent, false);

	/// <summary>Nodes in the account's personal storage (PEP): its avatar, nickname and so on.</summary>
	public Task<IReadOnlyList<PubSubNodeInfo>> PersonalNodesAsync(Guid accountId)
	{
		XmppClient x = this.Xmpp(accountId);
		return this.ListAsync(accountId, x.BareJID, null, true);
	}

	private async Task<IReadOnlyList<PubSubNodeInfo>> ListAsync(Guid accountId, string service, string? parent, bool personal)
	{
		XmppClient x = this.Xmpp(accountId);
		ServiceItemsDiscoveryEventArgs e = string.IsNullOrEmpty(parent)
			? await x.ServiceItemsDiscoveryAsync(service).WaitAsync(TimeSpan.FromSeconds(20))
			: await x.ServiceItemsDiscoveryAsync(service, parent).WaitAsync(TimeSpan.FromSeconds(20));

		if (!e.Ok)
			throw new InvalidOperationException(e.ErrorText ?? "The service did not list its nodes.");

		return [.. (e.Items ?? [])
			.Where(i => !string.IsNullOrEmpty(i.Node))
			.Select(i => new PubSubNodeInfo(service, i.Node, string.IsNullOrWhiteSpace(i.Name) ? null : i.Name, personal))
			.OrderBy(n => n.Node, StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>Items on a node, newest first as the service returns them.</summary>
	public async Task<IReadOnlyList<PubSubItemInfo>> ItemsAsync(Guid accountId, PubSubNodeInfo node)
	{
		TaskCompletionSource<ItemsEventArgs> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		await this.Client(accountId).GetItems(node.Service, node.Node, (_, e) => { done.TrySetResult(e); return Task.CompletedTask; }, null);
		ItemsEventArgs r = await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
		if (!r.Ok)
			throw new InvalidOperationException(r.ErrorText ?? "The node did not give its items.");

		return [.. (r.Items ?? []).Select(i => new PubSubItemInfo(node.Node, i.ItemId, string.IsNullOrEmpty(i.Publisher) ? null : i.Publisher, i.Payload ?? string.Empty))];
	}

	/// <summary>Publishes an XML payload. A new item id is given when <paramref name="itemId"/> is empty.</summary>
	public async Task<string> PublishAsync(Guid accountId, PubSubNodeInfo node, string? itemId, string payloadXml)
	{
		TaskCompletionSource<ItemResultEventArgs> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		await this.Client(accountId).Publish(node.Service, node.Node, itemId ?? string.Empty, payloadXml,
			(_, e) => { done.TrySetResult(e); return Task.CompletedTask; }, null);
		ItemResultEventArgs r = await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
		return r.Ok ? r.ItemId : throw new InvalidOperationException(r.ErrorText ?? "The node refused the item.");
	}

	public async Task RetractAsync(Guid accountId, PubSubNodeInfo node, string itemId)
	{
		TaskCompletionSource<IqResultEventArgs> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		await this.Client(accountId).Retract(node.Service, node.Node, itemId, (_, e) => { done.TrySetResult(e); return Task.CompletedTask; }, null);
		IqResultEventArgs r = await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
		if (!r.Ok)
			throw new InvalidOperationException(r.ErrorText ?? "The node refused.");
	}

	/// <summary>Creates a node with the service's default configuration.</summary>
	public async Task<PubSubNodeInfo> CreateNodeAsync(Guid accountId, string name)
	{
		PubSubClient c = this.Client(accountId);
		TaskCompletionSource<NodeEventArgs> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		await c.CreateNode(c.ComponentAddress, name, new NodeConfiguration(), (_, e) => { done.TrySetResult(e); return Task.CompletedTask; }, null);
		NodeEventArgs r = await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
		return r.Ok ? new PubSubNodeInfo(c.ComponentAddress, r.NodeName ?? name, null, false) : throw new InvalidOperationException(r.ErrorText ?? "The service refused to create the node.");
	}

	/// <summary>Deletes a node and everything published on it.</summary>
	public async Task DeleteNodeAsync(Guid accountId, PubSubNodeInfo node)
	{
		TaskCompletionSource<NodeEventArgs> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		await this.Client(accountId).DeleteNode(node.Service, node.Node, string.Empty, (_, e) => { done.TrySetResult(e); return Task.CompletedTask; }, null);
		NodeEventArgs r = await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
		if (!r.Ok)
			throw new InvalidOperationException(r.ErrorText ?? "The service refused to delete the node.");
	}

	// ---- Connections -----------------------------------------------------------------------

	private void Sync()
	{
		foreach (AccountSession s in this.accounts.Sessions)
		{
			bool added;
			lock (this.watched)
				added = this.watched.Add(s);
			if (!added)
				continue;

			s.StatusChanged += (_, status) =>
			{
				if (status == ConnectionStatus.Connected)
					_ = this.StartAsync(s);
				else if (this.clients.TryRemove(s.Profile.Id, out PubSubClient? old))
				{
					old.Dispose();
					this.Changed?.Invoke(this, s.Profile.Id);
				}
			};

			if (s.Status == ConnectionStatus.Connected)
				_ = this.StartAsync(s);
		}
	}

	private async Task StartAsync(AccountSession s)
	{
		XmppClient? client = s.Client;
		if (client is null)
			return;

		Guid id = s.Profile.Id;
		try
		{
			string? component = null;
			ServiceItemsDiscoveryEventArgs items = await client.ServiceItemsDiscoveryAsync(client.Domain).WaitAsync(TimeSpan.FromSeconds(20));
			foreach (Item item in items.Items ?? [])
			{
				ServiceDiscoveryEventArgs info = await client.ServiceDiscoveryAsync(item.JID).WaitAsync(TimeSpan.FromSeconds(10));
				if (info.Ok && info.HasFeature(PubSubClient.NamespacePubSub) && info.Identities.Any(i => i.Category == "pubsub" && i.Type == "service"))
				{
					component = item.JID;
					break;
				}
			}

			if (component is null)
			{
				this.problems[id] = "The account's server has no publish/subscribe service.";
				this.Changed?.Invoke(this, id);
				return;
			}

			PubSubClient c = new(client, component);
			if (this.clients.TryRemove(id, out PubSubClient? old))
				old.Dispose();
			this.clients[id] = c;
			this.problems.TryRemove(id, out _);
		}
		catch (Exception ex)
		{
			this.problems[id] = "Publish/subscribe is not available: " + ex.Message;
		}

		this.Changed?.Invoke(this, id);
	}

	public ValueTask DisposeAsync()
	{
		foreach (PubSubClient c in this.clients.Values)
			c.Dispose();
		this.clients.Clear();
		return ValueTask.CompletedTask;
	}
}
