using System.Xml;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.PubSub;
using Waher.Networking.XMPP.PubSub;

namespace Neuro.AA.Engine.Tests;

/// <summary>
/// Publish/subscribe behind the screen: a node's settings to and from what the service speaks,
/// refusals in plain words, watching, and requests to subscribe.
/// </summary>
public class PubSubServiceTests
{
	private const string Service = "pubsub.lab.tagroot.io";
	private const string Me = "ops@lab.tagroot.io";

	private static async Task<(PubSubService PubSub, PreviewPubSub Nodes, Guid Account)> SampleAsync()
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "NeuroAA.PubSub." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.tagroot.io", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);

		PubSubService pubsub = new(manager);
		PreviewPubSub nodes = new PreviewPubSub(pubsub, Service).Account(ops.Profile.Id, Me);
		pubsub.StandIn = nodes;
		return (pubsub, nodes, ops.Profile.Id);
	}

	[Fact]
	public void SettingsGoToTheServiceAndBackUnchanged()
	{
		NodeSettings s = new()
		{
			Title = "Alerts from the lab",
			Description = "What the lab's Neurons want an operator to see.\nOne item for each alert.",
			Access = NodeAccess.Authorize,
			Publishers = NodePublishers.Subscribers,
			KeepItems = true,
			MaxItems = 20,
			SendLast = NodeLastItem.OnSubscribeAndPresence,
			TellWhenRemoved = false,
		};

		NodeConfiguration c = PubSubService.ToConfiguration(s);
		Assert.Equal(NodeAccessModel.authorize, c.AccessModel);
		Assert.Equal(PublisherModel.subscribers, c.PublisherModel);
		Assert.Equal(true, c.PersistItems);
		Assert.Equal(20, c.MaxItems);
		Assert.Equal(SendLastPublishedItem.on_sub_and_presence, c.SendLastPublishedItem);
		Assert.Equal(false, c.NotifyRetract);
		Assert.Equal(2, c.Description!.Length);
		Assert.Equal(s, PubSubService.FromConfiguration(c));

		// Every choice has its counterpart.
		foreach (NodeAccess a in Enum.GetValues<NodeAccess>())
			Assert.Equal(a, PubSubService.FromConfiguration(PubSubService.ToConfiguration(new NodeSettings { Access = a })).Access);
		foreach (NodePublishers p in Enum.GetValues<NodePublishers>())
			Assert.Equal(p, PubSubService.FromConfiguration(PubSubService.ToConfiguration(new NodeSettings { Publishers = p })).Publishers);
		foreach (NodeLastItem l in Enum.GetValues<NodeLastItem>())
			Assert.Equal(l, PubSubService.FromConfiguration(PubSubService.ToConfiguration(new NodeSettings { SendLast = l })).SendLast);
	}

	[Fact]
	public void WhatIsNotSetIsLeftToTheService()
	{
		// No title, no description, no limit: none of them is sent, so the service keeps its own.
		NodeConfiguration c = PubSubService.ToConfiguration(new NodeSettings());
		Assert.Null(c.Title);
		Assert.Null(c.Description);
		Assert.Null(c.MaxItems);
		Assert.Null(c.NodeType);
		Assert.Equal(NodeType.collection, PubSubService.ToConfiguration(new NodeSettings { IsCollection = true }).NodeType);

		// A service that says nothing about a setting gets the usual one.
		NodeSettings s = PubSubService.FromConfiguration(new NodeConfiguration());
		Assert.Equal(new NodeSettings(), s);
		Assert.Equal(new NodeSettings(), PubSubService.FromConfiguration(null));
	}

	[Theory]
	[InlineData("ForbiddenException", "The item was not published. You are not allowed to. Ask one of the node's owners.")]
	[InlineData("NotAuthorizedException", "The item was not published. You are not allowed to. Ask one of the node's owners.")]
	[InlineData("ItemNotFoundException", "The item was not published. It is not there any more.")]
	[InlineData("FeatureNotImplementedException", "The item was not published. The service does not offer this.")]
	[InlineData("ConflictException", "The item was not published. There is one with that name already.")]
	[InlineData("NotAcceptableException", "The item was not published. The service did not accept what was sent.")]
	[InlineData("ResourceConstraintException", "The item was not published. The service has no room for more.")]
	public void ARefusalSaysWhatCanBeDoneAboutIt(string error, string expected)
		=> Assert.Equal(expected, PubSubService.Explain(error, "the server's own words", "The item was not published."));

	[Fact]
	public void AnUnknownRefusalKeepsTheServicesWords()
	{
		Assert.Equal("The item was not published. Payload too big.", PubSubService.Explain("InternalServerErrorException", " Payload too big. ", "The item was not published."));
		Assert.Equal("The item was not published.", PubSubService.Explain(null, null, "The item was not published."));
	}

	[Fact]
	public void TheItemOfANotificationIsWhatWasPublished()
	{
		XmlDocument doc = new();
		doc.LoadXml("<item xmlns='http://jabber.org/protocol/pubsub#event' id='a-1'><alert xmlns='urn:example:alerts' level='info'/></item>");
		Assert.Equal("<alert xmlns=\"urn:example:alerts\" level=\"info\" />", PubSubService.Payload(doc.DocumentElement));

		doc.LoadXml("<alert xmlns='urn:example:alerts' level='info'/>");
		Assert.Equal("<alert xmlns=\"urn:example:alerts\" level=\"info\" />", PubSubService.Payload(doc.DocumentElement));
		Assert.Equal(string.Empty, PubSubService.Payload(null));
	}

	[Fact]
	public async Task WatchingANodeBringsWhatIsPublishedOnIt()
	{
		(PubSubService pubsub, PreviewPubSub nodes, Guid me) = await SampleAsync();
		nodes.Add("alerts", Me).Add("other", Me);
		PubSubNodeInfo alerts = (await pubsub.NodesAsync(me)).Single(n => n.Node == "alerts");
		List<NodeItemEvent> seen = [];
		pubsub.ItemEvent += (_, e) => seen.Add(e);

		nodes.Arrive(Service, "alerts", "a-1", "<a xmlns='urn:x'/>", "robin@lab.tagroot.io");
		Assert.Empty(seen);
		Assert.Empty(await pubsub.WatchedAsync(me, personal: false));

		Assert.Equal(SubscriberState.Subscribed, await pubsub.WatchAsync(me, alerts));
		Assert.Equal(["alerts"], await pubsub.WatchedAsync(me, personal: false));

		nodes.Arrive(Service, "alerts", "a-2", "<a xmlns='urn:x'/>", "robin@lab.tagroot.io");
		nodes.Arrive(Service, "other", "o-1", "<a xmlns='urn:x'/>", "robin@lab.tagroot.io");
		NodeItemEvent came = Assert.Single(seen);
		Assert.Equal((me, Service, "alerts", "a-2", "robin@lab.tagroot.io"), (came.AccountId, came.Service, came.Node, came.Item!.ItemId, came.Item.Publisher));
		Assert.NotNull(came.Item.Published);

		// Removing and emptying are told too.
		await pubsub.RetractAsync(me, alerts, "a-2");
		Assert.Equal("a-2", seen[^1].RemovedItemId);
		Assert.Null(seen[^1].Item);
		await pubsub.ClearAsync(me, alerts);
		Assert.True(seen[^1].Cleared);
		Assert.Empty(await pubsub.ItemsAsync(me, alerts));

		await pubsub.UnwatchAsync(me, alerts);
		int before = seen.Count;
		nodes.Arrive(Service, "alerts", "a-3", "<a xmlns='urn:x'/>", "robin@lab.tagroot.io");
		Assert.Equal(before, seen.Count);
	}

	[Fact]
	public async Task TheLatestItemsComeFirstAndOnlyAsManyAsAskedFor()
	{
		(PubSubService pubsub, PreviewPubSub nodes, Guid me) = await SampleAsync();
		nodes.Add("log", Me);
		for (int i = 1; i <= 8; i++)
			nodes.Item(Service, "log", "n-" + i, $"<n xmlns='urn:x' v='{i}'/>");

		PubSubNodeInfo log = (await pubsub.NodesAsync(me)).Single();
		Assert.Equal(["n-8", "n-7", "n-6"], (await pubsub.ItemsAsync(me, log, 3)).Select(i => i.ItemId));
		Assert.Equal(8, (await pubsub.ItemsAsync(me, log)).Count);

		// Publishing under an id that is there takes its place; without one, the service gives one.
		Assert.Equal("n-3", await pubsub.PublishAsync(me, log, "n-3", "<n xmlns='urn:x' v='33'/>"));
		IReadOnlyList<PubSubItemInfo> items = await pubsub.ItemsAsync(me, log);
		Assert.Equal(8, items.Count);
		Assert.Equal(("n-3", "<n xmlns='urn:x' v='33'/>", Me), (items[0].ItemId, items[0].PayloadXml, items[0].Publisher));
		string given = await pubsub.PublishAsync(me, log, null, "<n xmlns='urn:x'/>");
		Assert.False(string.IsNullOrEmpty(given));
		Assert.Equal(given, (await pubsub.ItemsAsync(me, log, 1))[0].ItemId);
	}

	[Fact]
	public async Task NodesLiveInCollectionsAndAreMadeWithSettings()
	{
		(PubSubService pubsub, PreviewPubSub nodes, Guid me) = await SampleAsync();
		nodes.Add("lab", Me, collection: true).Add("lab/alerts", Me, "Alerts", parent: "lab").Add("top", "lead@lab.tagroot.io");

		IReadOnlyList<PubSubNodeInfo> top = await pubsub.NodesAsync(me);
		Assert.Equal(["lab", "top"], top.Select(n => n.Node));
		Assert.True(top[0].IsCollection);
		PubSubNodeInfo inside = Assert.Single(await pubsub.NodesAsync(me, "lab"));
		Assert.Equal(("lab/alerts", "Alerts", "lab"), (inside.Node, inside.Name, inside.Parent));

		PubSubNodeInfo made = await pubsub.CreateNodeAsync(me, "lab/doors", new NodeSettings { Title = "Doors", MaxItems = 5 }, "lab");
		Assert.Equal(("lab/doors", "Doors", "lab"), (made.Node, made.Name, made.Parent));
		Assert.Equal(5, (await pubsub.SettingsAsync(me, made)).MaxItems);
		Assert.Equal(2, (await pubsub.NodesAsync(me, "lab")).Count);

		InvalidOperationException taken = await Assert.ThrowsAsync<InvalidOperationException>(() => pubsub.CreateNodeAsync(me, "top"));
		Assert.Equal("The node was not created. There is one with that name already.", taken.Message);

		// Somebody else's node: its settings, its people and its end are the owners'.
		PubSubNodeInfo theirs = top[1];
		Assert.Equal("The node did not give its settings. You are not allowed to. Ask one of the node's owners.", (await Assert.ThrowsAsync<InvalidOperationException>(() => pubsub.SettingsAsync(me, theirs))).Message);
		Assert.Equal("The node was not deleted. You are not allowed to. Ask one of the node's owners.", (await Assert.ThrowsAsync<InvalidOperationException>(() => pubsub.DeleteNodeAsync(me, theirs))).Message);
		Assert.Equal("The node did not say who may do what. You are not allowed to. Ask one of the node's owners.", (await Assert.ThrowsAsync<InvalidOperationException>(() => pubsub.PeopleAsync(me, theirs))).Message);

		// Deleting a collection takes what is inside with it.
		await pubsub.DeleteNodeAsync(me, top[0]);
		Assert.Equal(["top"], (await pubsub.NodesAsync(me)).Select(n => n.Node));
		Assert.False(nodes.Has(Service, "lab/alerts"));
	}

	[Fact]
	public async Task RequestsToSubscribeAreAnsweredOnce()
	{
		(PubSubService pubsub, PreviewPubSub nodes, Guid me) = await SampleAsync();
		nodes.Add("alerts", Me, settings: new NodeSettings { Access = NodeAccess.Authorize });
		PubSubNodeInfo alerts = (await pubsub.NodesAsync(me)).Single();
		int changes = 0;
		pubsub.RequestsChanged += (_, _) => changes++;

		NodeRequest pat = new(me, Service, "alerts", "pat@partner.example", DateTime.Now);
		NodeRequest lee = new(me, Service, "alerts", "lee@lab.tagroot.io", DateTime.Now);
		pubsub.Preview(pat);
		pubsub.Preview(lee);
		Assert.Equal(2, changes);
		Assert.Equal([pat, lee], pubsub.Requests);

		await pubsub.AnswerAsync(pat, allow: true);
		await pubsub.AnswerAsync(lee, allow: false);
		Assert.Empty(pubsub.Requests);
		Assert.Equal(4, changes);

		(IReadOnlyList<NodePerson> people, IReadOnlyList<NodeSubscriber> subscribers) = await pubsub.PeopleAsync(me, alerts);
		Assert.Equal([new NodePerson(Me, NodeRole.Owner)], people);
		Assert.Equal([new NodeSubscriber("pat@partner.example", SubscriberState.Subscribed)], subscribers);

		// Roles: given, changed, taken away; the last owner stays one.
		await pubsub.SetRoleAsync(me, alerts, "robin@lab.tagroot.io", NodeRole.Publisher);
		await pubsub.SetRoleAsync(me, alerts, "robin@lab.tagroot.io", NodeRole.Blocked);
		Assert.Contains(new NodePerson("robin@lab.tagroot.io", NodeRole.Blocked), (await pubsub.PeopleAsync(me, alerts)).People);
		await pubsub.SetRoleAsync(me, alerts, "robin@lab.tagroot.io", NodeRole.None);
		Assert.Single((await pubsub.PeopleAsync(me, alerts)).People);
		await Assert.ThrowsAsync<InvalidOperationException>(() => pubsub.SetRoleAsync(me, alerts, Me, NodeRole.Member));
	}
}
