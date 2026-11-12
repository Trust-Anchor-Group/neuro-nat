using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// End to end against the local test Neuron: a notification the Neuron sends to its
/// administrators lands in the Attention inbox, typed, and a later correction of the same
/// message updates the item instead of adding one. The reply chat admin sends to the script
/// that triggers it must not show up.
/// </summary>
[Collection("Live Neuron")]
public class LiveAttentionTests(ITestOutputHelper output)
{
	[Fact]
	public async Task NotificationAndCorrectionArriveInInbox()
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

		AttentionInbox inbox = new();
		await using NeuronMonitor monitor = new(s, new EventHub());
		monitor.NotificationReceived += (_, n) => inbox.Add(n);
		await s.ConnectAsync();

		await StreamSnifferTests.WaitFor(() => monitor.Neurons.Any(n => n.State == WatchState.Live && string.Equals(n.BareJid, neuron, StringComparison.OrdinalIgnoreCase)), 30000);
		WatchedNeuron w = monitor.Neurons.First(n => string.Equals(n.BareJid, neuron, StringComparison.OrdinalIgnoreCase));

		string marker = Guid.NewGuid().ToString("N")[..8];
		string id = "naa-" + marker;
		await s.Client!.SendChatMessage(w.FullJid!, $"Waher.IoTGateway.Gateway.SendNotification(\"Unable to upload backup to `{marker}`.\", \"{id}\")");

		await StreamSnifferTests.WaitFor(() => inbox.Items.Any(n => n.Markdown.Contains(marker)), 20000);
		NeuronNotification first = inbox.Items.First(n => n.Markdown.Contains(marker));
		output.WriteLine($"Got {first.Kind}/{first.Severity} '{first.Title}' id {first.MessageId} at {first.Received:HH:mm:ss}");
		Assert.Equal(NotificationKind.Backup, first.Kind);
		Assert.True(first.NeedsAction);
		Assert.Equal(id, first.MessageId);

		await s.Client.SendChatMessage(w.FullJid!, $"Waher.IoTGateway.Gateway.SendNotificationUpdate(\"Backup file has been successfully been uploaded to `{marker}`.\", \"{id}\")");
		await StreamSnifferTests.WaitFor(() => inbox.Items.Any(n => n.Markdown.Contains(marker) && n.Updated is not null), 20000);

		List<NeuronNotification> mine = [.. inbox.Items.Where(n => n.Markdown.Contains(marker))];
		foreach (NeuronNotification n in inbox.Items)
			output.WriteLine($"  {n.Kind} '{n.Title}' updated {n.Updated:HH:mm:ss}: {n.Markdown[..Math.Min(80, n.Markdown.Length)]}");

		Assert.Single(mine);
		Assert.Equal(NotificationKind.Backup, mine[0].Kind);
		Assert.False(mine[0].NeedsAction);

		// The chat admin replies to the scripts went to this client's full JID, not to the inbox.
		Assert.DoesNotContain(inbox.Items, n => n.Markdown.Contains("SendNotification"));
	}
}
