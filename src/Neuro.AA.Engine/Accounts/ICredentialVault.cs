using System.Collections.Concurrent;

namespace Neuro.AA.Engine.Accounts;

/// <summary>
/// Stores account secrets. The desktop app supplies an OS keychain implementation
/// (Windows Credential Manager, macOS Keychain, Linux Secret Service).
/// </summary>
public interface ICredentialVault
{
	Task<AccountSecret?> GetAsync(Guid accountId, CancellationToken cancel = default);

	Task SetAsync(Guid accountId, AccountSecret secret, CancellationToken cancel = default);

	Task RemoveAsync(Guid accountId, CancellationToken cancel = default);
}

/// <summary>
/// Vault that only keeps secrets in memory. Used by tests and as a last resort.
/// </summary>
public sealed class InMemoryCredentialVault : ICredentialVault
{
	private readonly ConcurrentDictionary<Guid, AccountSecret> secrets = new();

	public Task<AccountSecret?> GetAsync(Guid accountId, CancellationToken cancel = default)
		=> Task.FromResult(this.secrets.TryGetValue(accountId, out AccountSecret? s) ? s : null);

	public Task SetAsync(Guid accountId, AccountSecret secret, CancellationToken cancel = default)
	{
		this.secrets[accountId] = secret;
		return Task.CompletedTask;
	}

	public Task RemoveAsync(Guid accountId, CancellationToken cancel = default)
	{
		this.secrets.TryRemove(accountId, out _);
		return Task.CompletedTask;
	}
}
