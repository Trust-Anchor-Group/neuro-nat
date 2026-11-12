using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Alerts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Storage;
using Waher.Persistence.Attributes;
using Waher.Persistence.Files;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// Everything NAT keeps lives in the encrypted database: what goes in comes back, nothing private
/// is readable on disk, a wrong key is caught before the files are touched, and the stored form
/// does not depend on our namespaces or class names.
/// </summary>
public class StorageTests(ITestOutputHelper output) : IDisposable
{
	private readonly string folder = Path.Combine(Path.GetTempPath(), "Nat.Storage." + Guid.NewGuid().ToString("N"));
	private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
	private readonly bool initialized = Initialize();

	private static bool Initialize()
	{
		EngineRuntime.Initialize();
		return true;
	}

	private string DatabaseFolder => Path.Combine(this.folder, "database");

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

	private async Task<FilesProvider> OpenAsync(byte[]? withKey = null)
	{
		DatabaseOpening o = await LocalDatabase.OpenAsync(this.DatabaseFolder, withKey ?? this.key);
		Assert.True(o.IsOpen, o.Problem + " " + o.Error);
		return o.Database!;
	}

	// ----- A later rename never makes saved data unreadable -----

	[Fact]
	public void EveryStoredClassHasAFixedNameAndNoTypeName()
	{
		Type[] types = typeof(IStoredObject).Assembly.GetTypes();
		Type[] stored = [.. types.Where(t => typeof(IStoredObject).IsAssignableFrom(t) && t.IsClass)];
		Assert.NotEmpty(stored);

		foreach (Type t in stored)
		{
			Assert.False(string.IsNullOrEmpty(t.GetCustomAttribute<CollectionNameAttribute>()?.Name), t.Name + " needs [CollectionName].");
			Assert.Equal(TypeNameSerialization.None, t.GetCustomAttribute<TypeNameAttribute>()?.TypeNameSerialization);
		}

		// And nothing of ours is stored without saying so.
		foreach (Type t in types.Where(t => t.GetCustomAttribute<CollectionNameAttribute>() is not null))
			Assert.True(typeof(IStoredObject).IsAssignableFrom(t), t.Name + " has [CollectionName] but is not an IStoredObject.");

		// Collection names are written into the files: they must not change either.
		Assert.Equal(
			["Accounts", "AppSettings", "ChatConversations", "ChatMessages", "ChatRooms", "InboxItems", "KnownNeurons", "NeuronCopies", "RunbookRuns", "Runbooks", "SignIns"],
			stored.Select(t => t.GetCustomAttribute<CollectionNameAttribute>()!.Name).Order(StringComparer.Ordinal));
	}

	// ----- What goes in comes back -----

	[Fact]
	public async Task EverythingComesBackAfterARestart()
	{
		Guid account = Guid.NewGuid();
		FilesProvider db = await this.OpenAsync();
		AccountProfile profile = new() { Id = account, Host = "lab.local", UserName = "ops", ColorIndex = 3 };
		await new DatabaseAccountStore(db).SaveAsync([profile], TestContext.Current.CancellationToken);
		await new DatabaseCredentialVault(db).SetAsync(account, AccountSecret.FromHash("hash", "SCRAM-SHA-256"), TestContext.Current.CancellationToken);

		Conversation c = new(account, "kim@lab.local") { Unread = 2, FirstUnreadId = "m2" };
		c.Add(Message("m1", "Hello", DateTime.UtcNow.AddMinutes(-2)));
		c.Add(Message("m2", "Are you there?", DateTime.UtcNow.AddMinutes(-1)));
		RoomInfo room = new(account, "ops@muc.lab.local", "Mille") { Password = "room-pw" };
		new DatabaseChatStore(db).Save([c], [room]);

		new DatabaseAttentionStore(db).Save([Notification("n1", "# Backup failed")]);
		DatabaseSettings settings = new(db);
		new DatabaseAlertSettingsStore(settings).Save(new AlertSettings { SoundForInformation = true });
		await db.Stop();

		db = await this.OpenAsync();
		Assert.Equal(profile, Assert.Single(await new DatabaseAccountStore(db).LoadAsync(TestContext.Current.CancellationToken)));
		Assert.Equal(AccountSecret.FromHash("hash", "SCRAM-SHA-256"), await new DatabaseCredentialVault(db).GetAsync(account, TestContext.Current.CancellationToken));

		(IReadOnlyList<Conversation> conversations, IReadOnlyList<RoomInfo> rooms) = new DatabaseChatStore(db).Load();
		Conversation back = Assert.Single(conversations);
		Assert.Equal(["Hello", "Are you there?"], back.Messages.Select(m => m.Markdown));
		Assert.Equal(2, back.Unread);
		Assert.Equal("m2", back.FirstUnreadId);
		Assert.Equal("room-pw", Assert.Single(rooms).Password);

		Assert.Equal("# Backup failed", Assert.Single(new DatabaseAttentionStore(db).Load()).Markdown);
		Assert.True(new DatabaseAlertSettingsStore(new DatabaseSettings(db)).Load().SoundForInformation);
		await db.Stop();
	}

	[Fact]
	public async Task ChatWritesOnlyWhatChanged()
	{
		Guid account = Guid.NewGuid();
		FilesProvider db = await this.OpenAsync();
		DatabaseChatStore store = new(db);
		Conversation c = new(account, "kim@lab.local");
		for (int i = 0; i < 5; i++)
			c.Add(Message("m" + i, "text " + i, DateTime.UtcNow.AddMinutes(i)));
		store.Save([c], []);

		// A correction replaces the message; one falls out; one is new.
		c.Correct(c.Messages[1].MessageId!, "corrected", "corrected", DateTime.UtcNow);
		c.Remove("m0");
		c.Add(Message("m5", "new", DateTime.UtcNow.AddMinutes(9)));
		store.Save([c], []);
		store.Save([c], []);

		Conversation back = Assert.Single(new DatabaseChatStore(db).Load().Conversations);
		Assert.Equal(["corrected", "text 2", "text 3", "text 4", "new"], back.Messages.Select(m => m.Markdown));
		Assert.Equal(5, (await db.Find<StoredChatMessage>(0, int.MaxValue)).Count());

		// A conversation that is gone takes its messages with it.
		store.Save([], []);
		Assert.Empty(await db.Find<StoredChatMessage>(0, int.MaxValue));
		await db.Stop();
	}

	// ----- Nothing private is readable on disk -----

	[Fact]
	public async Task NothingPrivateIsReadableInAnyFile()
	{
		const string text = "Zebra-quartz 7731: the vault door code is ÅÄÖ-silver";
		const string password = "Room-pw-mauve-4471-ufo";
		const string title = "Unusual inbox title: velvet-otter-9312";

		Guid account = Guid.NewGuid();
		FilesProvider db = await this.OpenAsync();
		Conversation c = new(account, "kim@lab.local");
		c.Add(Message("m1", text, DateTime.UtcNow) with { Encrypted = true });
		new DatabaseChatStore(db).Save([c], [new RoomInfo(account, "ops@muc.lab.local", "Mille") { Password = password }]);
		new DatabaseAttentionStore(db).Save([Notification("n1", "# " + title + "\n\nThe backup failed.")]);
		await db.Stop();

		foreach (string secret in new[] { text, password, title })
		{
			foreach (Encoding e in new Encoding[] { Encoding.UTF8, Encoding.Unicode })
			{
				byte[] needle = e.GetBytes(secret);
				foreach (string file in Directory.EnumerateFiles(this.folder, "*", SearchOption.AllDirectories))
					Assert.True((await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken)).AsSpan().IndexOf(needle) < 0, $"Readable in {Path.GetFileName(file)} ({e.WebName})");
			}
		}

		// And it all comes back with the key.
		db = await this.OpenAsync();
		Assert.Equal(text, new DatabaseChatStore(db).Load().Conversations.Single().Messages.Single().Markdown);
		await db.Stop();
	}

	// ----- When the database cannot be opened -----

	[Fact]
	public async Task AWrongKeyIsCaughtWithoutTouchingTheFiles()
	{
		FilesProvider db = await this.OpenAsync();
		await new DatabaseAccountStore(db).SaveAsync([new AccountProfile { Host = "lab.local", UserName = "ops" }], TestContext.Current.CancellationToken);
		await db.Stop();

		DatabaseOpening wrong = await LocalDatabase.OpenAsync(this.DatabaseFolder, RandomNumberGenerator.GetBytes(32));
		Assert.False(wrong.IsOpen);
		Assert.NotNull(wrong.Problem);
		Assert.Equal("NAT cannot open what it has saved on this computer.", LocalDatabase.CannotOpen);

		// The files were not touched, so the right key still opens them in the same process.
		db = await this.OpenAsync();
		Assert.Single(await new DatabaseAccountStore(db).LoadAsync(TestContext.Current.CancellationToken));
		await db.Stop();
	}

	[Fact]
	public async Task StartingFreshMovesTheDatabaseAsideIntoADatedFolder()
	{
		FilesProvider db = await this.OpenAsync();
		await db.Stop();
		string[] before = [.. Directory.EnumerateFiles(this.DatabaseFolder).Select(Path.GetFileName).OfType<string>().Order()];

		string? aside = LocalDatabase.SetAside(this.DatabaseFolder, new DateTime(2026, 11, 12, 13, 14, 15));

		Assert.NotNull(aside);
		Assert.EndsWith("database unreadable 2026-11-12 131415", aside);
		Assert.False(Directory.Exists(this.DatabaseFolder));
		Assert.Equal(before, Directory.EnumerateFiles(aside).Select(Path.GetFileName).OfType<string>().Order());

		// Then a new key opens a new, empty database.
		db = await this.OpenAsync(RandomNumberGenerator.GetBytes(32));
		Assert.Empty(await new DatabaseAccountStore(db).LoadAsync(TestContext.Current.CancellationToken));
		await db.Stop();
	}

	// ----- Speed -----

	/// <summary>
	/// Opening a conversation of 2000 messages (the most one keeps): the old JSON file against the
	/// database. Prints what was measured; fails only when the database is far behind.
	/// </summary>
	[Fact]
	public async Task LongConversationsLoadAndSaveQuickly()
	{
		// Five conversations of 2000 messages each, the most a conversation keeps.
		const int perConversation = 2000;
		Guid account = Guid.NewGuid();
		DateTime t0 = DateTime.UtcNow.AddDays(-30);
		List<Conversation> all = [];
		for (int k = 0; k < 5; k++)
		{
			Conversation c = new(account, $"person{k}@lab.local");
			for (int i = 0; i < perConversation; i++)
				c.Add(Message($"m{k}-{i}", $"Message {i}: the backup on gamma ran at {t0.AddMinutes(i):HH:mm} and wrote {i * 37} files.", t0.AddMinutes(i)));
			all.Add(c);
		}

		JsonChatStore json = new(Path.Combine(this.folder, "chat.json"));
		Directory.CreateDirectory(this.folder);
		json.Save(all, []);
		FilesProvider db = await this.OpenAsync();
		DatabaseChatStore store = new(db);
		Stopwatch w = Stopwatch.StartNew();
		store.Save(all, []);
		TimeSpan dbFirstSave = w.Elapsed;

		// One new message at a time, ten times: the JSON file is written whole every time; the database writes one object.
		List<double> jsonOne = [], dbOne = [];
		for (int n = 0; n < 10; n++)
		{
			all[n % all.Count].Add(Message("new" + n, "One more.", DateTime.UtcNow.AddSeconds(n)));
			w.Restart();
			json.Save(all, []);
			jsonOne.Add(w.Elapsed.TotalMilliseconds);
			w.Restart();
			store.Save(all, []);
			dbOne.Add(w.Elapsed.TotalMilliseconds);
		}
		await db.Stop();

		w.Restart();
		Assert.Equal(all.Sum(c => c.Messages.Count), json.Load().Conversations.Sum(c => c.Messages.Count));
		double jsonLoad = w.Elapsed.TotalMilliseconds;

		db = await this.OpenAsync();
		w.Restart();
		Assert.Equal(all.Sum(c => c.Messages.Count), new DatabaseChatStore(db).Load().Conversations.Sum(c => c.Messages.Count));
		double dbLoad = w.Elapsed.TotalMilliseconds;
		await db.Stop();

		static double Median(List<double> v) => v.Order().ElementAt(v.Count / 2);
		output.WriteLine($"5 conversations of {perConversation} messages. Load at start: JSON {jsonLoad:F0} ms, database {dbLoad:F0} ms. " +
			$"Save after one new message (median of 10): JSON {Median(jsonOne):F0} ms, database {Median(dbOne):F1} ms. " +
			$"First save of everything into the database: {dbFirstSave.TotalMilliseconds:F0} ms.");
		Assert.True(dbLoad < 5000, $"Loading took {dbLoad:F0} ms.");
		Assert.True(Median(dbOne) < Median(jsonOne), "Saving one message should cost less than writing the whole history again.");
	}

	internal static ChatMessage Message(string id, string text, DateTime at)
		=> new() { Id = id, MessageId = "x" + id, Direction = ChatDirection.In, Timestamp = at, Markdown = text, Text = text };

	internal static NeuronNotification Notification(string id, string markdown)
		=> new() { Id = id, AccountId = Guid.NewGuid(), NeuronJid = "neuron@lab.local", Received = DateTime.UtcNow, Markdown = markdown };
}
