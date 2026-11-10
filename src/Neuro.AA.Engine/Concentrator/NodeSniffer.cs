using Neuro.AA.Engine.Sniffing;
using Waher.Things;

namespace Neuro.AA.Engine.Concentrator;

public enum NodeSnifferState
{
	Starting,
	Live,
	Stopped,
	Failed,
}

/// <summary>
/// Live traffic of one node on a Neuron (for example its HTTP server or XMPP connections),
/// kept running until stopped: the registration is renewed before it expires and made again
/// when the account reconnects.
/// </summary>
public sealed class NodeSniffer : IAsyncDisposable
{
	private static readonly TimeSpan duration = TimeSpan.FromHours(1);
	private static readonly TimeSpan renewBefore = TimeSpan.FromMinutes(5);

	private readonly Func<NeuronConcentrator?> concentrator;
	private readonly Timer timer;
	private readonly SemaphoreSlim gate = new(1, 1);
	private NeuronConcentrator? registeredWith;
	private string? snifferId;
	private DateTime expires;

	/// <param name="concentrator">Gives the Neuron's concentrator as it is now; it changes when the account reconnects.</param>
	public NodeSniffer(Func<NeuronConcentrator?> concentrator, IThingReference node, string title, int capacity = 5000)
	{
		this.concentrator = concentrator;
		this.Node = new ThingReference(node.NodeId, node.SourceId, node.Partition);
		this.Title = title;
		this.Sniffer = new StreamSniffer(title, capacity);
		this.timer = new Timer(_ => _ = this.KeepAliveAsync(), null, Timeout.Infinite, Timeout.Infinite);
	}

	public ThingReference Node { get; }
	public string Title { get; }
	public StreamSniffer Sniffer { get; }
	public NodeSnifferState State { get; private set; } = NodeSnifferState.Starting;
	public string? Error { get; private set; }

	/// <summary>Raised when <see cref="State"/> or <see cref="Error"/> changes. Any thread.</summary>
	public event EventHandler? StateChanged;

	public async Task StartAsync()
	{
		await this.RegisterAsync();
		this.timer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
	}

	private async Task KeepAliveAsync()
	{
		if (this.State == NodeSnifferState.Stopped)
			return;

		NeuronConcentrator? now = this.concentrator();
		bool reconnected = now is not null && (this.registeredWith is null ||
			!ReferenceEquals(now.Client, this.registeredWith.Client) || now.FullJid != this.registeredWith.FullJid);

		if (reconnected || DateTime.Now > this.expires - renewBefore)
			await this.RegisterAsync();
	}

	private async Task RegisterAsync()
	{
		if (!await this.gate.WaitAsync(0))
			return;

		try
		{
			if (this.State == NodeSnifferState.Stopped)
				return;

			NeuronConcentrator? c = this.concentrator();
			if (c is null)
			{
				this.Set(NodeSnifferState.Failed, "The Neuron is not online through its account. The sniffer starts again when it is.");
				return;
			}

			NeuronConcentrator? previous = this.registeredWith;
			string? previousId = this.snifferId;

			(string id, DateTime exp) = await c.RegisterSnifferAsync(this.Node, this.Sniffer, DateTime.Now + duration);
			this.registeredWith = c;
			this.snifferId = id;
			this.expires = exp;

			if (previous is not null && previousId is not null && ReferenceEquals(previous.Client, c.Client))
				_ = previous.UnregisterSnifferAsync(this.Node, previousId);

			if (this.State != NodeSnifferState.Live)
			{
				this.Sniffer.Information("Sniffing " + this.Title + ".");
				this.Set(NodeSnifferState.Live, null);
			}
		}
		catch (Exception ex)
		{
			this.Set(NodeSnifferState.Failed, ex.Message);
		}
		finally
		{
			this.gate.Release();
		}
	}

	private void Set(NodeSnifferState state, string? error)
	{
		bool changed = this.State != state || this.Error != error;
		this.State = state;
		this.Error = error;
		if (error is not null && changed)
			this.Sniffer.Warning(error);
		if (changed)
			this.StateChanged?.Invoke(this, EventArgs.Empty);
	}

	public async Task StopAsync()
	{
		if (this.State == NodeSnifferState.Stopped)
			return;

		this.timer.Change(Timeout.Infinite, Timeout.Infinite);
		NeuronConcentrator? c = this.registeredWith;
		string? id = this.snifferId;
		this.Set(NodeSnifferState.Stopped, null);

		if (c is not null && id is not null)
			await c.UnregisterSnifferAsync(this.Node, id);
	}

	public async ValueTask DisposeAsync()
	{
		await this.StopAsync();
		await this.timer.DisposeAsync();
	}
}
