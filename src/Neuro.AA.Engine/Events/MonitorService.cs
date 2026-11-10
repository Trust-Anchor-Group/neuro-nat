using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Attention;

namespace Neuro.AA.Engine.Events;

/// <summary>
/// Runs one <see cref="NeuronMonitor"/> per account and feeds every Neuron's events into one
/// <see cref="EventHub"/>.
/// </summary>
public sealed class MonitorService : IAsyncDisposable
{
	private readonly AccountManager accounts;
	private readonly Dictionary<Guid, NeuronMonitor> monitors = [];
	private readonly object sync = new();

	public MonitorService(AccountManager accounts, EventHub hub, AttentionInbox? attention = null)
	{
		this.accounts = accounts;
		this.Hub = hub;
		this.Attention = attention ?? new AttentionInbox();
		accounts.SessionsChanged += this.Accounts_SessionsChanged;
		this.Sync();
	}

	public EventHub Hub { get; }

	/// <summary>Notifications from every Neuron, for the Attention list.</summary>
	public AttentionInbox Attention { get; }

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

	private void Accounts_SessionsChanged(object? sender, EventArgs e) => this.Sync();

	private void Sync()
	{
		List<NeuronMonitor> removed = [];

		lock (this.sync)
		{
			IReadOnlyList<AccountSession> sessions = this.accounts.Sessions;

			foreach (AccountSession s in sessions)
			{
				if (this.monitors.ContainsKey(s.Profile.Id))
					continue;

				NeuronMonitor m = new(s, this.Hub);
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

	public async ValueTask DisposeAsync()
	{
		this.accounts.SessionsChanged -= this.Accounts_SessionsChanged;

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
