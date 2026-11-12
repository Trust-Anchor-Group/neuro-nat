using System.Collections.Concurrent;
using Waher.Persistence;
using Waher.Persistence.Filters;

namespace Neuro.Nat.Engine.Storage;

/// <summary>One answer a feature keeps: the body, its ETag, and when it was last asked for (UTC).</summary>
public sealed record FeatureAnswer(string Body, string? ETag, DateTime AskedAt);

/// <summary>Where features keep what they fetched, so the numbers are there at the next start.</summary>
public interface IFeatureAnswerStore
{
	/// <summary>Everything a feature kept, by its key.</summary>
	Task<IReadOnlyDictionary<string, FeatureAnswer>> LoadAsync(string feature, CancellationToken cancel = default);

	Task SaveAsync(string feature, string key, FeatureAnswer answer, CancellationToken cancel = default);
}

/// <summary>Kept only while NAT runs: where there is no database, and in tests.</summary>
public sealed class InMemoryFeatureAnswerStore : IFeatureAnswerStore
{
	private readonly ConcurrentDictionary<(string, string), FeatureAnswer> answers = new();

	public Task<IReadOnlyDictionary<string, FeatureAnswer>> LoadAsync(string feature, CancellationToken cancel = default)
		=> Task.FromResult<IReadOnlyDictionary<string, FeatureAnswer>>(this.answers.Where(p => p.Key.Item1 == feature).ToDictionary(p => p.Key.Item2, p => p.Value));

	public Task SaveAsync(string feature, string key, FeatureAnswer answer, CancellationToken cancel = default)
	{
		this.answers[(feature, key)] = answer;
		return Task.CompletedTask;
	}
}

/// <summary>Answers in the encrypted database, one row per feature and key.</summary>
public sealed class DatabaseFeatureAnswerStore(IDatabaseProvider db) : IFeatureAnswerStore
{
	private readonly SemaphoreSlim gate = new(1, 1);

	public async Task<IReadOnlyDictionary<string, FeatureAnswer>> LoadAsync(string feature, CancellationToken cancel = default)
	{
		await this.gate.WaitAsync(cancel);
		try
		{
			Dictionary<string, FeatureAnswer> found = [];
			foreach (StoredFeatureAnswer row in await Stored.All<StoredFeatureAnswer>(db, new FilterFieldEqualTo("Feature", feature)))
			{
				if (Stored.FromJson<FeatureAnswer>(row.Json) is FeatureAnswer a)
					found[row.Key] = a;
			}

			return found;
		}
		finally
		{
			this.gate.Release();
		}
	}

	public async Task SaveAsync(string feature, string key, FeatureAnswer answer, CancellationToken cancel = default)
	{
		await this.gate.WaitAsync(cancel);
		try
		{
			List<StoredFeatureAnswer> rows = await Stored.All<StoredFeatureAnswer>(db,
				new FilterAnd(new FilterFieldEqualTo("Feature", feature), new FilterFieldEqualTo("Key", key)));
			string json = Stored.ToJson(answer);
			if (rows.Count == 0)
				await db.Insert(new StoredFeatureAnswer { Feature = feature, Key = key, Json = json });
			else
			{
				rows[0].Json = json;
				await db.Update(rows[0]);
				foreach (StoredFeatureAnswer extra in rows.Skip(1))
					await db.Delete(extra);
			}
		}
		finally
		{
			this.gate.Release();
		}
	}
}
