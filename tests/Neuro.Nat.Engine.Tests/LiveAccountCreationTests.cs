using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;
using Xunit.Abstractions;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// Creating an account on the local test Neuron with an API key: the operator account makes a
/// test key through chat admin, a new account is created with it, a taken name is refused, and
/// the test account and key are removed again.
/// </summary>
[Collection("Live Neuron")]
public class LiveAccountCreationTests(ITestOutputHelper output)
{
	[Fact]
	public async Task CreatesAnAccountWithAnApiKey()
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? neuron = Environment.GetEnvironmentVariable("NAT_TEST_NEURON");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(neuron))
		{
			output.WriteLine("Not run: NAT_TEST_* not set.");
			return;
		}

		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Live." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession op = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		await using ChatService chat = new(manager, monitors);
		await op.ConnectAsync();
		await StreamSnifferTests.WaitFor(() => monitors.Neurons.Any(n => n.State == WatchState.Live), 30000);
		ConsoleSession console = chat.Console(op.Profile.Id, neuron);

		string key = "naa-test-" + Guid.NewGuid().ToString("N")[..8];
		string secret = Guid.NewGuid().ToString("N");
		string? made = await console.AskAsync(
			$"K:=Create(Waher.Service.IoTBroker.DataStorage.ApiKey);K.Key:=\"{key}\";K.Secret:=\"{secret}\";K.Owner:=\"{op.BareJid}\";K.MaxAccounts:=10;SaveNewObject(K);K.Key",
			TimeSpan.FromSeconds(20));
		output.WriteLine("Key: " + made);
		Assert.Contains(key, made ?? string.Empty);

		string name = "naa-new-" + Guid.NewGuid().ToString("N")[..6];
		try
		{
			AccountSession created = await manager.CreateAsync(new AccountProfile { Host = host, UserName = name, TrustServerCertificate = true },
				"Pw-" + Guid.NewGuid().ToString("N"), new AccountRegistration(key, secret, "kim@example.com", null));
			output.WriteLine($"Created {created.BareJid}: {created.Status}");
			Assert.Equal(ConnectionStatus.Connected, created.Status);

			// The same name again is refused, and nothing is kept.
			int before = manager.Sessions.Count;
			InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAsync(
				new AccountProfile { Host = host, UserName = name, TrustServerCertificate = true }, "Other-" + Guid.NewGuid().ToString("N"), new AccountRegistration(key, secret)));
			output.WriteLine("Taken name: " + ex.Message);
			Assert.Equal(before, manager.Sessions.Count);

			// A wrong secret is refused too.
			ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAsync(
				new AccountProfile { Host = host, UserName = name + "x", TrustServerCertificate = true }, "Pw-" + Guid.NewGuid().ToString("N"), new AccountRegistration(key, "wrong")));
			output.WriteLine("Wrong secret: " + ex.Message);
		}
		finally
		{
			string? cleaned = await console.AskAsync(
				$"foreach A in (select * from Waher.Service.IoTBroker.DataStorage.Account where UserName=\"{name}\") do DeleteObject(A);" +
				$"foreach K in (select * from Waher.Service.IoTBroker.DataStorage.ApiKey where Key=\"{key}\") do DeleteObject(K);\"cleaned\"",
				TimeSpan.FromSeconds(20));
			output.WriteLine("Cleanup: " + cleaned);
		}
	}
}
