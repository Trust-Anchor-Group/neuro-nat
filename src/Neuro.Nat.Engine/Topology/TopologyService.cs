using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.Engine.Topology;

/// <summary>How two points in the network are connected.</summary>
public enum TopologyLink
{
	/// <summary>A Neuron and the broker its own XMPP client is connected to (its parent).</summary>
	Parent,

	/// <summary>A server-to-server connection between federated brokers.</summary>
	Federation,

	/// <summary>A contact in the Neuron's roster, in a group that authorizes information flow.</summary>
	Group,
}

/// <summary>What a point in the network is.</summary>
public enum TopologyNodeKind
{
	/// <summary>A Neuron that answered through one of your accounts.</summary>
	Neuron,

	/// <summary>A broker that a Neuron is connected to, which none of your accounts administers.</summary>
	Broker,

	/// <summary>A contact in a Neuron's roster: a device, a service or a person.</summary>
	Contact,
}

/// <summary>A point in the network.</summary>
public sealed record TopologyNode
{
	/// <summary>Lower-case name; edges refer to it.</summary>
	public required string Id { get; init; }

	public required string Label { get; init; }

	public TopologyNodeKind Kind { get; init; }

	/// <summary>The account that reached the Neuron, when it is one of yours.</summary>
	public Guid? AccountId { get; init; }

	/// <summary>The Neuron's address for chat admin and monitoring, when it is one of yours.</summary>
	public string? NeuronJid { get; init; }
}

/// <summary>A connection between two points. For <see cref="TopologyLink.Parent"/>, <see cref="From"/> is the child.</summary>
public sealed record TopologyEdge(string From, string To, TopologyLink Kind, string? Label);

/// <summary>A Neuron that could not tell its part of the topology.</summary>
public sealed record TopologyProblem(Guid AccountId, string NeuronJid, string Label, string Message);

/// <summary>The merged answers of every Neuron asked.</summary>
public sealed record TopologySnapshot(IReadOnlyList<TopologyNode> Nodes, IReadOnlyList<TopologyEdge> Edges, IReadOnlyList<TopologyProblem> Problems, DateTime Taken)
{
	public static readonly TopologySnapshot Empty = new([], [], [], DateTime.MinValue);
}

/// <summary>
/// Asks every Neuron you administer how it is connected, through its chat admin (ParentGraph,
/// S2SGraph and GroupGraph), and merges the answers into one network. Each Neuron only knows its
/// own connections; together they show the federation as far as your accounts can see.
/// </summary>
public sealed class TopologyService(MonitorService monitors, ChatService chat)
{
	private static readonly TimeSpan timeout = TimeSpan.FromSeconds(20);

	/// <summary>Asks every live Neuron, at the same time, and merges the answers.</summary>
	public async Task<TopologySnapshot> ScanAsync(bool includeGroups, CancellationToken cancel = default)
	{
		List<WatchedNeuron> live = [.. monitors.Neurons.Where(n => n.State == WatchState.Live)];
		NeuronAnswer[] answers = await Task.WhenAll(live.Select(n => this.AskAsync(n, includeGroups, cancel)));
		return Merge(answers, DateTime.Now);
	}

	/// <summary>One Neuron's part of the topology.</summary>
	internal sealed record NeuronAnswer(WatchedNeuron Neuron, string? Title, IReadOnlyList<DotEdge> Parent, IReadOnlyList<DotEdge> Federation,
		IReadOnlyList<DotEdge> Groups, string? Problem);

	private async Task<NeuronAnswer> AskAsync(WatchedNeuron n, bool includeGroups, CancellationToken cancel)
	{
		ConsoleSession console = chat.Console(n.AccountId, n.BareJid);
		try
		{
			string? parent = await console.AskAsync("ParentGraph", timeout).WaitAsync(cancel);
			if (parent is null)
				return new NeuronAnswer(n, null, [], [], [], "The Neuron did not answer.");
			if (!DotGraph.IsGraph(parent))
				return new NeuronAnswer(n, null, [], [], [], "The Neuron does not support topology graphs: " + Attention.NotificationClassifier.Plain(parent));

			IReadOnlyList<DotEdge> parentEdges = DotGraph.Edges(parent);
			string? title = OwnName(parent);

			string? s2s = await console.AskAsync("S2SGraph", timeout).WaitAsync(cancel);
			IReadOnlyList<DotEdge> federation = s2s is null ? [] : DotGraph.Edges(s2s);
			title ??= federation.FirstOrDefault()?.From;

			IReadOnlyList<DotEdge> groups = [];
			if (includeGroups && await console.AskAsync("GroupGraph", timeout).WaitAsync(cancel) is string g)
				groups = DotGraph.Edges(g);

			return new NeuronAnswer(n, title, parentEdges, federation, groups, s2s is null ? "The Neuron did not list its server-to-server connections." : null);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return new NeuronAnswer(n, null, [], [], [], ex.Message);
		}
	}

	/// <summary>
	/// What a Neuron calls itself, read from its answer to ParentGraph: its domain, or its account
	/// when it has none. Null when the answer is no graph, names nothing, or names something that
	/// cannot be a domain or an account (it is shown as a name, so it is not taken on trust).
	/// </summary>
	public static string? OwnName(string? parentGraph)
	{
		if (string.IsNullOrWhiteSpace(parentGraph) || !DotGraph.IsGraph(parentGraph))
			return null;

		// "parent" -> "self" [dir=back]: the first edge ends at the Neuron that answered.
		string? name = DotGraph.Edges(parentGraph).FirstOrDefault()?.To.Trim();
		return string.IsNullOrEmpty(name) || name.Length > 255 || name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? null : name;
	}

	/// <summary>Merges the Neurons' answers into one network.</summary>
	internal static TopologySnapshot Merge(IReadOnlyList<NeuronAnswer> answers, DateTime taken)
	{
		Dictionary<string, TopologyNode> nodes = new(StringComparer.Ordinal);
		HashSet<(string, string, TopologyLink, string?)> seen = [];
		List<TopologyEdge> edges = [];
		List<TopologyProblem> problems = [];

		static string Key(string s) => s.Trim().ToLowerInvariant();

		void Node(string name, TopologyNodeKind kind)
		{
			string id = Key(name);
			if (id.Length == 0)
				return;

			if (!nodes.TryGetValue(id, out TopologyNode? existing) || existing.Kind > kind)
				nodes[id] = existing is null ? new TopologyNode { Id = id, Label = name.Trim(), Kind = kind } : existing with { Kind = kind };
		}

		void Edge(string from, string to, TopologyLink kind, string? label)
		{
			string a = Key(from), b = Key(to);
			if (a.Length == 0 || b.Length == 0 || a == b)
				return;

			// A federation link seen from both ends is one link.
			if (kind == TopologyLink.Federation && seen.Contains((b, a, kind, label)))
				return;

			if (seen.Add((a, b, kind, label)))
				edges.Add(new TopologyEdge(a, b, kind, label));
		}

		// The Neurons that answered come first, so they keep their names and accounts.
		foreach (NeuronAnswer x in answers)
		{
			if (x.Title is null)
			{
				problems.Add(new TopologyProblem(x.Neuron.AccountId, x.Neuron.BareJid, x.Neuron.Label, x.Problem ?? "No answer."));
				continue;
			}

			string id = Key(x.Title);
			if (nodes.TryGetValue(id, out TopologyNode? n) && n.Kind == TopologyNodeKind.Neuron)
				continue;   // The same Neuron through another account.

			nodes[id] = new TopologyNode
			{
				Id = id,
				Label = string.IsNullOrWhiteSpace(x.Neuron.Name) ? x.Title : x.Neuron.Name!,
				Kind = TopologyNodeKind.Neuron,
				AccountId = x.Neuron.AccountId,
				NeuronJid = x.Neuron.BareJid,
			};

			if (x.Problem is not null)
				problems.Add(new TopologyProblem(x.Neuron.AccountId, x.Neuron.BareJid, x.Neuron.Label, x.Problem));
		}

		foreach (NeuronAnswer x in answers.Where(a => a.Title is not null))
		{
			string self = x.Title!;

			// "parent" -> "self" [dir=back]: the Neuron's client is connected to its parent.
			foreach (DotEdge e in x.Parent)
			{
				string child = e.Back ? e.To : e.From;
				string parent = e.Back ? e.From : e.To;
				Node(parent, TopologyNodeKind.Broker);
				Edge(child, parent, TopologyLink.Parent, null);
			}

			foreach (DotEdge e in x.Federation)
			{
				Node(e.To, TopologyNodeKind.Broker);
				Edge(self, e.To, TopologyLink.Federation, e.Label);
			}

			// GroupGraph names the Neuron by its account even when it has a domain.
			foreach (DotEdge e in x.Groups)
			{
				Node(e.To, TopologyNodeKind.Contact);
				Edge(self, e.To, TopologyLink.Group, e.Label);
			}
		}

		List<TopologyNode> ordered = [.. nodes.Values.OrderBy(n => n.Kind).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase)];
		return new TopologySnapshot(ordered, edges, problems, taken);
	}
}
