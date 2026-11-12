using Neuro.Nat.Engine.Accounts;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// Integration tests against a local Neuron. They only run when these environment
/// variables are set (they are skipped otherwise):
///   NAT_TEST_HOST, NAT_TEST_USER, NAT_TEST_PASSWORD
/// and optionally NAT_TEST_DOMAIN and NAT_TEST_PORT.
/// </summary>
[Collection("Live Neuron")]
public class LocalNeuronTests(ITestOutputHelper output)
{
	private static AccountProfile? Profile(out string password)
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD") ?? string.Empty;

		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
			return null;

		return new AccountProfile
		{
			Host = host,
			Domain = Environment.GetEnvironmentVariable("NAT_TEST_DOMAIN") ?? string.Empty,
			Port = int.TryParse(Environment.GetEnvironmentVariable("NAT_TEST_PORT"), out int port) ? port : 5222,
			UserName = user,
			TrustServerCertificate = true,
		};
	}

	[Fact]
	public async Task ConnectsAndReplacesPasswordWithHash()
	{
		AccountProfile? profile = Profile(out string password);
		if (profile is null)
			Live.Skip("NAT_TEST_HOST", "NAT_TEST_USER", "NAT_TEST_PASSWORD");

		EngineRuntime.Initialize();
		InMemoryCredentialVault vault = new();
		string file = Path.Combine(Path.GetTempPath(), "Nat.Tests." + Guid.NewGuid().ToString("N"), "accounts.json");
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
