using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Chat;
using Neuro.AA.Engine.Events;
using Neuro.AA.Engine.Topology;
using Xunit.Abstractions;

namespace Neuro.AA.Engine.Tests;

/// <summary>Topology from the local test Neuron, through its chat admin.</summary>
[Collection("Live Neuron")]
public class LiveTopologyTests(ITestOutputHelper output)
{
	[Fact]
	public async Task AsksTheNeuronHowItIsConnected()
	{
		string? host = Environment.GetEnvironmentVariable("NEUROAA_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NEUROAA_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NEUROAA_TEST_PASSWORD");
		string? neuron = Environment.GetEnvironmentVariable("NEUROAA_TEST_NEURON");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(neuron))
		{
			output.WriteLine("Not run: NEUROAA_TEST_* not set.");
			return;
		}

		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "NeuroAA.Live." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		await using ChatService chat = new(manager, monitors);
		await s.ConnectAsync();
		await StreamSnifferTests.WaitFor(() => monitors.Neurons.Any(n => n.State == WatchState.Live), 30000);

		System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
		TopologySnapshot snap = await new TopologyService(monitors, chat).ScanAsync(includeGroups: true);
		output.WriteLine($"Scan took {sw.Elapsed.TotalSeconds:F1} s");
		foreach (TopologyNode n in snap.Nodes)
			output.WriteLine($"Node {n.Id} '{n.Label}' {n.Kind} {n.NeuronJid}");
		foreach (TopologyEdge e in snap.Edges)
			output.WriteLine($"Edge {e.From} -> {e.To} {e.Kind} {e.Label}");
		foreach (TopologyProblem p in snap.Problems)
			output.WriteLine($"Problem {p.Label}: {p.Message}");

		Assert.Contains(snap.Nodes, n => n.Kind == TopologyNodeKind.Neuron && string.Equals(n.NeuronJid, neuron, StringComparison.OrdinalIgnoreCase));
		Assert.Contains(snap.Edges, e => e.Kind == TopologyLink.Parent);
	}
}
