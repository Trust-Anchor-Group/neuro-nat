using Neuro.AA.Engine.Accounts;
using Xunit.Abstractions;

namespace Neuro.AA.Engine.Tests;

/// <summary>
/// Integration tests against a local Neuron. They only run when these environment
/// variables are set (they are skipped otherwise):
///   NEUROAA_TEST_HOST, NEUROAA_TEST_USER, NEUROAA_TEST_PASSWORD
/// and optionally NEUROAA_TEST_DOMAIN and NEUROAA_TEST_PORT.
/// </summary>
[Collection("Live Neuron")]
public class LocalNeuronTests(ITestOutputHelper output)
{
	private static AccountProfile? Profile(out string password)
	{
		string? host = Environment.GetEnvironmentVariable("NEUROAA_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NEUROAA_TEST_USER");
		password = Environment.GetEnvironmentVariable("NEUROAA_TEST_PASSWORD") ?? string.Empty;

		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
			return null;

		return new AccountProfile
		{
			Host = host,
			Domain = Environment.GetEnvironmentVariable("NEUROAA_TEST_DOMAIN") ?? string.Empty,
			Port = int.TryParse(Environment.GetEnvironmentVariable("NEUROAA_TEST_PORT"), out int port) ? port : 5222,
			UserName = user,
			TrustServerCertificate = true,
		};
	}

	[Fact]
	public async Task ConnectsAndReplacesPasswordWithHash()
	{
		AccountProfile? profile = Profile(out string password);
		if (profile is null)
		{
			output.WriteLine("Not run: set NEUROAA_TEST_HOST, NEUROAA_TEST_USER and NEUROAA_TEST_PASSWORD to test against a local Neuron.");
			return;
		}

		EngineRuntime.Initialize();
		InMemoryCredentialVault vault = new();
		string file = Path.Combine(Path.GetTempPath(), "NeuroAA.Tests." + Guid.NewGuid().ToString("N"), "accounts.json");
		await using AccountManager manager = new(new JsonAccountStore(file), vault);

		AccountSession s = await manager.AddAsync(profile, AccountSecret.FromPassword(password), connect: true);
		await StreamSnifferTests.WaitFor(() => s.Status is ConnectionStatus.Connected or ConnectionStatus.Failed, 20000);

		Assert.True(s.Status == ConnectionStatus.Connected, $"Status {s.Status}: {s.LastError}");
		output.WriteLine($"Connected as {s.BareJid} ({s.Client?.FullJID}).");
		Assert.False(string.IsNullOrEmpty(s.BareJid));
		Assert.Contains(s.Sniffer.Snapshot(), l => l.Text.Contains("<stream:stream", StringComparison.Ordinal));

		AccountSecret? stored = await vault.GetAsync(s.Profile.Id);
		Assert.True(stored?.IsHash, "The plain password should be replaced by the negotiated hash.");
	}
}
