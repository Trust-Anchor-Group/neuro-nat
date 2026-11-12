namespace Neuro.Nat.Engine.Events;

/// <summary>
/// Event severity as NAT shows it. Live events arrive through a sniffer, which only
/// distinguishes four classes; history from the Neuron's event log has the full Waher type.
/// </summary>
public enum EventSeverity
{
	/// <summary>Debug, Informational or Notice.</summary>
	Info = 0,

	/// <summary>Warning.</summary>
	Warning = 1,

	/// <summary>Error.</summary>
	Error = 2,

	/// <summary>Critical, Alert or Emergency.</summary>
	Critical = 3,
}

/// <summary>Where an event came from.</summary>
/// <param name="AccountId">The NAT account that received it.</param>
/// <param name="NeuronJid">Bare JID of the Neuron that logged it.</param>
public sealed record EventSource(Guid AccountId, string NeuronJid);

/// <summary>One event logged on a Neuron.</summary>
public sealed record NeuronEvent
{
	/// <summary>Local sequence number, unique within this app session.</summary>
	public long Sequence { get; init; }

	public required EventSource Source { get; init; }

	/// <summary>When the Neuron logged it (local time of this machine).</summary>
	public DateTime Timestamp { get; init; }

	public EventSeverity Severity { get; init; }

	/// <summary>Exact Waher event type when known (history), otherwise null.</summary>
	public string? Type { get; init; }

	/// <summary>Exact Waher event level (Minor, Medium, Major) when known.</summary>
	public string? Level { get; init; }

	public string Message { get; init; } = string.Empty;
	public string? Object { get; init; }
	public string? Actor { get; init; }
	public string? EventId { get; init; }
	public string? Facility { get; init; }
	public string? Module { get; init; }
	public string? StackTrace { get; init; }

	/// <summary>Other tags, in the order the Neuron sent them.</summary>
	public IReadOnlyList<KeyValuePair<string, string>> Tags { get; init; } = [];

	/// <summary>True when read back from the Neuron's event log rather than streamed live.</summary>
	public bool FromHistory { get; init; }

	/// <summary>The grouping key; events with the same fingerprint form one issue.</summary>
	public string Fingerprint { get; init; } = string.Empty;

	/// <summary>First line of the message, for lists.</summary>
	public string Title
	{
		get
		{
			int i = this.Message.IndexOfAny(['\r', '\n']);
			return i < 0 ? this.Message : this.Message[..i];
		}
	}
}
