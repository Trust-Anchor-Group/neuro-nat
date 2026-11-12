namespace Neuro.Nat.Engine.Events;

/// <summary>Where a watched Neuron's live event stream stands.</summary>
public enum WatchState
{
	/// <summary>In the roster, but no presence seen yet.</summary>
	Waiting,

	/// <summary>Checking what the contact is.</summary>
	Discovering,

	/// <summary>The contact is not a Neuron (no concentrator).</summary>
	NotANeuron,

	/// <summary>Registering the event sniffer.</summary>
	Subscribing,

	/// <summary>Events are streaming.</summary>
	Live,

	/// <summary>The Neuron refused: this account is not an administrator there.</summary>
	Forbidden,

	/// <summary>The Neuron is offline.</summary>
	Offline,

	/// <summary>Something else went wrong; see the error.</summary>
	Failed,
}

/// <summary>Whether past events have been read from the Neuron's event log.</summary>
public enum HistoryState
{
	NotLoaded,
	Loading,
	Loaded,
	Failed,
}

/// <summary>A Neuron reachable from one account, and the state of its event stream.</summary>
public sealed record WatchedNeuron
{
	public required Guid AccountId { get; init; }

	/// <summary>Bare JID of the Neuron's own XMPP client.</summary>
	public required string BareJid { get; init; }

	/// <summary>Full JID while online.</summary>
	public string? FullJid { get; init; }

	/// <summary>Roster name, when the operator gave one.</summary>
	public string? Name { get; init; }

	public WatchState State { get; init; }

	public string? Error { get; init; }

	/// <summary>When the stream went live (local time).</summary>
	public DateTime? LiveSince { get; init; }

	public HistoryState HistoryState { get; init; }

	/// <summary>Number of past events read in the last backfill.</summary>
	public int HistoryCount { get; init; }

	/// <summary>Start of the period the last backfill covered.</summary>
	public DateTime? HistoryFrom { get; init; }

	public string? HistoryError { get; init; }

	/// <summary>One line for chips and tooltips, for example "Listening, 239 earlier events loaded".</summary>
	public string StatusText
	{
		get
		{
			string state = this.State switch
			{
				WatchState.Live => "Listening",
				WatchState.Waiting => "Waiting for it to come online",
				WatchState.Discovering => "Checking",
				WatchState.Subscribing => "Starting to listen",
				WatchState.Forbidden => "No access",
				WatchState.Offline => "Offline",
				WatchState.Failed => "Could not listen",
				_ => string.Empty,
			};

			string history = this.HistoryState switch
			{
				HistoryState.Loading => this.HistoryCount > 0 ? $", reading earlier events ({this.HistoryCount})" : ", reading earlier events",
				HistoryState.Loaded => this.HistoryCount == 1 ? ", 1 earlier event loaded" : $", {this.HistoryCount} earlier events loaded",
				HistoryState.Failed => ", earlier events incomplete",
				_ => string.Empty,
			};

			return this.State == WatchState.Live ? state + history : state;
		}
	}

	/// <summary>Details for the tooltip: errors, or what the history covers.</summary>
	public string? Details => this.Error ?? this.HistoryError ?? (this.HistoryFrom is DateTime f ? $"History read from {f:yyyy-MM-dd HH:mm}. Warnings and worse only." : null);

	/// <summary>The name on screen: the roster name, else the domain. See <see cref="NeuronNames"/>.</summary>
	public string Label => NeuronNames.Of(this.Name, this.BareJid);
}
