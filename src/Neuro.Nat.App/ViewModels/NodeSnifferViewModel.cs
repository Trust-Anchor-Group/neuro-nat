using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.Nat.Engine.Concentrator;

namespace Neuro.Nat.App.ViewModels;

/// <summary>A running sniffer on a node, shown as a tab in the Sources workspace.</summary>
public sealed partial class NodeSnifferViewModel : ObservableObject, IAsyncDisposable
{
	public NodeSnifferViewModel(NodeSniffer sniffer, Guid accountId, string neuronJid, string nodeLabel, string neuronLabel)
	{
		this.Sniffer = sniffer;
		this.AccountId = accountId;
		this.NeuronJid = neuronJid;
		this.NodeLabel = nodeLabel;
		this.NeuronLabel = neuronLabel;
		this.Lines = new SnifferViewModel(sniffer.Sniffer, nodeLabel + " on " + neuronLabel)
		{
			EmptyText = "Everything the Neuron sends to and receives from this node shows here, live.",
		};
		sniffer.StateChanged += (_, _) => Dispatcher.UIThread.Post(this.Refresh);
		this.Refresh();
	}

	public NodeSniffer Sniffer { get; }
	public Guid AccountId { get; }
	public string NeuronJid { get; }
	public string NodeLabel { get; }
	public string NeuronLabel { get; }
	public SnifferViewModel Lines { get; }

	[ObservableProperty] private bool isLive;
	[ObservableProperty] private bool hasProblem;
	[ObservableProperty] private string stateText = string.Empty;

	public bool Matches(Guid accountId, string neuronJid, Waher.Things.IThingReference node)
		=> this.AccountId == accountId && string.Equals(this.NeuronJid, neuronJid, StringComparison.OrdinalIgnoreCase) &&
			this.Sniffer.Node.NodeId == node.NodeId && this.Sniffer.Node.SourceId == node.SourceId && this.Sniffer.Node.Partition == node.Partition;

	private void Refresh()
	{
		this.IsLive = this.Sniffer.State == NodeSnifferState.Live;
		this.HasProblem = this.Sniffer.State == NodeSnifferState.Failed;
		this.StateText = this.Sniffer.State switch
		{
			NodeSnifferState.Starting => "Starting…",
			NodeSnifferState.Live => "Listening on " + this.NeuronLabel,
			NodeSnifferState.Stopped => "Stopped",
			_ => this.Sniffer.Error ?? "Failed",
		};
	}

	public async ValueTask DisposeAsync()
	{
		this.Lines.Dispose();
		await this.Sniffer.DisposeAsync();
	}
}
