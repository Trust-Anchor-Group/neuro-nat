using Neuro.AA.Engine.Accounts;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Concentrator;
using Xunit.Abstractions;

namespace Neuro.AA.Engine.Tests;

/// <summary>
/// Exploratory probe against the local test Neuron: roster handshake with the Neuron's own
/// client and access to its concentrator. Runs only when NEUROAA_TEST_* and
/// NEUROAA_TEST_NEURON (the Neuron's own bare JID) are set.
/// </summary>
[Collection("Live Neuron")]
public class NeuronProbeTests(ITestOutputHelper output)
{
	[Fact]
	public async Task ProbeConcentratorAccess()
	{
		string? host = Environment.GetEnvironmentVariable("NEUROAA_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NEUROAA_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NEUROAA_TEST_PASSWORD");
		string? neuron = Environment.GetEnvironmentVariable("NEUROAA_TEST_NEURON");

		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(neuron))
		{
			output.WriteLine("Not run: NEUROAA_TEST_HOST/USER/PASSWORD/NEURON not set.");
			return;
		}

		EngineRuntime.Initialize();
		InMemoryCredentialVault vault = new();
		string file = Path.Combine(Path.GetTempPath(), "NeuroAA.Probe." + Guid.NewGuid().ToString("N"), "accounts.json");
		await using AccountManager manager = new(new JsonAccountStore(file), vault);
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: true);
		await StreamSnifferTests.WaitFor(() => s.Status is ConnectionStatus.Connected or ConnectionStatus.Failed, 20000);
		Assert.Equal(ConnectionStatus.Connected, s.Status);
		XmppClient c = s.Client!;

		string? neuronFullJid = null;
		c.OnPresenceSubscribe += async (_, e) => { output.WriteLine($"Subscribe request from {e.From}; accepting."); await e.Accept(); };
		c.OnPresence += (_, e) =>
		{
			output.WriteLine($"Presence from {e.From} (type {e.Type}, availability {e.Availability}, online {e.IsOnline}).");
			if (string.Equals(e.FromBareJID, neuron, StringComparison.OrdinalIgnoreCase) && e.From.Contains('/'))
				neuronFullJid = e.From;
			return Task.CompletedTask;
		};

		RosterItem? item = c.GetRosterItem(neuron);
		output.WriteLine($"Roster item before: {item?.BareJid} state={item?.State} pendingOut={item?.PendingSubscription}");

		await c.RequestPresenceSubscription(neuron);
		await Task.Delay(8000);

		item = c.GetRosterItem(neuron);
		output.WriteLine($"Roster item after: {item?.BareJid} state={item?.State} pendingOut={item?.PendingSubscription} full={neuronFullJid}");

		neuronFullJid ??= Environment.GetEnvironmentVariable("NEUROAA_TEST_NEURON_FULL");

		if (neuronFullJid is null)
		{
			output.WriteLine("No presence from the Neuron yet (subscription not accepted on the Neuron side).");
			output.WriteLine(string.Join(Environment.NewLine, s.Sniffer.Snapshot().TakeLast(30).Select(l => $"{l.Direction}: {l.Text}")));
			return;
		}

		ConcentratorClient concentrator = new(c);
		DataSourceReference[] sources = await concentrator.GetRootDataSourcesAsync(neuronFullJid);
		output.WriteLine("Data sources: " + string.Join(", ", sources.Select(x => x.SourceID)));

		NodeInformation[] reportNodes = await concentrator.GetRootNodesAsync(neuronFullJid, "Reports", false, false, "en", string.Empty, string.Empty, string.Empty);
		output.WriteLine("Reports root nodes: " + string.Join(", ", reportNodes.Select(n => $"{n.NodeId} sniffable={n.Sniffable} children={n.HasChildren}")));

		Sniffing.StreamSniffer events = new("events");
		List<Sniffing.SnifferLine> got = [];
		events.LineAdded += (_, l) => { lock (got) got.Add(l); };

		TaskCompletionSource<bool> registered = new();
		await concentrator.RegisterSniffer(neuronFullJid, "Events", "Reports", string.Empty, DateTime.Now.AddMinutes(5), events,
			string.Empty, string.Empty, string.Empty, (_, e) =>
			{
				output.WriteLine($"Sniffer registration ok={e.Ok} id={e.SnifferId} error={e.ErrorText}");
				registered.TrySetResult(e.Ok);
				return Task.CompletedTask;
			}, null);
		await Task.WhenAny(registered.Task, Task.Delay(10000));

		int seconds = int.TryParse(Environment.GetEnvironmentVariable("NEUROAA_PROBE_WAIT"), out int w) ? w : 20;
		await Task.Delay(seconds * 1000);

		lock (got)
		{
			output.WriteLine($"Live event lines: {got.Count}");
			foreach (Sniffing.SnifferLine l in got.Take(20))
				output.WriteLine($"  {l.Timestamp:HH:mm:ss} {l.Direction}: {l.Text.Replace("\r", "").Replace("\n", " | ")}");
		}
	}
}
