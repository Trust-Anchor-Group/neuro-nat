using Microsoft.Win32;
using Neuro.Nat.Desktop;
using Neuro.Nat.Desktop.Install;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Identity;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The app was called Neuro.AA before it was called NAT. What was saved under the old name must
/// still open: the data folder moves once, the texts that saved data is encrypted with stay, and
/// "Start with Windows" moves to the new entry. None of these tests touch the real folders.
/// </summary>
public class RenameTests : IDisposable
{
	private readonly string root = Path.Combine(Path.GetTempPath(), "Nat.Rename." + Guid.NewGuid().ToString("N"));

	public RenameTests() => EngineRuntime.Initialize();

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private string Old => Path.Combine(this.root, "Neuro.AA");

	private string New => Path.Combine(this.root, "Neuro.Nat");

	public void Dispose()
	{
		if (Directory.Exists(this.root))
			Directory.Delete(this.root, true);
	}

	// ----- The hidden texts that saved data depends on -----

	[Fact]
	public void TextsThatSavedDataDependsOnStay()
	{
		// Changing any of these locks the user out of saved accounts and legal identity keys.
		if (OperatingSystem.IsWindows())
			Assert.Equal("Neuro.AA credential vault v1", DpapiCredentialVault.Entropy);
		Assert.Equal("Neuro.AA database key v1", DatabaseKeys.Entropy);
		Assert.Equal("Neuro.AA database key:", EngineRuntime.FileKeyLabel);
		Assert.Equal("Neuro.AA database iv:", EngineRuntime.FileIvLabel);

		Guid id = Guid.Parse("0123456789abcdef0123456789abcdef");
		Assert.Equal("NeuroAA.0123456789abcdef0123456789abcdef", IdentityService.KeySettingsInstance(id));
	}

	// ----- Moving the data folder -----

	[Fact]
	public async Task MovesTheOldFolderAndFindsTheAccounts()
	{
		AccountProfile[] profiles = [new() { Host = "a.local", UserName = "x" }, new() { Host = "b.local", UserName = "y" }];
		await new JsonAccountStore(Path.Combine(this.Old, "accounts.json")).SaveAsync(profiles, Ct);
		Directory.CreateDirectory(Path.Combine(this.Old, "secrets"));
		await File.WriteAllBytesAsync(Path.Combine(this.Old, "secrets", "x.bin"), [1, 2, 3], Ct);
		Directory.CreateDirectory(Path.Combine(this.Old, "logs"));
		await File.WriteAllTextAsync(Path.Combine(this.Old, "logs", "neuro.aa.log"), "old log", Ct);
		await File.WriteAllTextAsync(Path.Combine(this.Old, "update-source.txt"), @"\\nas\apps\NAT", Ct);

		DataFolderMoveResult r = DataFolderMove.Run(this.Old, this.New);

		Assert.Equal(DataFolderMoveOutcome.Moved, r.Outcome);
		Assert.False(Directory.Exists(this.Old));
		IReadOnlyList<AccountProfile> loaded = await new JsonAccountStore(Path.Combine(this.New, "accounts.json")).LoadAsync(Ct);
		Assert.Equal(profiles.Select(p => p.Id), loaded.Select(p => p.Id));
		Assert.True(File.Exists(Path.Combine(this.New, "secrets", "x.bin")));
		Assert.Equal("old log", await File.ReadAllTextAsync(Path.Combine(this.New, "logs", "nat.log"), Ct));
		Assert.False(File.Exists(Path.Combine(this.New, "logs", "neuro.aa.log")));
		Assert.Equal(@"\\nas\apps\NAT", await File.ReadAllTextAsync(Path.Combine(this.New, "update-source.txt"), Ct));

		// Once is enough: a second start finds nothing to move.
		Assert.Equal(DataFolderMoveOutcome.NothingToMove, DataFolderMove.Run(this.Old, this.New).Outcome);
	}

	[Fact]
	public async Task BothFoldersThereUsesTheNewOneAndLeavesTheOldAlone()
	{
		Directory.CreateDirectory(this.Old);
		Directory.CreateDirectory(this.New);
		await File.WriteAllTextAsync(Path.Combine(this.Old, "accounts.json"), "old", Ct);
		await File.WriteAllTextAsync(Path.Combine(this.New, "accounts.json"), "new", Ct);

		DataFolderMoveResult r = DataFolderMove.Run(this.Old, this.New);

		Assert.Equal(DataFolderMoveOutcome.BothExist, r.Outcome);
		Assert.NotNull(r.Note);
		Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(this.Old, "accounts.json"), Ct));
		Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(this.New, "accounts.json"), Ct));
	}

	[Fact]
	public async Task AMoveThatFailsChangesNothing()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows only: the vault and the registry are Windows features.");
			return;
		}

		Directory.CreateDirectory(Path.Combine(this.Old, "logs"));
		string log = Path.Combine(this.Old, "logs", "neuro.aa.log");
		await File.WriteAllTextAsync(log, "in use", Ct);

		// As when Neuro.AA still runs and holds its log file open.
		DataFolderMoveResult r;
		using (FileStream held = new(log, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
			r = DataFolderMove.Run(this.Old, this.New);

		Assert.Equal(DataFolderMoveOutcome.Failed, r.Outcome);
		Assert.NotNull(r.Error);
		Assert.False(Directory.Exists(this.New));
		Assert.Equal("in use", await File.ReadAllTextAsync(log, Ct));

		// The next start tries again, and then it works.
		Assert.Equal(DataFolderMoveOutcome.Moved, DataFolderMove.Run(this.Old, this.New).Outcome);
	}

	[Fact]
	public void NothingToMoveWithoutTheOldFolder()
		=> Assert.Equal(DataFolderMoveOutcome.NothingToMove, DataFolderMove.Run(this.Old, this.New).Outcome);

	[Fact]
	public async Task SecretsSavedBeforeTheRenameStillOpen()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows only: the vault and the registry are Windows features.");
			return;
		}

		Guid id = Guid.NewGuid();
		await new DpapiCredentialVault(Path.Combine(this.Old, "secrets")).SetAsync(id, AccountSecret.FromPassword("secret before"), Ct);
		byte[] key = DatabaseKeys.ForCurrentPlatform(this.Old)!;

		Assert.Equal(DataFolderMoveOutcome.Moved, DataFolderMove.Run(this.Old, this.New).Outcome);

		Assert.Equal("secret before", (await new DpapiCredentialVault(Path.Combine(this.New, "secrets")).GetAsync(id, Ct))?.Value);
		Assert.Equal(key, DatabaseKeys.ForCurrentPlatform(this.New));
	}

	// ----- Where updates come from -----

	[Fact]
	public async Task UpdateSourceOrderIsVariableThenFileThenBuiltIn()
	{
		string? was = Environment.GetEnvironmentVariable(VelopackUpdates.SourceVariable);
		try
		{
			Directory.CreateDirectory(this.New);
			Environment.SetEnvironmentVariable(VelopackUpdates.SourceVariable, null);

			Assert.Equal("https://built.in/repo", VelopackUpdates.SourceFor(this.New, "https://built.in/repo"));
			Assert.Null(VelopackUpdates.SourceFor(this.New, null));

			await File.WriteAllTextAsync(Path.Combine(this.New, "update-source.txt"), "# a comment\n\n  \\\\nas\\apps\\NAT  \nsecond line\n", Ct);
			Assert.Equal(@"\\nas\apps\NAT", VelopackUpdates.SourceFor(this.New, "https://built.in/repo"));

			Environment.SetEnvironmentVariable(VelopackUpdates.SourceVariable, " D:\\updates ");
			Assert.Equal(@"D:\updates", VelopackUpdates.SourceFor(this.New, "https://built.in/repo"));
		}
		finally
		{
			Environment.SetEnvironmentVariable(VelopackUpdates.SourceVariable, was);
		}
	}

	[Fact]
	public void TheBuiltInUpdateSourceComesFromDirectoryBuildProps()
	{
		string props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
		string address = System.Text.RegularExpressions.Regex.Match(props, "<UpdateSource[^>]*>([^<]+)</UpdateSource>").Groups[1].Value;

		Assert.StartsWith("https://", address);
		Assert.Equal(address, VelopackUpdates.BuiltInSource);
	}

	// ----- Start with Windows -----

	[Fact]
	public void StartWithWindowsMovesToTheNewEntry()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows only: the vault and the registry are Windows features.");
			return;
		}

		// A key of its own, not the real Run key.
		string path = @"Software\Neuro.Nat.Tests\" + Guid.NewGuid().ToString("N");
		try
		{
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, writable: true);
			Assert.False(WindowsAutostart.MoveOldEntry(key, "\"C:\\NAT.exe\" --tray"));

			key.SetValue(WindowsAutostart.OldValueName, "\"C:\\Old\\Neuro.AA.exe\" --tray");
			Assert.True(WindowsAutostart.MoveOldEntry(key, "\"C:\\NAT.exe\" --tray"));
			Assert.Null(key.GetValue(WindowsAutostart.OldValueName));
			Assert.Equal("\"C:\\NAT.exe\" --tray", key.GetValue("NAT"));
		}
		finally
		{
			// Only its own key: other tests use Neuro.Nat.Tests at the same time.
			Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
		}
	}

	internal static string RepositoryRoot()
	{
		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;
		return d?.FullName ?? throw new InvalidOperationException("The repository was not found.");
	}
}
