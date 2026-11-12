using Neuro.Nat.Engine.Accounts;

namespace Neuro.Nat.Engine.Tests;

public class AccountTests : IDisposable
{
	private readonly string folder = Path.Combine(Path.GetTempPath(), "Nat.Tests." + Guid.NewGuid().ToString("N"));

	public AccountTests() => EngineRuntime.Initialize();

	public void Dispose()
	{
		if (Directory.Exists(this.folder))
			Directory.Delete(this.folder, true);
	}

	[Fact]
	public async Task ProfilesRoundTripWithoutSecrets()
	{
		string file = Path.Combine(this.folder, "accounts.json");
		JsonAccountStore store = new(file);
		AccountProfile p = new() { Host = "localhost", Domain = "lab.local", UserName = "admin", TrustServerCertificate = true, ReadOnly = true };

		await store.SaveAsync([p]);
		IReadOnlyList<AccountProfile> loaded = await new JsonAccountStore(file).LoadAsync();

		Assert.Equal(p, Assert.Single(loaded));
		Assert.DoesNotContain("password", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
		Assert.Equal("admin@lab.local", loaded[0].Label);
	}

	[Fact]
	public async Task MissingStoreLoadsEmpty()
	{
		Assert.Empty(await new JsonAccountStore(Path.Combine(this.folder, "none.json")).LoadAsync());
	}

	[Fact]
	public async Task AddPersistsProfileAndSecretAndAssignsColours()
	{
		InMemoryCredentialVault vault = new();
		JsonAccountStore store = new(Path.Combine(this.folder, "accounts.json"));
		await using AccountManager manager = new(store, vault);

		AccountSession a = await manager.AddAsync(new AccountProfile { Host = "a.local", UserName = "x" }, AccountSecret.FromPassword("pw"), connect: false);
		AccountSession b = await manager.AddAsync(new AccountProfile { Host = "b.local", UserName = "y" }, AccountSecret.FromPassword("pw2"), connect: false);

		Assert.Equal(2, (await store.LoadAsync()).Count);
		Assert.Equal("pw", (await vault.GetAsync(a.Profile.Id))?.Value);
		Assert.NotEqual(a.Profile.ColorIndex, b.Profile.ColorIndex);

		await manager.RemoveAsync(a.Profile.Id);
		Assert.Single(manager.Sessions);
		Assert.Null(await vault.GetAsync(a.Profile.Id));
	}

	[Fact]
	public async Task ConnectWithoutSecretFailsWithReason()
	{
		InMemoryCredentialVault vault = new();
		JsonAccountStore store = new(Path.Combine(this.folder, "accounts.json"));
		await using AccountManager manager = new(store, vault);
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = "a.local", UserName = "x" }, AccountSecret.FromPassword("pw"), connect: false);
		await vault.RemoveAsync(s.Profile.Id);

		await s.ConnectAsync();

		Assert.Equal(ConnectionStatus.Failed, s.Status);
		Assert.Contains("No password", s.LastError);
	}

	[Fact]
	public void SecretsNeverPrint()
	{
		Assert.DoesNotContain("hunter2", AccountSecret.FromPassword("hunter2").ToString());
	}
}
