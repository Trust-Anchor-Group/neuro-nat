using System.Collections.Concurrent;
using Waher.Persistence;
using Waher.Persistence.Filters;

namespace Neuro.Nat.Engine.Storage;

/// <summary>
/// Remembers, per account and bare JID, whether a contact answered that it is a Neuron. So a
/// Neuron that is down when NAT starts still shows, and a person is never shown as a Neuron.
/// A "no" is not final: the contact is asked again when it comes online, and a new answer
/// replaces the old one.
/// </summary>
public interface IKnownNeuronStore
{
	/// <summary>Every answer kept for an account, by bare JID.</summary>
	IReadOnlyDictionary<string, bool> Load(Guid accountId);

	/// <summary>Keeps an answer, replacing an earlier one.</summary>
	Task SetAsync(Guid accountId, string bareJid, bool isNeuron);

	/// <summary>Forgets the answer for a contact, and what it calls itself.</summary>
	Task ForgetAsync(Guid accountId, string bareJid);

	/// <summary>What the Neurons of an account call themselves, by bare JID. Only those that have said.</summary>
	IReadOnlyDictionary<string, string> LoadNames(Guid accountId);

	/// <summary>Keeps what a Neuron calls itself, replacing an earlier name.</summary>
	Task SetNameAsync(Guid accountId, string bareJid, string ownName);
}

/// <summary>Answers kept only while NAT runs: for tests and for systems without a database.</summary>
public sealed class InMemoryKnownNeuronStore : IKnownNeuronStore
{
	private readonly ConcurrentDictionary<(Guid, string), bool> answers = new();

	private static (Guid, string) Key(Guid accountId, string bareJid) => (accountId, bareJid.ToLowerInvariant());

	public IReadOnlyDictionary<string, bool> Load(Guid accountId)
		=> this.answers.Where(p => p.Key.Item1 == accountId).ToDictionary(p => p.Key.Item2, p => p.Value, StringComparer.OrdinalIgnoreCase);

	public Task SetAsync(Guid accountId, string bareJid, bool isNeuron)
	{
		this.answers[Key(accountId, bareJid)] = isNeuron;
		return Task.CompletedTask;
	}

	public Task ForgetAsync(Guid accountId, string bareJid)
	{
		this.answers.TryRemove(Key(accountId, bareJid), out _);
		this.names.TryRemove(Key(accountId, bareJid), out _);
		return Task.CompletedTask;
	}

	private readonly ConcurrentDictionary<(Guid, string), string> names = new();

	public IReadOnlyDictionary<string, string> LoadNames(Guid accountId)
		=> this.names.Where(p => p.Key.Item1 == accountId).ToDictionary(p => p.Key.Item2, p => p.Value, StringComparer.OrdinalIgnoreCase);

	public Task SetNameAsync(Guid accountId, string bareJid, string ownName)
	{
		this.names[Key(accountId, bareJid)] = ownName;
		return Task.CompletedTask;
	}
}

/// <summary>Answers kept in the encrypted database, one object per account and contact.</summary>
public sealed class DatabaseKnownNeuronStore(IDatabaseProvider db) : IKnownNeuronStore
{
	private readonly SemaphoreSlim gate = new(1, 1);

	public IReadOnlyDictionary<string, bool> Load(Guid accountId)
		=> Stored.Run(async () =>
		{
			await this.gate.WaitAsync();
			try
			{
				Dictionary<string, bool> result = new(StringComparer.OrdinalIgnoreCase);
				Dictionary<string, string> named = new(StringComparer.OrdinalIgnoreCase);
				foreach (StoredKnownNeuron row in await Stored.All<StoredKnownNeuron>(db, new FilterFieldEqualTo("AccountId", Stored.Id(accountId))))
				{
					result[row.Jid] = row.IsNeuron;
					if (!string.IsNullOrWhiteSpace(row.OwnName))
						named[row.Jid] = row.OwnName;
				}

				this.names[accountId] = named;
				return (IReadOnlyDictionary<string, bool>)result;
			}
			finally
			{
				this.gate.Release();
			}
		});

	public async Task SetAsync(Guid accountId, string bareJid, bool isNeuron)
	{
		await this.gate.WaitAsync();
		try
		{
			List<StoredKnownNeuron> rows = await this.RowsAsync(accountId, bareJid);
			if (rows.Count == 0)
			{
				await db.Insert(new StoredKnownNeuron { AccountId = Stored.Id(accountId), Jid = bareJid.ToLowerInvariant(), IsNeuron = isNeuron, Answered = DateTime.UtcNow });
				return;
			}

			if (rows[0].IsNeuron != isNeuron)
			{
				rows[0].IsNeuron = isNeuron;
				rows[0].Answered = DateTime.UtcNow;
				await db.Update(rows[0]);
			}

			foreach (StoredKnownNeuron extra in rows.Skip(1))
				await db.Delete(extra);
		}
		finally
		{
			this.gate.Release();
		}
	}

	public async Task ForgetAsync(Guid accountId, string bareJid)
	{
		await this.gate.WaitAsync();
		try
		{
			foreach (StoredKnownNeuron row in await this.RowsAsync(accountId, bareJid))
				await db.Delete(row);

			lock (this.names)
			{
				if (this.names.TryGetValue(accountId, out Dictionary<string, string>? named))
					named.Remove(bareJid);
			}
		}
		finally
		{
			this.gate.Release();
		}
	}

	/// <summary>The names read with the answers, by account: one read of the database gives both.</summary>
	private readonly ConcurrentDictionary<Guid, Dictionary<string, string>> names = new();

	public IReadOnlyDictionary<string, string> LoadNames(Guid accountId)
	{
		if (!this.names.ContainsKey(accountId))
			this.Load(accountId);

		lock (this.names)
		{
			return this.names.TryGetValue(accountId, out Dictionary<string, string>? named)
				? new Dictionary<string, string>(named, StringComparer.OrdinalIgnoreCase)
				: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}
	}

	public async Task SetNameAsync(Guid accountId, string bareJid, string ownName)
	{
		await this.gate.WaitAsync();
		try
		{
			string jid = bareJid.ToLowerInvariant();
			List<StoredKnownNeuron> rows = await this.RowsAsync(accountId, bareJid);
			if (rows.Count == 0)
			{
				// Only a Neuron is asked for its name.
				await db.Insert(new StoredKnownNeuron { AccountId = Stored.Id(accountId), Jid = jid, IsNeuron = true, OwnName = ownName, Answered = DateTime.UtcNow });
			}
			else if (!string.Equals(rows[0].OwnName, ownName, StringComparison.Ordinal))
			{
				rows[0].OwnName = ownName;
				await db.Update(rows[0]);
			}

			lock (this.names)
			{
				if (this.names.TryGetValue(accountId, out Dictionary<string, string>? named))
					named[jid] = ownName;
			}
		}
		finally
		{
			this.gate.Release();
		}
	}

	private Task<List<StoredKnownNeuron>> RowsAsync(Guid accountId, string bareJid)
		=> Stored.All<StoredKnownNeuron>(db, new FilterAnd(
			new FilterFieldEqualTo("AccountId", Stored.Id(accountId)),
			new FilterFieldEqualTo("Jid", bareJid.ToLowerInvariant())));
}
