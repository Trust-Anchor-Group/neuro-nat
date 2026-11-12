using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Chat;
using Waher.Persistence;

namespace Neuro.Nat.Engine.Storage;

/// <summary>What <see cref="LegacyImport.RunAsync"/> did with one kind of old file.</summary>
/// <param name="What">Which data, in a word: accounts, sign-ins, chat, inbox, ui, alerts.</param>
/// <param name="Count">How many things were found in the old file.</param>
/// <param name="Error">Why it failed. The old file is still there then.</param>
public sealed record ImportStep(string What, int Count, Exception? Error = null)
{
	public bool Failed => this.Error is not null;
}

/// <summary>Accounts kept for this run only, where there is no database to keep them in.</summary>
public sealed class InMemoryAccountStore : IAccountStore
{
	private IReadOnlyList<AccountProfile> profiles = [];

	public Task<IReadOnlyList<AccountProfile>> LoadAsync(CancellationToken cancel = default) => Task.FromResult(this.profiles);

	public Task SaveAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken cancel = default)
	{
		this.profiles = [.. profiles];
		return Task.CompletedTask;
	}
}

/// <summary>
/// Moves what NAT kept in plain files before it had a database into the database, once. For each
/// kind of file: read it, write what is not in the database yet, read the database back and check
/// that everything from the file is there, and only then delete the file. Anything that fails
/// keeps its file, so the next start tries again; running it twice never doubles anything. The
/// key to the database (secrets\database.key), the log and update-source.txt stay outside.
/// </summary>
public sealed class LegacyImport(IDatabaseProvider db, string dataFolder)
{
	/// <summary>Reads a sign-in from an old secrets\&lt;id&gt;.bin file. Supplied by the host, which knows how they were protected.</summary>
	public Func<Guid, Task<AccountSecret?>>? ReadOldSignIn { get; init; }

	/// <summary>For tests: called after a kind of data was written and before its file is deleted.</summary>
	internal Action<string>? AfterWrite { get; init; }

	private string File(string name) => Path.Combine(dataFolder, name);

	/// <summary>True when an old file is still there to import.</summary>
	public bool HasWork()
		=> new[] { "accounts.json", "chat.json", "attention.json", "ui.json", "alerts.json" }.Any(f => System.IO.File.Exists(this.File(f)))
			|| this.OldSignInFiles().Any();

	public async Task<IReadOnlyList<ImportStep>> RunAsync()
	{
		List<ImportStep> steps = [];
		await Step(steps, "accounts", this.AccountsAsync);
		await Step(steps, "sign-ins", this.SignInsAsync);
		await Step(steps, "chat", this.ChatAsync);
		await Step(steps, "inbox", this.InboxAsync);
		await Step(steps, "ui", () => this.SettingAsync("ui", "ui.json"));
		await Step(steps, "alerts", () => this.SettingAsync("alerts", "alerts.json"));
		return steps;
	}

	private static async Task Step(List<ImportStep> steps, string what, Func<Task<int?>> work)
	{
		try
		{
			if (await work() is int count)
				steps.Add(new ImportStep(what, count));
		}
		catch (Exception ex)
		{
			steps.Add(new ImportStep(what, 0, ex));
		}
	}

	private void Written(string what) => this.AfterWrite?.Invoke(what);

	/// <summary>The old stores set a damaged file aside and read nothing; here a damaged file must fail the step and stay.</summary>
	private static void MustParse(string file)
	{
		using FileStream f = System.IO.File.OpenRead(file);
		using System.Text.Json.JsonDocument _ = System.Text.Json.JsonDocument.Parse(f);
	}

	private static void Check(bool ok, string what)
	{
		if (!ok)
			throw new InvalidDataException($"Read back from the database, the {what} did not match the old file.");
	}

	private async Task<int?> AccountsAsync()
	{
		string file = this.File("accounts.json");
		if (!System.IO.File.Exists(file))
			return null;
		MustParse(file);

		IReadOnlyList<AccountProfile> old = await new JsonAccountStore(file).LoadAsync();
		DatabaseAccountStore store = new(db);
		List<AccountProfile> merged = [.. await store.LoadAsync()];

		// What is in the database already is newer than the file.
		foreach (AccountProfile p in old)
		{
			if (!merged.Any(m => m.Id == p.Id))
				merged.Add(p);
		}

		await store.SaveAsync(merged);
		this.Written("accounts");

		IReadOnlyList<AccountProfile> back = await new DatabaseAccountStore(db).LoadAsync();
		Check(old.All(p => back.Any(b => b.Id == p.Id)), "accounts");
		System.IO.File.Delete(file);
		return old.Count;
	}

	private IEnumerable<(Guid Id, string File)> OldSignInFiles()
	{
		string folder = this.File("secrets");
		if (!Directory.Exists(folder))
			yield break;

		foreach (string f in Directory.EnumerateFiles(folder, "*.bin"))
		{
			if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out Guid id))
				yield return (id, f);
		}
	}

	private async Task<int?> SignInsAsync()
	{
		List<(Guid Id, string File)> files = [.. this.OldSignInFiles()];
		if (files.Count == 0)
			return null;
		if (this.ReadOldSignIn is null)
			throw new InvalidOperationException("Old sign-in files are there, but nothing to read them with.");

		DatabaseCredentialVault vault = new(db);
		foreach ((Guid id, string file) in files)
		{
			AccountSecret? old = await this.ReadOldSignIn(id);
			if (old is null)
				throw new InvalidDataException("An old sign-in file could not be read.");

			// A sign-in already in the database is newer than the file.
			AccountSecret? current = await vault.GetAsync(id);
			if (current is null)
				await vault.SetAsync(id, old);
			this.Written("sign-ins");

			AccountSecret? back = await new DatabaseCredentialVault(db).GetAsync(id);
			Check(back is not null && (current is not null || back == old), "sign-in");
			System.IO.File.Delete(file);
		}

		return files.Count;
	}

	private async Task<int?> ChatAsync()
	{
		string file = this.File("chat.json");
		if (!System.IO.File.Exists(file))
			return null;
		MustParse(file);

		(IReadOnlyList<Conversation> oldConversations, IReadOnlyList<RoomInfo> oldRooms) = new JsonChatStore(file).Load();
		DatabaseChatStore store = new(db);
		(IReadOnlyList<Conversation> conversations, IReadOnlyList<RoomInfo> rooms) = store.Load();
		List<Conversation> merged = [.. conversations];
		List<RoomInfo> mergedRooms = [.. rooms];

		foreach (Conversation old in oldConversations)
		{
			Conversation? c = merged.FirstOrDefault(x => x.AccountId == old.AccountId && string.Equals(x.BareJid, old.BareJid, StringComparison.OrdinalIgnoreCase));
			if (c is null)
			{
				merged.Add(old);
				continue;
			}

			foreach (ChatMessage m in old.Messages)
			{
				if (c.Message(m.Id) is null)
					c.Add(m);
			}
		}

		foreach (RoomInfo r in oldRooms)
		{
			if (!mergedRooms.Any(x => x.AccountId == r.AccountId && string.Equals(x.RoomJid, r.RoomJid, StringComparison.OrdinalIgnoreCase)))
				mergedRooms.Add(r);
		}

		store.Save(merged, mergedRooms);
		this.Written("chat");

		(IReadOnlyList<Conversation> backConversations, IReadOnlyList<RoomInfo> backRooms) = new DatabaseChatStore(db).Load();
		foreach (Conversation old in oldConversations)
		{
			Conversation? back = backConversations.FirstOrDefault(x => x.AccountId == old.AccountId && string.Equals(x.BareJid, old.BareJid, StringComparison.OrdinalIgnoreCase));
			Check(back is not null && old.Messages.All(m => back.Message(m.Id) is not null), "chat messages");
		}
		Check(oldRooms.All(r => backRooms.Any(x => x.AccountId == r.AccountId && string.Equals(x.RoomJid, r.RoomJid, StringComparison.OrdinalIgnoreCase))), "chat rooms");

		System.IO.File.Delete(file);
		return oldConversations.Sum(c => c.Messages.Count);
	}

	private async Task<int?> InboxAsync()
	{
		string file = this.File("attention.json");
		if (!System.IO.File.Exists(file))
			return null;
		MustParse(file);

		IReadOnlyList<NeuronNotification> old = new JsonAttentionStore(file).Load();
		DatabaseAttentionStore store = new(db);
		List<NeuronNotification> merged = [.. store.Load()];
		foreach (NeuronNotification n in old)
		{
			if (!merged.Any(m => m.Id == n.Id))
				merged.Add(n);
		}

		store.Save(merged);
		this.Written("inbox");

		IReadOnlyList<NeuronNotification> back = new DatabaseAttentionStore(db).Load();
		Check(old.All(n => back.Any(b => b.Id == n.Id)), "Inbox items");
		System.IO.File.Delete(file);
		await Task.CompletedTask;
		return old.Count;
	}

	private async Task<int?> SettingAsync(string name, string fileName)
	{
		string file = this.File(fileName);
		if (!System.IO.File.Exists(file))
			return null;

		MustParse(file);
		string json = await System.IO.File.ReadAllTextAsync(file);
		DatabaseSettings settings = new(db);

		// Settings saved in the database already are newer than the file.
		if (settings.Get(name) is null)
			settings.Set(name, json);
		this.Written(name);

		Check(new DatabaseSettings(db).Get(name) is not null, name + " settings");
		System.IO.File.Delete(file);
		return 1;
	}
}
