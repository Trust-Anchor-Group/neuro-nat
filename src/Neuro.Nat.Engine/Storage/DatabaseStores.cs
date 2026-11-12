using System.Text.Json;
using System.Text.Json.Serialization;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Chat;
using Waher.Persistence;
using Waher.Persistence.Filters;

namespace Neuro.Nat.Engine.Storage;

/// <summary>Shared by the database stores.</summary>
internal static class Stored
{
	public static readonly JsonSerializerOptions Json = new()
	{
		Converters = { new JsonStringEnumConverter() },
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public static string Id(Guid id) => id.ToString("N");

	public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

	public static T? FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);

	/// <summary>
	/// Runs database work to the end from code that cannot wait for it. On a thread of its own, so
	/// it never waits for the thread it was called from (the UI thread, for one).
	/// </summary>
	public static void Run(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

	public static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

	public static async Task<List<T>> All<T>(IDatabaseProvider db, Filter? filter = null, params string[] sortOrder) where T : class
		=> [.. filter is null ? await db.Find<T>(0, int.MaxValue, sortOrder) : await db.Find<T>(0, int.MaxValue, filter, sortOrder)];
}

/// <summary>Account profiles in the database.</summary>
public sealed class DatabaseAccountStore(IDatabaseProvider db) : IAccountStore
{
	private readonly SemaphoreSlim gate = new(1, 1);

	public async Task<IReadOnlyList<AccountProfile>> LoadAsync(CancellationToken cancel = default)
	{
		await this.gate.WaitAsync(cancel);
		try
		{
			return [.. (await Stored.All<StoredAccount>(db)).Select(r => Stored.FromJson<AccountProfile>(r.Json)).OfType<AccountProfile>()];
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
			Dictionary<string, StoredAccount> rows = [];
			foreach (StoredAccount r in await Stored.All<StoredAccount>(db))
			{
				if (!rows.TryAdd(r.AccountId, r))
					await db.Delete(r);
			}

			foreach (AccountProfile p in profiles)
			{
				string json = Stored.ToJson(p);
				if (rows.Remove(Stored.Id(p.Id), out StoredAccount? row))
				{
					if (row.Json != json)
					{
						row.Json = json;
						await db.Update(row);
					}
				}
				else
					await db.Insert(new StoredAccount { AccountId = Stored.Id(p.Id), Json = json });
			}

			foreach (StoredAccount gone in rows.Values)
				await db.Delete(gone);
		}
		finally
		{
			this.gate.Release();
		}
	}
}

/// <summary>Sign-ins in the database, which is encrypted with a key the operating system protects.</summary>
public sealed class DatabaseCredentialVault(IDatabaseProvider db) : ICredentialVault
{
	private readonly SemaphoreSlim gate = new(1, 1);

	public async Task<AccountSecret?> GetAsync(Guid accountId, CancellationToken cancel = default)
	{
		await this.gate.WaitAsync(cancel);
		try
		{
			StoredSignIn? row = (await this.RowsAsync(accountId)).FirstOrDefault();
			return row is null ? null : Stored.FromJson<AccountSecret>(row.Json);
		}
		finally
		{
			this.gate.Release();
		}
	}

	public async Task SetAsync(Guid accountId, AccountSecret secret, CancellationToken cancel = default)
	{
		await this.gate.WaitAsync(cancel);
		try
		{
			List<StoredSignIn> rows = await this.RowsAsync(accountId);
			string json = Stored.ToJson(secret);
			if (rows.Count == 0)
				await db.Insert(new StoredSignIn { AccountId = Stored.Id(accountId), Json = json });
			else
			{
				rows[0].Json = json;
				await db.Update(rows[0]);
				foreach (StoredSignIn extra in rows.Skip(1))
					await db.Delete(extra);
			}
		}
		finally
		{
			this.gate.Release();
		}
	}

	public async Task RemoveAsync(Guid accountId, CancellationToken cancel = default)
	{
		await this.gate.WaitAsync(cancel);
		try
		{
			foreach (StoredSignIn row in await this.RowsAsync(accountId))
				await db.Delete(row);
		}
		finally
		{
			this.gate.Release();
		}
	}

	private Task<List<StoredSignIn>> RowsAsync(Guid accountId)
		=> Stored.All<StoredSignIn>(db, new FilterFieldEqualTo("AccountId", Stored.Id(accountId)));
}

/// <summary>Inbox items from Neurons in the database, one object per item. Only what changed is written.</summary>
public sealed class DatabaseAttentionStore(IDatabaseProvider db) : IAttentionStore
{
	private readonly object gate = new();
	private readonly Dictionary<string, (StoredInboxItem Row, string Json)> written = [];

	private sealed record Item(
		string Id, Guid AccountId, string NeuronJid, string? MessageId, DateTime Received, DateTime? Updated,
		string Markdown, string Text, AttentionState State, DateTime? HandledAt);

	public IReadOnlyList<NeuronNotification> Load()
	{
		lock (this.gate)
		{
			List<StoredInboxItem> rows = Stored.Run(() => Stored.All<StoredInboxItem>(db));
			this.written.Clear();
			List<NeuronNotification> result = [];
			foreach (StoredInboxItem row in rows)
			{
				if (this.written.ContainsKey(row.ItemId) || Stored.FromJson<Item>(row.Json) is not Item s)
					continue;

				this.written[row.ItemId] = (row, row.Json);
				result.Add(new NeuronNotification
				{
					Id = s.Id,
					AccountId = s.AccountId,
					NeuronJid = s.NeuronJid,
					MessageId = s.MessageId,
					Received = s.Received,
					Updated = s.Updated,
					Markdown = s.Markdown,
					Text = s.Text,
					State = s.State,
					HandledAt = s.HandledAt,
				});
			}

			return result;
		}
	}

	public void Save(IReadOnlyList<NeuronNotification> items)
	{
		lock (this.gate)
		{
			Stored.Run(async () =>
			{
				HashSet<string> present = [];
				foreach (NeuronNotification n in items)
				{
					if (!present.Add(n.Id))
						continue;

					string json = Stored.ToJson(new Item(n.Id, n.AccountId, n.NeuronJid, n.MessageId, n.Received, n.Updated, n.Markdown, n.Text, n.State, n.HandledAt));
					if (this.written.TryGetValue(n.Id, out (StoredInboxItem Row, string Json) w))
					{
						if (w.Json == json)
							continue;
						w.Row.Json = json;
						await db.Update(w.Row);
						this.written[n.Id] = (w.Row, json);
					}
					else
					{
						StoredInboxItem row = new() { ItemId = n.Id, Json = json };
						await db.Insert(row);
						this.written[n.Id] = (row, json);
					}
				}

				foreach (string gone in this.written.Keys.Where(k => !present.Contains(k)).ToList())
				{
					await db.Delete(this.written[gone].Row);
					this.written.Remove(gone);
				}
			});
		}
	}
}

/// <summary>
/// Conversations in the database: each message is an object of its own, read a conversation at a
/// time in order. Messages never change in place (a correction replaces the message), so what is
/// new or changed is found by reference, and only that is written.
/// </summary>
public sealed class DatabaseChatStore(IDatabaseProvider db) : IChatStore
{
	private readonly object gate = new();
	private readonly Dictionary<string, (StoredChatMessage Row, ChatMessage Message)> messages = [];
	private readonly Dictionary<string, (StoredConversation Row, string Json)> conversations = [];
	private readonly Dictionary<string, (StoredRoom Row, string Json)> rooms = [];

	private sealed record ConversationState(int Unread, bool IsRoom, string? Subject, RoomNotify Notify, DateTime? MutedUntil, string? FirstUnreadId);

	private sealed record RoomState(string NickName, string? Password);

	private static string Key(Guid account, string jid) => Stored.Id(account) + "|" + jid.ToLowerInvariant();

	private static string Key(Guid account, string jid, string messageId) => Key(account, jid) + "|" + messageId;

	public (IReadOnlyList<Conversation> Conversations, IReadOnlyList<RoomInfo> Rooms) Load()
	{
		lock (this.gate)
			return Stored.Run(this.LoadAsync);
	}

	private async Task<(IReadOnlyList<Conversation>, IReadOnlyList<RoomInfo>)> LoadAsync()
	{
		this.messages.Clear();
		this.conversations.Clear();
		this.rooms.Clear();

		// All messages in one pass, in index order (account, conversation, time), then shared out.
		Dictionary<string, List<StoredChatMessage>> byConversation = [];
		foreach (StoredChatMessage m in await Stored.All<StoredChatMessage>(db, null, "AccountId", "Jid", "Timestamp"))
		{
			string ck = m.AccountId + "|" + m.Jid.ToLowerInvariant();
			if (!byConversation.TryGetValue(ck, out List<StoredChatMessage>? list))
				byConversation[ck] = list = [];
			list.Add(m);
		}

		List<Conversation> result = [];
		foreach (StoredConversation row in await Stored.All<StoredConversation>(db))
		{
			if (!Guid.TryParse(row.AccountId, out Guid account) || Stored.FromJson<ConversationState>(row.Json) is not ConversationState s)
				continue;

			string key = Key(account, row.Jid);
			if (this.conversations.ContainsKey(key))
				continue;

			this.conversations[key] = (row, row.Json);
			Conversation c = new(account, row.Jid)
			{
				Unread = s.Unread,
				IsRoom = s.IsRoom,
				Subject = s.Subject,
				Notify = s.Notify,
				MutedUntil = s.MutedUntil,
				FirstUnreadId = s.FirstUnreadId,
			};

			foreach (StoredChatMessage m in byConversation.GetValueOrDefault(row.AccountId + "|" + row.Jid.ToLowerInvariant()) ?? [])
			{
				if (Stored.FromJson<ChatMessage>(m.Json) is not ChatMessage message)
					continue;

				string mk = Key(account, row.Jid, m.MessageKey);
				if (this.messages.ContainsKey(mk))
					continue;

				c.Add(message);
				this.messages[mk] = (m, message);
			}

			result.Add(c);
		}

		List<RoomInfo> roomList = [];
		foreach (StoredRoom row in await Stored.All<StoredRoom>(db))
		{
			if (!Guid.TryParse(row.AccountId, out Guid account) || Stored.FromJson<RoomState>(row.Json) is not RoomState s)
				continue;

			string key = Key(account, row.Jid);
			if (this.rooms.TryAdd(key, (row, row.Json)))
				roomList.Add(new RoomInfo(account, row.Jid, s.NickName) { Password = s.Password });
		}

		return (result, roomList);
	}

	public void Save(IReadOnlyList<Conversation> conversations, IReadOnlyList<RoomInfo> rooms)
	{
		lock (this.gate)
			Stored.Run(() => this.SaveAsync(conversations, rooms));
	}

	private async Task SaveAsync(IReadOnlyList<Conversation> conversations, IReadOnlyList<RoomInfo> rooms)
	{
		HashSet<string> keptConversations = [];
		HashSet<string> keptMessages = [];
		List<object> inserts = [];

		foreach (Conversation c in conversations)
		{
			string ck = Key(c.AccountId, c.BareJid);
			if (!keptConversations.Add(ck))
				continue;

			string json = Stored.ToJson(new ConversationState(c.Unread, c.IsRoom, c.Subject, c.Notify, c.MutedUntil, c.FirstUnreadId));
			if (this.conversations.TryGetValue(ck, out (StoredConversation Row, string Json) w))
			{
				if (w.Json != json)
				{
					w.Row.Json = json;
					await db.Update(w.Row);
					this.conversations[ck] = (w.Row, json);
				}
			}
			else
			{
				StoredConversation row = new() { AccountId = Stored.Id(c.AccountId), Jid = c.BareJid, Json = json };
				inserts.Add(row);
				this.conversations[ck] = (row, json);
			}

			foreach (ChatMessage m in c.Messages)
			{
				string mk = Key(c.AccountId, c.BareJid, m.Id);
				if (!keptMessages.Add(mk))
					continue;

				if (this.messages.TryGetValue(mk, out (StoredChatMessage Row, ChatMessage Message) known))
				{
					if (ReferenceEquals(known.Message, m))
						continue;

					known.Row.Json = Stored.ToJson(m);
					known.Row.Timestamp = m.Timestamp;
					await db.Update(known.Row);
					this.messages[mk] = (known.Row, m);
				}
				else
				{
					StoredChatMessage row = new()
					{
						AccountId = Stored.Id(c.AccountId),
						Jid = c.BareJid,
						Timestamp = m.Timestamp,
						MessageKey = m.Id,
						Json = Stored.ToJson(m),
					};
					inserts.Add(row);
					this.messages[mk] = (row, m);
				}
			}
		}

		HashSet<string> keptRooms = [];
		foreach (RoomInfo r in rooms)
		{
			string rk = Key(r.AccountId, r.RoomJid);
			if (!keptRooms.Add(rk))
				continue;

			string json = Stored.ToJson(new RoomState(r.NickName, r.Password));
			if (this.rooms.TryGetValue(rk, out (StoredRoom Row, string Json) w))
			{
				if (w.Json != json)
				{
					w.Row.Json = json;
					await db.Update(w.Row);
					this.rooms[rk] = (w.Row, json);
				}
			}
			else
			{
				StoredRoom row = new() { AccountId = Stored.Id(r.AccountId), Jid = r.RoomJid, Json = json };
				inserts.Add(row);
				this.rooms[rk] = (row, json);
			}
		}

		if (inserts.Count > 0)
			await db.Insert(inserts);

		// Messages that fell out (a conversation keeps the latest ones), conversations and rooms that are gone.
		List<object> deletes = [];
		foreach (string gone in this.messages.Keys.Where(k => !keptMessages.Contains(k)).ToList())
		{
			deletes.Add(this.messages[gone].Row);
			this.messages.Remove(gone);
		}
		foreach (string gone in this.conversations.Keys.Where(k => !keptConversations.Contains(k)).ToList())
		{
			deletes.Add(this.conversations[gone].Row);
			this.conversations.Remove(gone);
		}
		foreach (string gone in this.rooms.Keys.Where(k => !keptRooms.Contains(k)).ToList())
		{
			deletes.Add(this.rooms[gone].Row);
			this.rooms.Remove(gone);
		}

		if (deletes.Count > 0)
			await db.Delete(deletes);
	}
}

/// <summary>Settings in the database, one group per name, kept as the group's own JSON.</summary>
public sealed class DatabaseSettings(IDatabaseProvider db)
{
	private readonly object gate = new();

	/// <summary>The saved JSON of a group, or null when it was never saved.</summary>
	public string? Get(string name)
	{
		lock (this.gate)
			return Stored.Run(() => this.RowsAsync(name)).FirstOrDefault()?.Json;
	}

	public void Set(string name, string json)
	{
		lock (this.gate)
		{
			Stored.Run(async () =>
			{
				List<StoredSetting> rows = await this.RowsAsync(name);
				if (rows.Count == 0)
					await db.Insert(new StoredSetting { Name = name, Json = json });
				else
				{
					if (rows[0].Json != json)
					{
						rows[0].Json = json;
						await db.Update(rows[0]);
					}
					foreach (StoredSetting extra in rows.Skip(1))
						await db.Delete(extra);
				}
			});
		}
	}

	public T? Get<T>(string name) where T : class
	{
		string? json = this.Get(name);
		if (json is null)
			return null;

		try
		{
			return JsonSerializer.Deserialize<T>(json);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public void Set<T>(string name, T value) => this.Set(name, JsonSerializer.Serialize(value));

	private Task<List<StoredSetting>> RowsAsync(string name)
		=> Stored.All<StoredSetting>(db, new FilterFieldEqualTo("Name", name));
}
