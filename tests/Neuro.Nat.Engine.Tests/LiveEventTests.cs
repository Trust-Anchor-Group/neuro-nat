using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;
using Waher.Networking.XMPP;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// End to end against the local test Neuron: the monitor finds the Neuron in the roster,
/// starts the event stream, and an event logged on the Neuron (through chat admin, which
/// runs script for notification addresses) arrives in the hub.
/// Needs NAT_TEST_HOST/USER/PASSWORD and NAT_TEST_NEURON (the Neuron's bare JID).
/// </summary>
[Collection("Live Neuron")]
public class LiveEventTests(ITestOutputHelper output)
{
	[Fact]
	public async Task EventLoggedOnNeuronArrivesInHub()
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? neuron = Environment.GetEnvironmentVariable("NAT_TEST_NEURON");

		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(neuron))
			Live.Skip("NAT_TEST_HOST", "NAT_TEST_USER", "NAT_TEST_PASSWORD", "NAT_TEST_NEURON");

		EngineRuntime.Initialize();
		string file = Path.Combine(Path.GetTempPath(), "Nat.Live." + Guid.NewGuid().ToString("N"), "accounts.json");
		await using AccountManager manager = new(new JsonAccountStore(file), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);

		EventHub hub = new();
		await using NeuronMonitor monitor = new(s, hub);
		await s.ConnectAsync();

		await StreamSnifferTests.WaitFor(() => monitor.Neurons.Any(n => n.State == WatchState.Live && string.Equals(n.BareJid, neuron, StringComparison.OrdinalIgnoreCase)), 30000);
		WatchedNeuron w = monitor.Neurons.First(n => string.Equals(n.BareJid, neuron, StringComparison.OrdinalIgnoreCase));
		output.WriteLine($"Live: {w.FullJid} since {w.LiveSince:HH:mm:ss}");

		string marker = "NAT integration " + Guid.NewGuid().ToString("N")[..8];
		await s.Client!.SendChatMessage(w.FullJid!, $"LogError(\"{marker}: SocketException Connection reset by peer\")");

		await StreamSnifferTests.WaitFor(() => hub.Snapshot().Any(e => e.Message.Contains(marker)), 20000);
		NeuronEvent got = hub.Snapshot().First(e => e.Message.Contains(marker));

		output.WriteLine($"Got {got.Severity} from {got.Source.NeuronJid}: {got.Message}");
		Assert.Equal(EventSeverity.Error, got.Severity);
		Assert.Contains(hub.Issues(), i => i.Fingerprint == got.Fingerprint);

		// History: the Neuron's event log is read back when the stream starts.
		try
		{
			await StreamSnifferTests.WaitFor(() => monitor.Neurons.Any(n => n.HistoryState is HistoryState.Loaded or HistoryState.Failed), 120000);
		}
		finally
		{
			foreach (var l in s.Sniffer.Snapshot().Where(l => l.Text.Contains("Search Event Log") || l.Text.Contains("getCommandParameters") || l.Text.Contains("executeNodeQuery") || l.Text.Contains("query") || l.Direction >= Sniffing.SnifferDirection.Warning).TakeLast(12))
				output.WriteLine($"  [{l.Direction}] {l.Text[..Math.Min(400, l.Text.Length)]}");
		}
		WatchedNeuron after = monitor.Neurons.First(n => string.Equals(n.BareJid, neuron, StringComparison.OrdinalIgnoreCase));
		output.WriteLine($"History: {after.HistoryState}, {after.HistoryCount} events since {after.HistoryFrom:yyyy-MM-dd HH:mm} {after.HistoryError}");
		foreach (NeuronEvent h in hub.Snapshot().Where(e => e.FromHistory).Take(5))
			output.WriteLine($"  {h.Timestamp:HH:mm:ss} {h.Type}/{h.Level} {h.Title}");

		Assert.Equal(HistoryState.Loaded, after.HistoryState);

		// Health: performance samples and software version.
		NeuronHealth? health = await monitor.ReadHealthAsync(neuron);
		Assert.NotNull(health);
		output.WriteLine($"Health: {health!.Software} on {health.OperatingSystem}; CPU {health.Cpu.Latest} {health.Cpu.Unit} ({health.Cpu.Series.Count} samples), " +
			$"alloc {health.AllocatedMemory.Latest} {health.AllocatedMemory.Unit}, free {health.FreeMemory.Latest} {health.FreeMemory.Unit}, disk {health.FreeDisk.Latest} {health.FreeDisk.Unit}; error: {health.Error}");
		Assert.False(string.IsNullOrEmpty(health.Software));
		// Performance samples exist only on Neurons running as the service; the console-run
		// test Neuron has none, which must not be an error.
		Assert.Null(health.Error);
	}
}
