using System.Text.Json;

namespace Neuro.Nat.Engine.Accounts;

/// <summary>
/// Persists non-secret account profiles.
/// </summary>
public interface IAccountStore
{
	Task<IReadOnlyList<AccountProfile>> LoadAsync(CancellationToken cancel = default);

	Task SaveAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken cancel = default);
}

/// <summary>
/// Stores profiles as JSON in a file. Writes go to a temporary file first and are then
/// moved into place, so a crash never leaves a half-written file.
/// </summary>
public sealed class JsonAccountStore(string filePath) : IAccountStore
{
	private static readonly JsonSerializerOptions options = new() { WriteIndented = true };
	private readonly SemaphoreSlim gate = new(1, 1);

	public string FilePath { get; } = filePath;

	public async Task<IReadOnlyList<AccountProfile>> LoadAsync(CancellationToken cancel = default)
	{
		await this.gate.WaitAsync(cancel);
		try
		{
			if (!File.Exists(this.FilePath))
				return [];

			await using FileStream f = File.OpenRead(this.FilePath);
			List<AccountProfile>? list = await JsonSerializer.DeserializeAsync<List<AccountProfile>>(f, options, cancel);
			return list ?? [];
		}
		finally
		{
			this.gate.Release();
		}
	}

	public async Task SaveAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken cancel = default)
	{
		await this.gate.WaitAsync(cancel);
		try
		{
			string? folder = Path.GetDirectoryName(this.FilePath);
			if (!string.IsNullOrEmpty(folder))
				Directory.CreateDirectory(folder);

			string tmp = this.FilePath + ".tmp";
			await using (FileStream f = File.Create(tmp))
				await JsonSerializer.SerializeAsync(f, profiles, options, cancel);

			File.Move(tmp, this.FilePath, true);
		}
		finally
		{
			this.gate.Release();
		}
	}
}
