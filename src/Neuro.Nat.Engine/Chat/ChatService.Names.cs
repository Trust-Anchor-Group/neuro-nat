using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Topology;

namespace Neuro.Nat.Engine.Chat;

/// <summary>
/// Asks each Neuron what it calls itself. A Neuron is watched through its own XMPP client, and
/// that client is often an account on the Neuron's parent broker, so the domain of its address
/// can be another Neuron's. The Neuron's chat admin knows better: ParentGraph names the Neuron
/// by its own domain, as the topology map shows it.
/// </summary>
public sealed partial class ChatService
{
	private static readonly TimeSpan ownNameTimeout = TimeSpan.FromSeconds(20);

	/// <summary>The stream (by when it went live) each Neuron was last asked on: once per stream, so a Neuron that did not answer is asked again when it comes back.</summary>
	private readonly Dictionary<string, DateTime> ownNameAsked = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Stands in for the Neuron's answer to ParentGraph in tests, so no live Neuron is needed.</summary>
	internal Func<WatchedNeuron, Task<string?>>? AskParentGraph { get; set; }

	private void Monitors_NeuronsChanged(object? sender, EventArgs e)
	{
		foreach (WatchedNeuron n in this.monitors.Neurons)
		{
			// Only a Neuron whose events stream answers its chat admin: the same right gives both.
			if (n.State != WatchState.Live || n.LiveSince is not DateTime since)
				continue;

			string key = Key(n.AccountId, n.BareJid);
			lock (this.ownNameAsked)
			{
				if (this.ownNameAsked.TryGetValue(key, out DateTime asked) && asked == since)
					continue;

				this.ownNameAsked[key] = since;
			}

			_ = this.AskOwnNameAsync(n);
		}
	}

	private async Task AskOwnNameAsync(WatchedNeuron n)
	{
		try
		{
			string? answer = this.AskParentGraph is Func<WatchedNeuron, Task<string?>> ask
				? await ask(n)
				: await this.Console(n.AccountId, n.BareJid).AskAsync("ParentGraph", ownNameTimeout);

			if (TopologyService.OwnName(answer) is string name)
				this.monitors.For(n.AccountId)?.SetOwnName(n.BareJid, name);
		}
		catch (Exception)
		{
			// No name this time: the Neuron keeps the one it had, and is asked again when it comes back.
		}
	}
}
