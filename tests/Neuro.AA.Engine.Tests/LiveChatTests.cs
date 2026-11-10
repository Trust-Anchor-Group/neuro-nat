using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Chat;
using Neuro.AA.Engine.Events;
using Xunit.Abstractions;

namespace Neuro.AA.Engine.Tests;

/// <summary>
/// Chat and the chat admin console against the local test Neuron: the console reads the
/// command list, runs a command and gets its reply in the command's thread; ordinary chat with
/// the Neuron lands in a conversation instead.
/// </summary>
[Collection("Live Neuron")]
public class LiveChatTests(ITestOutputHelper output)
{
	[Fact]
	public async Task ConsoleAndChat()
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
		string file = Path.Combine(Path.GetTempPath(), "NeuroAA.Live." + Guid.NewGuid().ToString("N"), "accounts.json");
		await using AccountManager manager = new(new JsonAccountStore(file), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		await using ChatService chat = new(manager, monitors);
		await s.ConnectAsync();
		await StreamSnifferTests.WaitFor(() => monitors.For(s.Profile.Id)?.Concentrator(neuron) is not null, 30000);

		IReadOnlyList<Contact> contacts = chat.Contacts(s.Profile.Id);
		output.WriteLine("Contacts: " + string.Join(", ", contacts.Select(c => $"{c.Label} ({c.Subscription}, online {c.IsOnline}, neuron {c.IsNeuron})")));
		Assert.Contains(contacts, c => c.IsNeuron && string.Equals(c.BareJid, neuron, StringComparison.OrdinalIgnoreCase));

		ConsoleSession console = chat.Console(s.Profile.Id, neuron);
		await console.LoadCommandsAsync();
		await StreamSnifferTests.WaitFor(() => console.Commands.Count > 0, 20000);
		output.WriteLine($"{console.Commands.Count} commands: " + string.Join(" ", console.Commands.Take(25)));

		ConsoleEntry versions = await console.SendAsync("Versions");
		await StreamSnifferTests.WaitFor(() => versions.Responses.Count > 0, 20000);
		await Task.Delay(1500);
		foreach (ConsoleResponse r in versions.Responses)
			output.WriteLine($"Versions reply ({r.MessageId}, corrected {r.Corrected:HH:mm:ss}): {r.Markdown[..Math.Min(300, r.Markdown.Length)].Replace("\r", "").Replace("\n", " | ")}");

		ConsoleEntry script = await console.SendAsync("6*7");
		await StreamSnifferTests.WaitFor(() => script.Responses.Count > 0, 20000);
		output.WriteLine("6*7 => " + script.Responses[0].Markdown);
		Assert.Contains("42", script.Responses[0].Markdown);
		Assert.Single(console.Entries, e => e.Command == "6*7");
		Assert.DoesNotContain(console.Entries, e => e.Command == "help");

		// Ordinary chat with the Neuron (no thread) goes to the conversation, not the console.
		ChatMessage plain = await chat.SendAsync(s.Profile.Id, neuron, "1+1");
		output.WriteLine($"Sent 1+1: encrypted {plain.Encrypted}, error {plain.Error}");
		Conversation conv = chat.Get(s.Profile.Id, neuron);
		try
		{
			await StreamSnifferTests.WaitFor(() => conv.Messages.Any(m => m.Direction == ChatDirection.In), 20000);
		}
		finally
		{
			foreach (var l in s.Sniffer.Snapshot().Where(l => l.Text.StartsWith("<message", StringComparison.Ordinal)).TakeLast(6))
				output.WriteLine($"  [{l.Direction}] {l.Text[..Math.Min(700, l.Text.Length)]}");
		}
		foreach (ChatMessage m in conv.Messages)
			output.WriteLine($"Chat {m.Direction}: {m.Markdown}");
		Assert.Contains(conv.Messages, m => m.Direction == ChatDirection.In && m.Markdown.Contains('2'));
	}
}
