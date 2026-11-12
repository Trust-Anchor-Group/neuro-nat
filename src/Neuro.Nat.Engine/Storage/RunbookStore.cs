using System.Collections.Concurrent;
using Neuro.Nat.Engine.Runbooks;
using Waher.Persistence;
using Waher.Persistence.Filters;

namespace Neuro.Nat.Engine.Storage;

/// <summary>Where runbooks and the records of their runs are kept.</summary>
public interface IRunbookStore
{
	/// <summary>Every runbook someone made or kept. The ready-made ones are not here.</summary>
	Task<IReadOnlyList<Runbook>> LoadAsync();

	/// <summary>Keeps a runbook, replacing the one with the same id.</summary>
	Task SaveAsync(Runbook runbook);

	/// <summary>Forgets a runbook. The records of its runs stay.</summary>
	Task DeleteAsync(Guid runbookId);

	/// <summary>Keeps the record of a run.</summary>
	Task AddRecordAsync(RunbookRecord record);

	/// <summary>The records of a runbook's runs, latest first.</summary>
	Task<IReadOnlyList<RunbookRecord>> RecordsAsync(Guid runbookId);
}

/// <summary>Runbooks kept only while NAT runs: for tests and for systems without a database.</summary>
public sealed class InMemoryRunbookStore : IRunbookStore
{
	private readonly ConcurrentDictionary<Guid, Runbook> runbooks = new();
	private readonly ConcurrentQueue<RunbookRecord> records = new();

	public Task<IReadOnlyList<Runbook>> LoadAsync() => Task.FromResult<IReadOnlyList<Runbook>>([.. this.runbooks.Values.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)]);

	public Task SaveAsync(Runbook runbook)
	{
		this.runbooks[runbook.Id] = runbook;
		return Task.CompletedTask;
	}

	public Task DeleteAsync(Guid runbookId)
	{
		this.runbooks.TryRemove(runbookId, out _);
		return Task.CompletedTask;
	}

	public Task AddRecordAsync(RunbookRecord record)
	{
		this.records.Enqueue(record);
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<RunbookRecord>> RecordsAsync(Guid runbookId)
		=> Task.FromResult<IReadOnlyList<RunbookRecord>>([.. this.records.Where(r => r.RunbookId == runbookId).OrderByDescending(r => r.Started)]);
}

/// <summary>Runbooks and run records in the encrypted database, one object each.</summary>
public sealed class DatabaseRunbookStore(IDatabaseProvider db) : IRunbookStore
{
	/// <summary>The most records kept per runbook; older ones go.</summary>
	public const int RecordsKept = 100;

	private readonly SemaphoreSlim gate = new(1, 1);

	public async Task<IReadOnlyList<Runbook>> LoadAsync()
	{
		await this.gate.WaitAsync();
		try
		{
			Dictionary<Guid, Runbook> found = [];
			foreach (StoredRunbook row in await Stored.All<StoredRunbook>(db))
			{
				if (Stored.FromJson<Runbook>(row.Json) is Runbook r)
					found.TryAdd(r.Id, r with { ReadyMade = false });
			}

			return [.. found.Values.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)];
		}
		finally
		{
			this.gate.Release();
		}
	}

	public async Task SaveAsync(Runbook runbook)
	{
		await this.gate.WaitAsync();
		try
		{
			List<StoredRunbook> rows = await this.RowsAsync(runbook.Id);
			string json = Stored.ToJson(runbook);
			if (rows.Count == 0)
				await db.Insert(new StoredRunbook { RunbookId = Stored.Id(runbook.Id), Json = json });
			else
			{
				if (rows[0].Json != json)
				{
					rows[0].Json = json;
					await db.Update(rows[0]);
				}
				foreach (StoredRunbook extra in rows.Skip(1))
					await db.Delete(extra);
			}
		}
		finally
		{
			this.gate.Release();
		}
	}

	public async Task DeleteAsync(Guid runbookId)
	{
		await this.gate.WaitAsync();
		try
		{
			foreach (StoredRunbook row in await this.RowsAsync(runbookId))
				await db.Delete(row);
		}
		finally
		{
			this.gate.Release();
		}
	}

	public async Task AddRecordAsync(RunbookRecord record)
	{
		await this.gate.WaitAsync();
		try
		{
			await db.Insert(new StoredRunbookRun { RunbookId = Stored.Id(record.RunbookId), Started = record.Started, Json = Stored.ToJson(record) });

			List<StoredRunbookRun> all = await this.RecordRowsAsync(record.RunbookId);
			foreach (StoredRunbookRun old in all.OrderByDescending(r => r.Started).Skip(RecordsKept))
				await db.Delete(old);
		}
		finally
		{
			this.gate.Release();
		}
	}

	public async Task<IReadOnlyList<RunbookRecord>> RecordsAsync(Guid runbookId)
	{
		await this.gate.WaitAsync();
		try
		{
			return [.. (await this.RecordRowsAsync(runbookId))
				.Select(r => Stored.FromJson<RunbookRecord>(r.Json))
				.OfType<RunbookRecord>()
				.OrderByDescending(r => r.Started)];
		}
		finally
		{
			this.gate.Release();
		}
	}

	private Task<List<StoredRunbook>> RowsAsync(Guid runbookId)
		=> Stored.All<StoredRunbook>(db, new FilterFieldEqualTo("RunbookId", Stored.Id(runbookId)));

	private Task<List<StoredRunbookRun>> RecordRowsAsync(Guid runbookId)
		=> Stored.All<StoredRunbookRun>(db, new FilterFieldEqualTo("RunbookId", Stored.Id(runbookId)));
}
