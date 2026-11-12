using System.Reflection;
using System.Security.Cryptography;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Desktop;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Alerts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Storage;
using Waher.Persistence.Attributes;
using Waher.Persistence.Files;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The plain files NAT kept before it had a database move into it once, completely, and never
/// twice; when the database cannot be opened, the user is asked, in plain words.
/// </summary>
public class StorageImportTests : IDisposable
{
	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private readonly string folder = Path.Combine(Path.GetTempPath(), "Nat.Import." + Guid.NewGuid().ToString("N"));
	private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
	private readonly Guid account = Guid.NewGuid();
	private readonly bool initialized = Initialize();

	private static bool Initialize()
	{
		EngineRuntime.Initialize();
		return true;
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(this.folder))
				Directory.Delete(this.folder, true);
		}
		catch (IOException)
		{
		}
	}

	private string Old(string name) => Path.Combine(this.folder, name);

	private async Task<FilesProvider> OpenAsync()
	{
		DatabaseOpening o = await LocalDatabase.OpenAsync(Path.Combine(this.folder, "database"), this.key);
		Assert.True(o.IsOpen, o.Problem + " " + o.Error);
		return o.Database!;
	}

	/// <summary>An old-style data folder: every plain file NAT used to keep, and a sign-in file where Windows can make one.</summary>
	private async Task WriteOldFolderAsync()
	{
		Assert.True(this.initialized);
		await new JsonAccountStore(this.Old("accounts.json")).SaveAsync(
			[new AccountProfile { Id = this.account, Host = "lab.local", UserName = "ops" }, new AccountProfile { Host = "prod.example", UserName = "admin" }], Ct);

		Conversation kim = new(this.account, "kim@lab.local");
		kim.Add(Message("m1", "Morning"));
		kim.Add(Message("m2", "The backup failed again"));
		Conversation room = new(this.account, "ops@muc.lab.local") { IsRoom = true };
		room.Add(Message("r1", "Deploying at ten"));
		new JsonChatStore(this.Old("chat.json")).Save([kim, room], [new RoomInfo(this.account, "ops@muc.lab.local", "Mille") { Password = "room-pw" }]);

		new JsonAttentionStore(this.Old("attention.json")).Save([
			new NeuronNotification { Id = "n1", AccountId = this.account, NeuronJid = "neuron@lab.local", Received = DateTime.UtcNow, Markdown = "# Backup failed" },
			new NeuronNotification { Id = "n2", AccountId = this.account, NeuronJid = "neuron@lab.local", Received = DateTime.UtcNow, Markdown = "# Disk almost full" }]);

		new JsonUiSettingsStore(this.Old("ui.json")).Save(new UiSettings { TextSize = 115, CompactRows = true });
		new JsonAlertSettingsStore(this.Old("alerts.json")).Save(new AlertSettings { SoundForInformation = true });

		if (OperatingSystem.IsWindows())
		{
			await new DpapiCredentialVault(this.Old("secrets")).SetAsync(this.account, AccountSecret.FromHash("agreed-hash", "SCRAM-SHA-256"), Ct);
			DatabaseKeys.ForCurrentPlatform(this.folder);
		}
	}

	private LegacyImport Import(FilesProvider db, Action<string>? afterWrite = null) => new(db, this.folder)
	{
		ReadOldSignIn = id => OperatingSystem.IsWindows() ? new DpapiCredentialVault(this.Old("secrets")).GetAsync(id) : Task.FromResult<AccountSecret?>(null),
		AfterWrite = afterWrite,
	};

	[Fact]
	public async Task EverythingFromTheOldFilesIsThereAndTheFilesAreGone()
	{
		await this.WriteOldFolderAsync();
		FilesProvider db = await this.OpenAsync();

		IReadOnlyList<ImportStep> steps = await this.Import(db).RunAsync();
		Assert.DoesNotContain(steps, s => s.Failed);
		await db.Stop();

		// After a restart, from the database alone.
		db = await this.OpenAsync();
		IReadOnlyList<AccountProfile> accounts = await new DatabaseAccountStore(db).LoadAsync(Ct);
		Assert.Equal(2, accounts.Count);

		(IReadOnlyList<Conversation> conversations, IReadOnlyList<RoomInfo> rooms) = new DatabaseChatStore(db).Load();
		Assert.Equal(2, conversations.Count);
		Assert.Equal(["Morning", "The backup failed again"], conversations.Single(c => c.BareJid == "kim@lab.local").Messages.Select(m => m.Markdown));
		Assert.Single(conversations.Single(c => c.IsRoom).Messages);
		Assert.Equal("room-pw", Assert.Single(rooms).Password);

		Assert.Equal(["n1", "n2"], new DatabaseAttentionStore(db).Load().Select(n => n.Id).Order());

		DatabaseSettings settings = new(db);
		UiSettings ui = new DatabaseUiSettingsStore(settings).Load();
		Assert.Equal(115, ui.TextSize);
		Assert.True(ui.CompactRows);
		Assert.True(new DatabaseAlertSettingsStore(settings).Load().SoundForInformation);

		if (OperatingSystem.IsWindows())
		{
			// The sign-in works: the account manager finds the account with its sign-in.
			DatabaseCredentialVault vault = new(db);
			Assert.Equal(AccountSecret.FromHash("agreed-hash", "SCRAM-SHA-256"), await vault.GetAsync(this.account, Ct));
			await using AccountManager manager = new(new DatabaseAccountStore(db), vault);
			await manager.LoadAsync(Ct);
			Assert.Equal(2, manager.Sessions.Count);
			Assert.False(File.Exists(this.Old(Path.Combine("secrets", this.account.ToString("N") + ".bin"))));
			Assert.True(File.Exists(this.Old(Path.Combine("secrets", "database.key"))), "The key to the database stays outside.");
		}

		await db.Stop();
		foreach (string f in new[] { "accounts.json", "chat.json", "attention.json", "ui.json", "alerts.json" })
			Assert.False(File.Exists(this.Old(f)), f + " is still there.");
	}

	[Fact]
	public async Task AMoveBrokenInTheMiddleKeepsTheOldFilesAndDoublesNothingLater()
	{
		await this.WriteOldFolderAsync();
		FilesProvider db = await this.OpenAsync();

		// The chat is written, and then something fails before its file is deleted.
		IReadOnlyList<ImportStep> first = await this.Import(db, what =>
		{
			if (what == "chat")
				throw new IOException("Broken on purpose.");
		}).RunAsync();

		Assert.True(first.Single(s => s.What == "chat").Failed);
		Assert.True(File.Exists(this.Old("chat.json")), "The old chat file stays when its move fails.");
		Assert.False(File.Exists(this.Old("accounts.json")), "What did move has its file removed.");
		Assert.True(this.Import(db).HasWork());

		// The next start finishes the job, and nothing is there twice.
		IReadOnlyList<ImportStep> second = await this.Import(db).RunAsync();
		Assert.DoesNotContain(second, s => s.Failed);
		Assert.False(File.Exists(this.Old("chat.json")));
		Assert.False(this.Import(db).HasWork());

		Assert.Equal(3, (await db.Find<StoredChatMessage>(0, int.MaxValue)).Count());
		Assert.Equal(2, (await db.Find<StoredConversation>(0, int.MaxValue)).Count());
		Assert.Single(await db.Find<StoredRoom>(0, int.MaxValue));
		Assert.Equal(2, (await db.Find<StoredAccount>(0, int.MaxValue)).Count());
		Assert.Equal(2, (await db.Find<StoredInboxItem>(0, int.MaxValue)).Count());

		// And a third run has nothing to do.
		Assert.Empty(await this.Import(db).RunAsync());
		Assert.Equal(3, (await db.Find<StoredChatMessage>(0, int.MaxValue)).Count());
		await db.Stop();
	}

	[Fact]
	public async Task ADamagedOldFileStays()
	{
		Directory.CreateDirectory(this.folder);
		await File.WriteAllTextAsync(this.Old("chat.json"), "{ this is not json", Ct);
		FilesProvider db = await this.OpenAsync();

		Assert.True(Assert.Single(await this.Import(db).RunAsync()).Failed);
		Assert.True(File.Exists(this.Old("chat.json")));
		await db.Stop();
	}

	// ----- When the database cannot be opened -----

	[AvaloniaFact]
	public async Task AWrongKeyGivesThePlainMessageWithThreeChoices()
	{
		FilesProvider db = await this.OpenAsync();
		await db.Stop();

		DatabaseOpening wrong = await LocalDatabase.OpenAsync(Path.Combine(this.folder, "database"), RandomNumberGenerator.GetBytes(32));
		Assert.False(wrong.IsOpen);

		DatabaseProblemViewModel vm = new();
		Assert.Equal("NAT cannot open what it has saved on this computer.", vm.Title);

		DatabaseProblemWindow window = new(vm);
		window.Show();
		Dispatcher.UIThread.RunJobs();
		string[] buttons = [.. window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Select(b => b.Content as string).OfType<string>()];
		Assert.Equal(["Quit", "Start fresh", "Try again"], buttons);
		SaveFrame(window, "database-problem");

		vm.StartFreshCommand.Execute(null);
		Assert.Equal(DatabaseChoice.StartFresh, vm.Choice);
		Assert.False(window.IsVisible);

		// Start fresh leaves the old database in a dated folder beside it.
		string? aside = LocalDatabase.SetAside(Path.Combine(this.folder, "database"), DateTime.Now);
		Assert.NotNull(aside);
		Assert.Contains("database unreadable ", Path.GetFileName(aside));
		Assert.True(File.Exists(Path.Combine(aside, LocalDatabase.KeyCheckFile)));
	}

	[Fact]
	public void NoClassOfTheAppOrTheHostIsStoredByName()
	{
		// Stored classes live in the engine and are checked there; nothing else may slip into the database.
		foreach (Assembly a in new[] { typeof(App).Assembly, typeof(DataFolder).Assembly })
			Assert.DoesNotContain(a.GetTypes(), t => t.GetCustomAttribute<CollectionNameAttribute>() is not null);
	}

	private static void SaveFrame(Avalonia.Controls.Window window, string name)
	{
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		string dir = Path.Combine(RenameTests.RepositoryRoot(), ".impeccable", "review");
		Directory.CreateDirectory(dir);
		using FileStream f = File.Create(Path.Combine(dir, name + ".png"));
		frame!.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
	}

	private static ChatMessage Message(string id, string text)
		=> new() { Id = id, MessageId = "x" + id, Direction = ChatDirection.In, Timestamp = DateTime.UtcNow, Markdown = text, Text = text };
}
