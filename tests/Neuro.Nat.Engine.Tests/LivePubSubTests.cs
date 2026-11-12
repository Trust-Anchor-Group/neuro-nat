using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.PubSub;

namespace Neuro.Nat.Engine.Tests;

/// <summary>Publish/subscribe on the local test Neuron: a node is made, used and removed.</summary>
[Collection("Live Neuron")]
public class LivePubSubTests(ITestOutputHelper output)
{
	[Fact]
	public async Task CreatePublishReadRetractDelete()
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
			Live.Skip("NAT_TEST_HOST", "NAT_TEST_USER", "NAT_TEST_PASSWORD");

		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Live." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);
		await using PubSubService pubsub = new(manager);
		await s.ConnectAsync();
		await StreamSnifferTests.WaitFor(() => pubsub.IsAvailable(s.Profile.Id) || pubsub.Problem(s.Profile.Id) is not null, 30000);
		output.WriteLine($"Service: {pubsub.Service(s.Profile.Id)} {pubsub.Problem(s.Profile.Id)}");
		Assert.True(pubsub.IsAvailable(s.Profile.Id));

		string name = "nat-test-" + Guid.NewGuid().ToString("N")[..8];
		PubSubNodeInfo node = await pubsub.CreateNodeAsync(s.Profile.Id, name);
		try
		{
			IReadOnlyList<PubSubNodeInfo> nodes = await pubsub.NodesAsync(s.Profile.Id);
			output.WriteLine("Nodes: " + string.Join(", ", nodes.Select(n => n.Node)));
			Assert.Contains(nodes, n => n.Node == name);

			string id = await pubsub.PublishAsync(s.Profile.Id, node, null, "<reading xmlns='urn:nat:test' value='21.5'/>");
			IReadOnlyList<PubSubItemInfo> items = await pubsub.ItemsAsync(s.Profile.Id, node);
			output.WriteLine($"Published {id}; items: " + string.Join(", ", items.Select(i => $"{i.ItemId} {i.PayloadXml}")));
			Assert.Contains(items, i => i.ItemId == id && i.PayloadXml.Contains("21.5"));

			await pubsub.RetractAsync(s.Profile.Id, node, id);
			Assert.DoesNotContain(await pubsub.ItemsAsync(s.Profile.Id, node), i => i.ItemId == id);

			IReadOnlyList<PubSubNodeInfo> personal = await pubsub.PersonalNodesAsync(s.Profile.Id);
			output.WriteLine("Personal: " + string.Join(", ", personal.Select(n => n.Node)));
		}
		finally
		{
			await pubsub.DeleteNodeAsync(s.Profile.Id, node);
		}

		Assert.DoesNotContain(await pubsub.NodesAsync(s.Profile.Id), n => n.Node == name);
	}
}
