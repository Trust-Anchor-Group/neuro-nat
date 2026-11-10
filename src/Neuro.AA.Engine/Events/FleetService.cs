using System.Collections.Concurrent;

namespace Neuro.AA.Engine.Events;

/// <summary>
/// Keeps the health of every live Neuron up to date: reads it when a Neuron's stream goes
/// live, then every <see cref="Interval"/>.
/// </summary>
public sealed class FleetService : IAsyncDisposable
{
	private readonly MonitorService monitors;
	private readonly ConcurrentDictionary<string, NeuronHealth> health = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, bool> reading = new(StringComparer.OrdinalIgnoreCase);
	private readonly Timer timer;

	public FleetService(MonitorService monitors)
	{
		this.monitors = monitors;
		monitors.NeuronsChanged += this.Monitors_NeuronsChanged;
		this.timer = new Timer(_ => _ = this.PollAllAsync(), null, this.Interval, this.Interval);
	}

	/// <summary>How often health is read. Performance samples are taken once a minute.</summary>
	public TimeSpan Interval { get; } = TimeSpan.FromMinutes(1);

	/// <summary>Raised when the health of a Neuron has been read. Any thread.</summary>
	public event EventHandler<NeuronHealth>? HealthChanged;

	public MonitorService Monitors => this.monitors;

	/// <summary>Latest health of a Neuron, if read.</summary>
	public NeuronHealth? Get(Guid accountId, string neuronJid)
		=> this.health.TryGetValue(Key(accountId, neuronJid), out NeuronHealth? h) ? h : null;

	/// <summary>Sets health without network traffic, for previews and UI tests.</summary>
	internal void Preview(NeuronHealth h)
	{
		this.health[Key(h.AccountId, h.NeuronJid)] = h;
		this.HealthChanged?.Invoke(this, h);
	}

	/// <summary>Reads every live Neuron now.</summary>
	public Task RefreshAsync() => this.PollAllAsync();

	private static string Key(Guid accountId, string jid) => accountId.ToString("N") + "|" + jid;

	private void Monitors_NeuronsChanged(object? sender, EventArgs e)
	{
		foreach (NeuronMonitor m in this.monitors.Monitors)
		{
			foreach (WatchedNeuron n in m.Neurons)
			{
				if (n.State == WatchState.Live && !this.health.ContainsKey(Key(n.AccountId, n.BareJid)))
					_ = this.ReadAsync(m, n);
			}
		}
	}

	private async Task PollAllAsync()
	{
		List<Task> tasks = [];
		foreach (NeuronMonitor m in this.monitors.Monitors)
		{
			foreach (WatchedNeuron n in m.Neurons)
			{
				if (n.State == WatchState.Live)
					tasks.Add(this.ReadAsync(m, n));
			}
		}

		await Task.WhenAll(tasks);
	}

	private async Task ReadAsync(NeuronMonitor m, WatchedNeuron n)
	{
		string key = Key(n.AccountId, n.BareJid);
		if (!this.reading.TryAdd(key, true))
			return;

		try
		{
			NeuronHealth? h = await m.ReadHealthAsync(n.BareJid);
			if (h is not null)
			{
				this.health[key] = h;
				this.HealthChanged?.Invoke(this, h);
			}
		}
		catch (Exception ex)
		{
			m.Session.Sniffer.Exception(ex);
		}
		finally
		{
			this.reading.TryRemove(key, out _);
		}
	}

	public ValueTask DisposeAsync()
	{
		this.monitors.NeuronsChanged -= this.Monitors_NeuronsChanged;
		this.timer.Dispose();
		return ValueTask.CompletedTask;
	}
}
