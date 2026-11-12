using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;

namespace Neuro.Nat.App.Tests;

/// <summary>An existing account's web address can be set, changed and cleared, and only to an address NAT will use.</summary>
public class AccountWebAddressTests
{
	[Fact]
	public async Task AnExistingAccountsWebAddressCanBeChanged()
	{
		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Web." + Guid.NewGuid().ToString("N"), "accounts.json");
		await using AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = "localhost", UserName = "ops", ConnectOnStartup = false },
			AccountSecret.FromPassword("x"), connect: false, cancel: TestContext.Current.CancellationToken);

		AccountItemViewModel vm = new(s, _ => Task.CompletedTask, (_, _) => Task.CompletedTask, null, p => manager.UpdateAsync(p));
		Assert.Equal(string.Empty, vm.WebAddressText);
		Assert.Equal("https://localhost/", vm.WebAddressPlaceholder);
		Assert.False(vm.IsWebAddressChanged);

		// http only on this computer.
		vm.WebAddressText = "http://lab.example.com:8081";
		Assert.True(vm.IsWebAddressChanged);
		await vm.SaveWebAddressCommand.ExecuteAsync(null);
		Assert.NotNull(vm.WebAddressError);
		Assert.Null(s.Profile.WebAddress);

		vm.WebAddressText = "http://localhost:8081";
		await vm.SaveWebAddressCommand.ExecuteAsync(null);
		Assert.Null(vm.WebAddressError);
		Assert.Equal("http://localhost:8081/", s.Profile.WebAddress);
		Assert.False(vm.IsWebAddressChanged);

		// Kept with the account.
		IReadOnlyList<AccountProfile> saved = await new JsonAccountStore(store).LoadAsync(TestContext.Current.CancellationToken);
		Assert.Equal("http://localhost:8081/", Assert.Single(saved).WebAddress);

		// Empty goes back to https on the domain.
		vm.WebAddressText = string.Empty;
		await vm.SaveWebAddressCommand.ExecuteAsync(null);
		Assert.Null(s.Profile.WebAddress);
	}
}
