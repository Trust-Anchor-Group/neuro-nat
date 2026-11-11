using System.Collections.Concurrent;
using Neuro.AA.Engine.Accounts;
using Waher.Events;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Events;
using Waher.Networking.XMPP.PubSub;
using Waher.Networking.XMPP.PubSub.Events;
using Waher.Networking.XMPP.ServiceDiscovery;

namespace Neuro.AA.Engine.PubSub;

/// <summary>
/// Publish/subscribe through each account: the nodes of the service on its Neuron and the
/// account's own personal storage (PEP). Lists nodes and what is inside collections, reads,
/// publishes and removes items, watches nodes for what is published while you look, and manages
/// a node: its settings, who may do what on it, who subscribes, and removing it.
/// </summary>
public sealed class PubSubService : IAsyncDisposable
{
	private static readonly TimeSpan wait = TimeSpan.FromSeconds(20);

	private readonly AccountManager accounts;
	private readonly ConcurrentDictionary<Guid, PubSubClient> clients = new();
	private readonly ConcurrentDictionary<Guid, string> problems = new();
	private readonly HashSet<AccountSession> watched = [];
	private readonly List<NodeRequest> requests = [];

	public PubSubService(AccountManager accounts)
	{
		this.accounts = accounts;
		accounts.SessionsChanged += (_, _) => this.Sync();
		this.Sync();
	}

	/// <summary>Set by tests and previews: answers in place of the connection.</summary>
	internal IPubSubStandIn? StandIn { get; set; }

	/// <summary>Raised when the service becomes available for an account, or goes away. Any thread.</summary>
	public event EventHandler<Guid>? Changed;

	/// <summary>Raised when something is published on, or removed from, a node the account watches. Any thread.</summary>
	public event EventHandler<NodeItemEvent>? ItemEvent;

	/// <summary>Raised when somebody asks to subscribe to a node an account owns, or such a request was answered. Any thread.</summary>
	public event EventHandler? RequestsChanged;

	public bool IsAvailable(Guid accountId) => this.StandIn is not null || this.clients.ContainsKey(accountId);

	public string? Problem(Guid accountId) => this.StandIn is null && this.problems.TryGetValue(accountId, out string? p) ? p : null;

	/// <summary>The pubsub service's address for an account.</summary>
	public string? Service(Guid accountId)
		=> this.StandIn is IPubSubStandIn s ? s.ServiceAddress : this.clients.TryGetValue(accountId, out PubSubClient? c) ? c.ComponentAddress : null;

	private PubSubClient Client(Guid accountId)
		=> this.clients.TryGetValue(accountId, out PubSubClient? c) ? c : throw new InvalidOperationException(this.Problem(accountId) ?? "The account is not connected.");

	private XmppClient Xmpp(Guid accountId)
		=> this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == accountId)?.Client ?? throw new InvalidOperationException("The account is not connected.");

	/// <summary>Sends a request and waits for its answer. A refusal becomes an exception that says what was refused.</summary>
	private static async Task<T> Ask<T>(Func<EventHandlerAsync<T>, Task> send, string refused)
		where T : IqResultEventArgs
	{
		TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		await send((_, e) =>
		{
			done.TrySetResult(e);
			return Task.CompletedTask;
		});

		T r;
		try
		{
			r = await done.Task.WaitAsync(wait);
		}
		catch (TimeoutException)
		{
			throw new TimeoutException("The service did not answer in time.");
		}

		return r.Ok ? r : throw new InvalidOperationException(Explain(r, refused));
	}

	/// <summary>Says why the service refused, in words that tell what can be done about it.</summary>
	internal static string Explain(IqResultEventArgs e, string refused)
		=> Explain(e.StanzaError?.GetType().Name, e.ErrorText, refused);

	internal static string Explain(string? errorType, string? errorText, string refused)
	{
		string? why = errorType switch
		{
			"ForbiddenException" or "NotAuthorizedException" or "NotAllowedException" or "RegistrationRequiredException" or "SubscriptionRequiredException"
				=> "You are not allowed to. Ask one of the node's owners.",
			"ItemNotFoundException" => "It is not there any more.",
			"FeatureNotImplementedException" or "ServiceUnavailableException" => "The service does not offer this.",
			"ConflictException" => "There is one with that name already.",
			"NotAcceptableException" or "BadRequestException" => "The service did not accept what was sent.",
			"PaymentRequiredException" or "ResourceConstraintException" => "The service has no room for more.",
			_ => null,
		};

		if (why is null)
			return string.IsNullOrWhiteSpace(errorText) ? refused : refused + " " + errorText.Trim();

		return refused + " " + why;
	}

	// ---- Nodes -----------------------------------------------------------------------------

	/// <summary>Nodes on the pubsub service: the top level, or what is inside a collection.</summary>
	public Task<IReadOnlyList<PubSubNodeInfo>> NodesAsync(Guid accountId, string? parent = null)
		=> this.StandIn is IPubSubStandIn s ? s.NodesAsync(accountId, false, parent) : this.ListAsync(accountId, this.Client(accountId).ComponentAddress, parent, false);

	/// <summary>Nodes in the account's personal storage (PEP): its avatar, nickname and so on.</summary>
	public Task<IReadOnlyList<PubSubNodeInfo>> PersonalNodesAsync(Guid accountId)
		=> this.StandIn is IPubSubStandIn s ? s.NodesAsync(accountId, true, null) : this.ListAsync(accountId, this.Xmpp(accountId).BareJID, null, true);

	private async Task<IReadOnlyList<PubSubNodeInfo>> ListAsync(Guid accountId, string service, string? parent, bool personal)
	{
		XmppClient x = this.Xmpp(accountId);
		ServiceItemsDiscoveryEventArgs e;
		try
		{
			e = string.IsNullOrEmpty(parent)
				? await x.ServiceItemsDiscoveryAsync(service).WaitAsync(wait)
				: await x.ServiceItemsDiscoveryAsync(service, parent).WaitAsync(wait);
		}
		catch (TimeoutException)
		{
			throw new TimeoutException("The service did not answer in time.");
		}

		if (!e.Ok)
			throw new InvalidOperationException(Explain(e, "The service did not list its nodes."));

		List<PubSubNodeInfo> nodes = [.. (e.Items ?? [])
			.Where(i => !string.IsNullOrEmpty(i.Node))
			.Select(i => new PubSubNodeInfo(service, i.Node, string.IsNullOrWhiteSpace(i.Name) ? null : i.Name, personal) { Parent = parent })
			.OrderBy(n => n.Node, StringComparer.OrdinalIgnoreCase)];

		// Which of them hold other nodes: each says so about itself. Personal storage has none.
		if (!personal && nodes.Count is > 0 and <= 80)
		{
			bool[] collection = await Task.WhenAll(nodes.Select(async n =>
			{
				try
				{
					ServiceDiscoveryEventArgs info = await x.ServiceDiscoveryAsync(service, n.Node).WaitAsync(TimeSpan.FromSeconds(8));
					return info.Ok && info.Identities.Any(i => i.Category == "pubsub" && i.Type == "collection");
				}
				catch (Exception)
				{
					return false;
				}
			}));

			for (int i = 0; i < nodes.Count; i++)
			{
				if (collection[i])
					nodes[i] = nodes[i] with { IsCollection = true };
			}
		}

		return nodes;
	}

	/// <summary>
	/// Creates a node. With no settings it gets the service's own; inside a collection when
	/// <paramref name="parent"/> names one.
	/// </summary>
	public async Task<PubSubNodeInfo> CreateNodeAsync(Guid accountId, string name, NodeSettings? settings = null, string? parent = null)
	{
		if (this.StandIn is IPubSubStandIn s)
			return await s.CreateNodeAsync(accountId, name, settings, parent);

		PubSubClient c = this.Client(accountId);
		NodeConfiguration config = settings is null ? new NodeConfiguration() : ToConfiguration(settings);
		if (!string.IsNullOrEmpty(parent))
			config.Collections = [parent];

		NodeEventArgs r = await Ask<NodeEventArgs>(cb => c.CreateNode(c.ComponentAddress, name, config, cb, null), "The node was not created.");
		return new PubSubNodeInfo(c.ComponentAddress, r.NodeName ?? name, string.IsNullOrWhiteSpace(settings?.Title) ? null : settings.Title, false)
		{
			Parent = parent,
			IsCollection = settings?.IsCollection == true,
		};
	}

	/// <summary>Deletes a node and everything published on it.</summary>
	public async Task DeleteNodeAsync(Guid accountId, PubSubNodeInfo node)
	{
		if (this.StandIn is IPubSubStandIn s)
		{
			await s.DeleteNodeAsync(accountId, node);
			return;
		}

		PubSubClient c = this.Client(accountId);
		await Ask<NodeEventArgs>(cb => c.DeleteNode(node.Service, node.Node, string.Empty, cb, null), "The node was not deleted.");
	}

	/// <summary>Removes every item from a node. The node, its settings and its subscribers stay.</summary>
	public async Task ClearAsync(Guid accountId, PubSubNodeInfo node)
	{
		if (this.StandIn is IPubSubStandIn s)
		{
			await s.ClearAsync(accountId, node);
			return;
		}

		PubSubClient c = this.Client(accountId);
		await Ask<NodeEventArgs>(cb => c.PurgeNode(node.Service, node.Node, cb, null), "The items were not removed.");
	}

	// ---- Items -----------------------------------------------------------------------------

	/// <summary>Items on a node, newest first. With <paramref name="latest"/>, only that many of the newest.</summary>
	public async Task<IReadOnlyList<PubSubItemInfo>> ItemsAsync(Guid accountId, PubSubNodeInfo node, int? latest = null)
	{
		if (this.StandIn is IPubSubStandIn s)
			return await s.ItemsAsync(accountId, node, latest);

		PubSubClient c = this.Client(accountId);
		ItemsEventArgs r;
		if (latest is int count)
		{
			try
			{
				r = await Ask<ItemsEventArgs>(cb => c.GetLatestItems(node.Service, node.Node, count, cb, null), "The node did not give its items.");
			}
			catch (InvalidOperationException)
			{
				// A service that cannot give "the latest few" gives all of them.
				r = await Ask<ItemsEventArgs>(cb => c.GetItems(node.Service, node.Node, cb, null), "The node did not give its items.");
			}
		}
		else
			r = await Ask<ItemsEventArgs>(cb => c.GetItems(node.Service, node.Node, cb, null), "The node did not give its items.");

		return [.. (r.Items ?? []).Select(i => new PubSubItemInfo(node.Node, i.ItemId, string.IsNullOrEmpty(i.Publisher) ? null : i.Publisher, i.Payload ?? string.Empty))];
	}

	/// <summary>Publishes an XML payload. A new item id is given when <paramref name="itemId"/> is empty.</summary>
	public async Task<string> PublishAsync(Guid accountId, PubSubNodeInfo node, string? itemId, string payloadXml)
	{
		if (this.StandIn is IPubSubStandIn s)
			return await s.PublishAsync(accountId, node, itemId, payloadXml);

		PubSubClient c = this.Client(accountId);
		ItemResultEventArgs r = await Ask<ItemResultEventArgs>(cb => c.Publish(node.Service, node.Node, itemId ?? string.Empty, payloadXml, cb, null), "The item was not published.");
		return r.ItemId;
	}

	public async Task RetractAsync(Guid accountId, PubSubNodeInfo node, string itemId)
	{
		if (this.StandIn is IPubSubStandIn s)
		{
			await s.RetractAsync(accountId, node, itemId);
			return;
		}

		PubSubClient c = this.Client(accountId);
		await Ask<IqResultEventArgs>(cb => c.Retract(node.Service, node.Node, itemId, cb, null), "The item was not removed.");
	}

	// ---- Watching --------------------------------------------------------------------------

	/// <summary>
	/// The names of the nodes the account subscribes to on its service, or in its personal
	/// storage. A subscription is kept by the service, so it is there again the next time.
	/// </summary>
	public async Task<IReadOnlyList<string>> WatchedAsync(Guid accountId, bool personal)
	{
		if (this.StandIn is IPubSubStandIn s)
			return await s.WatchedAsync(accountId, personal);

		PubSubClient c = this.Client(accountId);
		string me = this.Xmpp(accountId).BareJID;
		SubscriptionsEventArgs r = personal
			? await Ask<SubscriptionsEventArgs>(cb => c.GetMySubscriptions(me, string.Empty, cb, null), "The service did not list what you watch.")
			: await Ask<SubscriptionsEventArgs>(cb => c.GetMySubscriptions(cb, null), "The service did not list what you watch.");

		return [.. (r.Subscriptions ?? [])
			.Where(x => x.Status is NodeSubscriptionStatus.subscribed or NodeSubscriptionStatus.unconfigured && !string.IsNullOrEmpty(x.Node))
			.Select(x => x.Node)
			.Distinct(StringComparer.Ordinal)];
	}

	/// <summary>
	/// Subscribes the account to a node. What is published then arrives as it happens, through
	/// <see cref="ItemEvent"/>. Returns Pending when an owner has to say yes first.
	/// </summary>
	public async Task<SubscriberState> WatchAsync(Guid accountId, PubSubNodeInfo node)
	{
		if (this.StandIn is IPubSubStandIn s)
			return await s.WatchAsync(accountId, node);

		PubSubClient c = this.Client(accountId);
		string me = this.Xmpp(accountId).BareJID;
		SubscriptionEventArgs r = await Ask<SubscriptionEventArgs>(cb => c.Subscribe(node.Service, node.Node, me, (Waher.Networking.XMPP.DataForms.DataForm?)null, cb, null), "The node did not let you watch it.");
		return ToState(r.Status);
	}

	/// <summary>Ends the account's subscription to a node.</summary>
	public async Task UnwatchAsync(Guid accountId, PubSubNodeInfo node)
	{
		if (this.StandIn is IPubSubStandIn s)
		{
			await s.UnwatchAsync(accountId, node);
			return;
		}

		PubSubClient c = this.Client(accountId);
		string me = this.Xmpp(accountId).BareJID;
		await Ask<SubscriptionEventArgs>(cb => c.Unsubscribe(node.Service, node.Node, me, string.Empty, cb, null), "The node did not stop sending.");
	}

	/// <summary>Tells everyone listening what happened on a node. Called by the connection, and by stand-ins.</summary>
	internal void Raise(NodeItemEvent e) => this.ItemEvent?.Invoke(this, e);

	// ---- Settings --------------------------------------------------------------------------

	/// <summary>A node's settings. With no node: the settings the service gives a new one.</summary>
	public async Task<NodeSettings> SettingsAsync(Guid accountId, PubSubNodeInfo? node)
	{
		if (this.StandIn is IPubSubStandIn s)
			return await s.SettingsAsync(accountId, node);

		PubSubClient c = this.Client(accountId);
		ConfigurationEventArgs r = node is null
			? await Ask<ConfigurationEventArgs>(cb => c.GetDefaultNodeConfiguration(c.ComponentAddress, cb, null), "The service did not give its settings for new nodes.")
			: await Ask<ConfigurationEventArgs>(cb => c.GetNodeConfiguration(node.Service, node.Node, cb, null), "The node did not give its settings.");

		return FromConfiguration(r.Configuration);
	}

	public async Task SaveSettingsAsync(Guid accountId, PubSubNodeInfo node, NodeSettings settings)
	{
		if (this.StandIn is IPubSubStandIn s)
		{
			await s.SaveSettingsAsync(accountId, node, settings);
			return;
		}

		PubSubClient c = this.Client(accountId);
		await Ask<IqResultEventArgs>(cb => c.ConfigureNode(node.Service, node.Node, ToConfiguration(settings), cb, null), "The settings were not saved.");
	}

	internal static NodeSettings FromConfiguration(NodeConfiguration? c)
	{
		if (c is null)
			return new NodeSettings();

		return new NodeSettings
		{
			Title = c.Title ?? string.Empty,
			Description = string.Join('\n', c.Description ?? []),
			Access = c.AccessModel switch
			{
				NodeAccessModel.presence => NodeAccess.Presence,
				NodeAccessModel.roster => NodeAccess.Roster,
				NodeAccessModel.authorize => NodeAccess.Authorize,
				NodeAccessModel.whitelist => NodeAccess.Whitelist,
				_ => NodeAccess.Open,
			},
			Publishers = c.PublisherModel switch
			{
				PublisherModel.subscribers => NodePublishers.Subscribers,
				PublisherModel.open => NodePublishers.Open,
				_ => NodePublishers.Publishers,
			},
			KeepItems = c.PersistItems ?? true,
			MaxItems = c.MaxItems,
			SendLast = c.SendLastPublishedItem switch
			{
				SendLastPublishedItem.never => NodeLastItem.Never,
				SendLastPublishedItem.on_sub_and_presence => NodeLastItem.OnSubscribeAndPresence,
				_ => NodeLastItem.OnSubscribe,
			},
			TellWhenRemoved = c.NotifyRetract ?? true,
			IsCollection = c.NodeType == NodeType.collection,
		};
	}

	/// <summary>Only what the app shows is sent; everything else on the node stays as the service has it.</summary>
	internal static NodeConfiguration ToConfiguration(NodeSettings s)
	{
		NodeConfiguration c = new()
		{
			AccessModel = s.Access switch
			{
				NodeAccess.Presence => NodeAccessModel.presence,
				NodeAccess.Roster => NodeAccessModel.roster,
				NodeAccess.Authorize => NodeAccessModel.authorize,
				NodeAccess.Whitelist => NodeAccessModel.whitelist,
				_ => NodeAccessModel.open,
			},
			PublisherModel = s.Publishers switch
			{
				NodePublishers.Subscribers => PublisherModel.subscribers,
				NodePublishers.Open => PublisherModel.open,
				_ => PublisherModel.publishers,
			},
			PersistItems = s.KeepItems,
			SendLastPublishedItem = s.SendLast switch
			{
				NodeLastItem.Never => SendLastPublishedItem.never,
				NodeLastItem.OnSubscribeAndPresence => SendLastPublishedItem.on_sub_and_presence,
				_ => SendLastPublishedItem.on_sub,
			},
			NotifyRetract = s.TellWhenRemoved,
		};

		if (!string.IsNullOrWhiteSpace(s.Title))
			c.Title = s.Title.Trim();
		if (!string.IsNullOrWhiteSpace(s.Description))
			c.Description = s.Description.Trim().Split('\n');
		if (s.MaxItems is int max)
			c.MaxItems = max;
		if (s.IsCollection)
			c.NodeType = NodeType.collection;

		return c;
	}

	// ---- People ----------------------------------------------------------------------------

	/// <summary>Who has a role on the node, and who subscribes or has asked to. Only an owner is told.</summary>
	public async Task<(IReadOnlyList<NodePerson> People, IReadOnlyList<NodeSubscriber> Subscribers)> PeopleAsync(Guid accountId, PubSubNodeInfo node)
	{
		if (this.StandIn is IPubSubStandIn s)
			return await s.PeopleAsync(accountId, node);

		PubSubClient c = this.Client(accountId);
		AffiliationsEventArgs a = await Ask<AffiliationsEventArgs>(cb => c.GetAffiliations(node.Service, node.Node, cb, null), "The node did not say who may do what.");
		SubscriptionsEventArgs b = await Ask<SubscriptionsEventArgs>(cb => c.GetSubscriptions(node.Service, node.Node, cb, null), "The node did not say who subscribes.");

		return (
			[.. (a.Affiliations ?? []).Where(x => x.Status != AffiliationStatus.none).Select(x => new NodePerson(x.Jid, ToRole(x.Status))).OrderBy(p => Order(p.Role)).ThenBy(p => p.Jid, StringComparer.OrdinalIgnoreCase)],
			[.. (b.Subscriptions ?? []).Where(x => x.Status != NodeSubscriptionStatus.none).Select(x => new NodeSubscriber(x.Jid, ToState(x.Status), string.IsNullOrEmpty(x.SubscriptionId) ? null : x.SubscriptionId)).OrderBy(x => x.State != SubscriberState.Pending).ThenBy(x => x.Jid, StringComparer.OrdinalIgnoreCase)]);
	}

	/// <summary>Gives somebody a role on the node, or takes it away with <see cref="NodeRole.None"/>.</summary>
	public async Task SetRoleAsync(Guid accountId, PubSubNodeInfo node, string jid, NodeRole role)
	{
		if (this.StandIn is IPubSubStandIn s)
		{
			await s.SetRoleAsync(accountId, node, jid, role);
			return;
		}

		PubSubClient c = this.Client(accountId);
		KeyValuePair<string, AffiliationStatus>[] change = [new(jid.Trim(), role switch
		{
			NodeRole.Owner => AffiliationStatus.owner,
			NodeRole.Publisher => AffiliationStatus.publisher,
			NodeRole.PublishOnly => AffiliationStatus.publishOnly,
			NodeRole.Member => AffiliationStatus.member,
			NodeRole.Blocked => AffiliationStatus.outcast,
			_ => AffiliationStatus.none,
		})];
		await Ask<IqResultEventArgs>(cb => c.UpdateAffiliations(node.Service, node.Node, change, cb, null), "The role was not changed.");
	}

	/// <summary>Lets somebody subscribe who asked to, or ends somebody's subscription with <see cref="SubscriberState.None"/>.</summary>
	public async Task SetSubscriberAsync(Guid accountId, PubSubNodeInfo node, string jid, SubscriberState state)
	{
		if (this.StandIn is IPubSubStandIn s)
		{
			await s.SetSubscriberAsync(accountId, node, jid, state);
			return;
		}

		PubSubClient c = this.Client(accountId);
		KeyValuePair<string, NodeSubscriptionStatus>[] change = [new(jid.Trim(), state switch
		{
			SubscriberState.Subscribed => NodeSubscriptionStatus.subscribed,
			SubscriberState.Pending => NodeSubscriptionStatus.pending,
			SubscriberState.Unconfigured => NodeSubscriptionStatus.unconfigured,
			_ => NodeSubscriptionStatus.none,
		})];
		await Ask<IqResultEventArgs>(cb => c.UpdateSubscriptions(node.Service, node.Node, change, cb, null), "The subscription was not changed.");
	}

	private static int Order(NodeRole r) => r switch
	{
		NodeRole.Owner => 0,
		NodeRole.Publisher => 1,
		NodeRole.PublishOnly => 2,
		NodeRole.Member => 3,
		_ => 4,
	};

	private static NodeRole ToRole(AffiliationStatus s) => s switch
	{
		AffiliationStatus.owner => NodeRole.Owner,
		AffiliationStatus.publisher => NodeRole.Publisher,
		AffiliationStatus.publishOnly => NodeRole.PublishOnly,
		AffiliationStatus.member => NodeRole.Member,
		AffiliationStatus.outcast => NodeRole.Blocked,
		_ => NodeRole.None,
	};

	private static SubscriberState ToState(NodeSubscriptionStatus s) => s switch
	{
		NodeSubscriptionStatus.subscribed => SubscriberState.Subscribed,
		NodeSubscriptionStatus.pending => SubscriberState.Pending,
		NodeSubscriptionStatus.unconfigured => SubscriberState.Unconfigured,
		_ => SubscriberState.None,
	};

	// ---- Requests to subscribe -------------------------------------------------------------

	/// <summary>The requests to subscribe to nodes the accounts own, that nobody has answered yet.</summary>
	public IReadOnlyList<NodeRequest> Requests
	{
		get
		{
			lock (this.requests)
				return [.. this.requests];
		}
	}

	/// <summary>Adds a request as it is, for previews and UI tests.</summary>
	internal void Preview(NodeRequest request)
	{
		lock (this.requests)
			this.requests.Add(request);

		this.RequestsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Lets the one who asked subscribe, or says no.</summary>
	public async Task AnswerAsync(NodeRequest request, bool allow)
	{
		lock (this.requests)
			this.requests.Remove(request);

		this.RequestsChanged?.Invoke(this, EventArgs.Empty);

		if (request.Args is SubscriptionRequestEventArgs e)
		{
			if (allow)
				await e.Approve();
			else
				await e.Reject();
		}
		else if (this.StandIn is IPubSubStandIn s)
			await s.SetSubscriberAsync(request.AccountId, new PubSubNodeInfo(request.Service, request.Node, null, false), request.Jid, allow ? SubscriberState.Subscribed : SubscriberState.None);
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
			ServiceItemsDiscoveryEventArgs items = await client.ServiceItemsDiscoveryAsync(client.Domain).WaitAsync(wait);
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
			c.ItemNotification += (_, e) =>
			{
				this.Raise(new NodeItemEvent(id, e.From ?? component, e.NodeName, new PubSubItemInfo(e.NodeName, e.ItemId, string.IsNullOrEmpty(e.Publisher) ? null : e.Publisher, Payload(e.Item))
				{
					Published = e.Delay ?? DateTime.Now,
				}));
				return Task.CompletedTask;
			};
			c.ItemRetracted += (_, e) =>
			{
				this.Raise(new NodeItemEvent(id, e.From ?? component, e.NodeName, null, e.ItemId));
				return Task.CompletedTask;
			};
			c.NodePurged += (_, e) =>
			{
				this.Raise(new NodeItemEvent(id, e.From ?? component, e.NodeName, null, null, true));
				return Task.CompletedTask;
			};
			c.SubscriptionRequest += (_, e) =>
			{
				lock (this.requests)
				{
					this.requests.RemoveAll(r => r.AccountId == id && r.Node == e.NodeName && string.Equals(r.Jid, e.Jid, StringComparison.OrdinalIgnoreCase));
					this.requests.Add(new NodeRequest(id, e.From ?? component, e.NodeName, e.Jid, DateTime.Now) { Args = e });
				}

				this.RequestsChanged?.Invoke(this, EventArgs.Empty);
				return Task.CompletedTask;
			};

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

	/// <summary>What was published, from the item a notification carries.</summary>
	internal static string Payload(System.Xml.XmlElement? item)
	{
		if (item is null)
			return string.Empty;

		return item.LocalName == "item" && item.NamespaceURI.StartsWith(PubSubClient.NamespacePubSub, StringComparison.Ordinal) ? item.InnerXml : item.OuterXml;
	}

	public ValueTask DisposeAsync()
	{
		foreach (PubSubClient c in this.clients.Values)
			c.Dispose();
		this.clients.Clear();
		return ValueTask.CompletedTask;
	}
}
