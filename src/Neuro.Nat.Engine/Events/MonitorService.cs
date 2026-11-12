using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Storage;

namespace Neuro.Nat.Engine.Events;

/// <summary>
/// Runs one <see cref="NeuronMonitor"/> per account and feeds every Neuron's events into one
/// <see cref="EventHub"/>.
/// </summary>
public sealed class MonitorService : IAsyncDisposable
{
	private readonly AccountManager accounts;
	private readonly Dictionary<Guid, NeuronMonitor> monitors = [];
	private readonly object sync = new();
	private readonly IKnownNeuronStore known;
	private IReadOnlyList<AccountSession> sessions = [];

	/// <param name="known">Where answers to "are you a Neuron?" are kept, so they survive a restart. In memory when not given.</param>
	public MonitorService(AccountManager accounts, EventHub hub, AttentionInbox? attention = null, IKnownNeuronStore? known = null)
	{
		this.accounts = accounts;
		this.Hub = hub;
		this.Attention = attention ?? new AttentionInbox();
		this.known = known ?? new InMemoryKnownNeuronStore();
		this.IdentityReviews = new IdentityReviews(this.Attention, hub, this.CanReadEvents);
		accounts.SessionsChanged += this.Accounts_SessionsChanged;
		this.Sync();
	}

	public EventHub Hub { get; }

	/// <summary>Notifications from every Neuron, for the Attention list.</summary>
	public AttentionInbox Attention { get; }

	/// <summary>Decides which legal identity applications need a person, from the Neuron's automatic review.</summary>
	public IdentityReviews IdentityReviews { get; }

	/// <summary>Raised when any Neuron appears, disappears or changes state. Any thread.</summary>
	public event EventHandler? NeuronsChanged;

	/// <summary>Raised when someone who is not a Neuron asks to subscribe to an account.</summary>
	public event EventHandler<(Guid AccountId, Waher.Networking.XMPP.Events.PresenceEventArgs Presence)>? SubscriptionRequested;

	/// <summary>Every Neuron known through any account.</summary>
	public IReadOnlyList<WatchedNeuron> Neurons
	{
		get
		{
			lock (this.sync)
				return [.. this.monitors.Values.SelectMany(m => m.Neurons)];
		}
	}

	/// <summary>All monitors, one per account.</summary>
	/// <summary>A kiosk's rule for presence subscriptions, given to every account's monitor. See <see cref="NeuronMonitor.SubscriptionRule"/>.</summary>
	public Func<string, bool?>? SubscriptionRule
	{
		get => this.subscriptionRule;
		set
		{
			this.subscriptionRule = value;
			foreach (NeuronMonitor m in this.Monitors)
				m.SubscriptionRule = value;
		}
	}

	private Func<string, bool?>? subscriptionRule;

	/// <summary>Decline what is not accepted instead of asking a person. See <see cref="NeuronMonitor.DeclineOthers"/>.</summary>
	public bool DeclineOthers
	{
		get => this.declineOthers;
		set
		{
			this.declineOthers = value;
			foreach (NeuronMonitor m in this.Monitors)
				m.DeclineOthers = value;
		}
	}

	private bool declineOthers;

	public IReadOnlyList<NeuronMonitor> Monitors
	{
		get
		{
			lock (this.sync)
				return [.. this.monitors.Values];
		}
	}

	/// <summary>The monitor of one account, if any.</summary>
	public NeuronMonitor? For(Guid accountId)
	{
		lock (this.sync)
			return this.monitors.TryGetValue(accountId, out NeuronMonitor? m) ? m : null;
	}

	/// <summary>Stands in for the watch states in tests: whether a Neuron's events can be read.</summary>
	internal Func<Guid, string, bool>? ReadsEventsForTests { get; set; }

	/// <summary>True when some account streams this Neuron's events, so its review events can arrive.</summary>
	/// <summary>
	/// False only when no account can read the Neuron's events: none streams them, and none is on the way to.
	/// A stream that is still being set up (as at start) counts as readable, so its review gets the time to arrive.
	/// </summary>
	private bool CanReadEvents(Guid accountId, string neuronJid)
		=> this.ReadsEventsForTests?.Invoke(accountId, neuronJid) ?? this.Neurons.Any(n =>
			n.State is WatchState.Live or WatchState.Subscribing or WatchState.Discovering or WatchState.Waiting &&
			string.Equals(n.BareJid, neuronJid, StringComparison.OrdinalIgnoreCase));

	private void Accounts_SessionsChanged(object? sender, EventArgs e) => this.Sync();

	private void Sync()
	{
		List<NeuronMonitor> removed = [];

		lock (this.sync)
		{
			IReadOnlyList<AccountSession> sessions = this.accounts.Sessions;
			this.sessions = sessions;

			foreach (AccountSession s in sessions)
			{
				if (this.monitors.ContainsKey(s.Profile.Id))
					continue;

				NeuronMonitor m = new(s, this.Hub, this.known, this.IsOwnAddress) { SubscriptionRule = this.SubscriptionRule, DeclineOthers = this.DeclineOthers };
				m.NeuronsChanged += (_, _) => this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
				m.SubscriptionRequested += (_, p) => this.SubscriptionRequested?.Invoke(this, (s.Profile.Id, p));
				m.NotificationReceived += (_, n) => this.Attention.Add(n);
				this.monitors[s.Profile.Id] = m;
			}

			foreach (Guid id in this.monitors.Keys.Where(id => !sessions.Any(s => s.Profile.Id == id)).ToList())
			{
				removed.Add(this.monitors[id]);
				this.monitors.Remove(id);
			}
		}

		foreach (NeuronMonitor m in removed)
			_ = m.DisposeAsync().AsTask();

		this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>True when a bare JID is one of the user's own accounts. Reads a copy, so it takes no lock.</summary>
	private bool IsOwnAddress(string bareJid)
	{
		foreach (AccountSession s in this.sessions)
		{
			if (string.Equals(s.BareJid, bareJid, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(s.Profile.UserName + "@" + s.Profile.EffectiveDomain, bareJid, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	public async ValueTask DisposeAsync()
	{
		this.accounts.SessionsChanged -= this.Accounts_SessionsChanged;
		this.IdentityReviews.Dispose();

		List<NeuronMonitor> all;
		lock (this.sync)
		{
			all = [.. this.monitors.Values];
			this.monitors.Clear();
		}

		foreach (NeuronMonitor m in all)
			await m.DisposeAsync();
	}
}
