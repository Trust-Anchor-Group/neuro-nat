using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.PubSub;
using Xunit.Abstractions;

namespace Neuro.AA.Engine.Tests;

/// <summary>Publish/subscribe on the local test Neuron: a node is made, used and removed.</summary>
[Collection("Live Neuron")]
public class LivePubSubTests(ITestOutputHelper output)
{
	[Fact]
	public async Task CreatePublishReadRetractDelete()
	{
		string? host = Environment.GetEnvironmentVariable("NEUROAA_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NEUROAA_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NEUROAA_TEST_PASSWORD");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
		{
			output.WriteLine("Not run: NEUROAA_TEST_* not set.");
			return;
		}

		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "NeuroAA.Live." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);
		await using PubSubService pubsub = new(manager);
		await s.ConnectAsync();
		await StreamSnifferTests.WaitFor(() => pubsub.IsAvailable(s.Profile.Id) || pubsub.Problem(s.Profile.Id) is not null, 30000);
		output.WriteLine($"Service: {pubsub.Service(s.Profile.Id)} {pubsub.Problem(s.Profile.Id)}");
		Assert.True(pubsub.IsAvailable(s.Profile.Id));

		string name = "neuroaa-test-" + Guid.NewGuid().ToString("N")[..8];
		PubSubNodeInfo node = await pubsub.CreateNodeAsync(s.Profile.Id, name);
		try
		{
			IReadOnlyList<PubSubNodeInfo> nodes = await pubsub.NodesAsync(s.Profile.Id);
			output.WriteLine("Nodes: " + string.Join(", ", nodes.Select(n => n.Node)));
			Assert.Contains(nodes, n => n.Node == name);

			string id = await pubsub.PublishAsync(s.Profile.Id, node, null, "<reading xmlns='urn:neuroaa:test' value='21.5'/>");
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
