using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neuro.Nat.Engine.Accounts;

namespace Neuro.Nat.Desktop;

/// <summary>
/// How sign-ins were kept before the database: each encrypted with DPAPI for the current
/// Windows user, one file per account. Now only read, once, to move them into the database
/// (Storage/LegacyImport.cs in the engine). Tests use it too.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiCredentialVault(string folder) : ICredentialVault
{
	// Must stay "Neuro.AA ..." after the rename: the secrets saved before it are encrypted with this text. Pinned by a test.
	internal const string Entropy = "Neuro.AA credential vault v1";
	private static readonly byte[] entropy = Encoding.UTF8.GetBytes(Entropy);

	private string PathFor(Guid id) => Path.Combine(folder, id.ToString("N") + ".bin");

	public async Task<AccountSecret?> GetAsync(Guid accountId, CancellationToken cancel = default)
	{
		string file = this.PathFor(accountId);
		if (!File.Exists(file))
			return null;

		byte[] encrypted = await File.ReadAllBytesAsync(file, cancel);
		byte[] plain = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
		try
		{
			return JsonSerializer.Deserialize<AccountSecret>(plain);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plain);
		}
	}

	public async Task SetAsync(Guid accountId, AccountSecret secret, CancellationToken cancel = default)
	{
		Directory.CreateDirectory(folder);
		byte[] plain = JsonSerializer.SerializeToUtf8Bytes(secret);
		try
		{
			byte[] encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
			string file = this.PathFor(accountId);
			string tmp = file + ".tmp";
			await File.WriteAllBytesAsync(tmp, encrypted, cancel);
			File.Move(tmp, file, true);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plain);
		}
	}

	public Task RemoveAsync(Guid accountId, CancellationToken cancel = default)
	{
		string file = this.PathFor(accountId);
		if (File.Exists(file))
			File.Delete(file);
		return Task.CompletedTask;
	}
}

/// <summary>
/// The key for the encrypted database that keeps legal identity signing keys. On Windows it is
/// made once, protected with DPAPI for the current Windows user, and kept next to the account
/// secrets. Other platforms have no vault adapter yet; they get no key, and the engine keeps
/// signing keys for the session only.
/// </summary>
internal static class DatabaseKeys
{
	// Must stay "Neuro.AA ..." after the rename: the database key saved before it is encrypted with this text. Pinned by a test.
	internal const string Entropy = "Neuro.AA database key v1";
	private static readonly byte[] entropy = Encoding.UTF8.GetBytes(Entropy);

	public static byte[]? ForCurrentPlatform(string dataFolder)
	{
		if (!OperatingSystem.IsWindows())
			return null;

		return Windows(Path.Combine(dataFolder, "secrets", "database.key"));
	}

	[SupportedOSPlatform("windows")]
	private static byte[] Windows(string file)
	{
		if (File.Exists(file))
			return ProtectedData.Unprotect(File.ReadAllBytes(file), entropy, DataProtectionScope.CurrentUser);

		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		byte[] key = RandomNumberGenerator.GetBytes(32);
		string tmp = file + ".tmp";
		File.WriteAllBytes(tmp, ProtectedData.Protect(key, entropy, DataProtectionScope.CurrentUser));
		File.Move(tmp, file, false);
		return key;
	}
}
