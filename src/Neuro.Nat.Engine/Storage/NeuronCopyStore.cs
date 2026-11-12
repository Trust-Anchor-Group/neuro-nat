using System.Collections.Concurrent;
using Neuro.Nat.Engine.Compare;
using Waher.Persistence;
using Waher.Persistence.Attributes;
using Waher.Persistence.Filters;

namespace Neuro.Nat.Engine.Storage;

/// <summary>
/// A kept copy of what was read from a Neuron for a comparison. Secrets are in it only as hashes.
/// Stored the way <see cref="StoredAccount"/> and the others are: a fixed collection name, no
/// type name, the snapshot as JSON, and the fields it is looked up by.
/// </summary>
[CollectionName("NeuronCopies")]
[TypeName(TypeNameSerialization.None)]
[Index("Domain", "Taken")]
public sealed class StoredNeuronCopy : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	/// <summary>The copy's id in NAT (32 hex digits), as a link names it.</summary>
	public string CopyId { get; set; } = string.Empty;

	/// <summary>The Neuron's domain, in lower case.</summary>
	public string Domain { get; set; } = string.Empty;

	/// <summary>When it was read (UTC).</summary>
	public DateTime Taken { get; set; }

	public string Json { get; set; } = string.Empty;
}

/// <summary>A kept copy, as listed: which Neuron and when.</summary>
public sealed record KeptCopy(string CopyId, string NeuronJid, DateTime Taken);

/// <summary>Copies of what comparisons read, kept so a Neuron can be compared with how it was.</summary>
public interface INeuronCopyStore
{
	/// <summary>Keeps a copy and returns it with its id. The oldest go when a Neuron has more than <see cref="NeuronCopies.MaxPerNeuron"/>.</summary>
	Task<NeuronSnapshot> KeepAsync(NeuronSnapshot snapshot);

	/// <summary>The copies kept of a Neuron, newest first.</summary>
	Task<IReadOnlyList<KeptCopy>> ListAsync(string neuronJid);

	/// <summary>A kept copy by its id, or null.</summary>
	Task<NeuronSnapshot?> GetAsync(string copyId);
}

/// <summary>Shared by the copy stores.</summary>
public static class NeuronCopies
{
	/// <summary>How many copies are kept of one Neuron.</summary>
	public const int MaxPerNeuron = 50;

	public static string NewId() => Guid.NewGuid().ToString("N");

	/// <summary>True for an id NAT gives a copy: 32 hex digits.</summary>
	public static bool IsId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);

	/// <summary>The snapshot as it may be kept: no value of a secret, only its hash.</summary>
	internal static NeuronSnapshot Scrubbed(NeuronSnapshot s, string copyId)
		=> s with
		{
			CopyId = copyId,
			Values = [.. s.Values.Select(v => v.IsSecret && v.Value is not null ? v with { Value = null } : v)],
		};
}

/// <summary>Copies kept only while NAT runs: for tests and for systems without a database.</summary>
public sealed class MemoryNeuronCopyStore : INeuronCopyStore
{
	private readonly ConcurrentDictionary<string, NeuronSnapshot> copies = new(StringComparer.OrdinalIgnoreCase);

	public Task<NeuronSnapshot> KeepAsync(NeuronSnapshot snapshot)
	{
		NeuronSnapshot kept = NeuronCopies.Scrubbed(snapshot, NeuronCopies.NewId());
		this.copies[kept.CopyId!] = kept;

		string domain = Links.NatLink.DomainOf(snapshot.NeuronJid);
		foreach (NeuronSnapshot old in this.copies.Values.Where(c => Links.NatLink.DomainOf(c.NeuronJid) == domain).OrderByDescending(c => c.ReadAt).Skip(NeuronCopies.MaxPerNeuron))
			this.copies.TryRemove(old.CopyId!, out _);

		return Task.FromResult(kept);
	}

	public Task<IReadOnlyList<KeptCopy>> ListAsync(string neuronJid)
	{
		string domain = Links.NatLink.DomainOf(neuronJid);
		return Task.FromResult<IReadOnlyList<KeptCopy>>([.. this.copies.Values
			.Where(c => Links.NatLink.DomainOf(c.NeuronJid) == domain)
			.OrderByDescending(c => c.ReadAt)
			.Select(c => new KeptCopy(c.CopyId!, c.NeuronJid, c.ReadAt))]);
	}

	public Task<NeuronSnapshot?> GetAsync(string copyId)
		=> Task.FromResult(this.copies.TryGetValue(copyId, out NeuronSnapshot? s) ? s : null);
}

/// <summary>Copies kept in the encrypted database, one object per copy.</summary>
public sealed class DatabaseNeuronCopyStore(IDatabaseProvider db) : INeuronCopyStore
{
	private readonly SemaphoreSlim gate = new(1, 1);

	private sealed record Payload(NeuronSnapshot Snapshot);

	public async Task<NeuronSnapshot> KeepAsync(NeuronSnapshot snapshot)
	{
		NeuronSnapshot kept = NeuronCopies.Scrubbed(snapshot, NeuronCopies.NewId());
		string domain = Links.NatLink.DomainOf(snapshot.NeuronJid);
		await this.gate.WaitAsync();
		try
		{
			await db.Insert(new StoredNeuronCopy { CopyId = kept.CopyId!, Domain = domain, Taken = kept.ReadAt, Json = Stored.ToJson(new Payload(kept)) });

			List<StoredNeuronCopy> rows = await this.RowsAsync(domain);
			foreach (StoredNeuronCopy old in rows.OrderByDescending(r => r.Taken).Skip(NeuronCopies.MaxPerNeuron))
				await db.Delete(old);
		}
		finally
		{
			this.gate.Release();
		}

		return kept;
	}

	public async Task<IReadOnlyList<KeptCopy>> ListAsync(string neuronJid)
	{
		await this.gate.WaitAsync();
		try
		{
			List<KeptCopy> list = [];
			foreach (StoredNeuronCopy row in (await this.RowsAsync(Links.NatLink.DomainOf(neuronJid))).OrderByDescending(r => r.Taken))
			{
				if (Read(row) is NeuronSnapshot s)
					list.Add(new KeptCopy(row.CopyId, s.NeuronJid, row.Taken));
			}

			return list;
		}
		finally
		{
			this.gate.Release();
		}
	}

	public async Task<NeuronSnapshot?> GetAsync(string copyId)
	{
		if (!NeuronCopies.IsId(copyId))
			return null;

		await this.gate.WaitAsync();
		try
		{
			// Few copies per Neuron, and few Neurons: looked up among all of them rather than by an index of its own.
			StoredNeuronCopy? row = (await Stored.All<StoredNeuronCopy>(db)).FirstOrDefault(r => string.Equals(r.CopyId, copyId, StringComparison.OrdinalIgnoreCase));
			return row is null ? null : Read(row);
		}
		finally
		{
			this.gate.Release();
		}
	}

	private static NeuronSnapshot? Read(StoredNeuronCopy row)
	{
		try
		{
			return Stored.FromJson<Payload>(row.Json)?.Snapshot;
		}
		catch (System.Text.Json.JsonException)
		{
			return null;
		}
	}

	private Task<List<StoredNeuronCopy>> RowsAsync(string domain)
		=> Stored.All<StoredNeuronCopy>(db, new FilterFieldEqualTo("Domain", domain));
}
