using Waher.Persistence.Attributes;

namespace Neuro.Nat.Engine.Storage;

// Runbooks and what their runs left behind, as kept in the encrypted database. Same rules as
// StoredObjects.cs: a fixed collection name, no type name, the payload as JSON in one field.

/// <summary>A runbook someone made or kept, with the blanks it remembers.</summary>
[CollectionName("Runbooks")]
[TypeName(TypeNameSerialization.None)]
[Index("RunbookId")]
public sealed class StoredRunbook : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	public string RunbookId { get; set; } = string.Empty;

	public string Json { get; set; } = string.Empty;
}

/// <summary>The record of one run: account, time, Neurons, and the answer to each step.</summary>
[CollectionName("RunbookRuns")]
[TypeName(TypeNameSerialization.None)]
[Index("RunbookId", "Started")]
public sealed class StoredRunbookRun : IStoredObject
{
	[ObjectId]
	public string? ObjectId { get; set; }

	public string RunbookId { get; set; } = string.Empty;

	public DateTime Started { get; set; }

	public string Json { get; set; } = string.Empty;
}
