using Neuro.AA.Engine.Events;
using Neuro.AA.Engine.Topology;

namespace Neuro.AA.Engine.Tests;

/// <summary>Reading the Neurons' topology graphs, merging them, and laying the network out.</summary>
public class TopologyTests
{
	private const string Parent = "```dot\ndigraph G {\nrankdir=LR\n\"lab.example.com\" -> \"edge1.example.com\" [dir=back];\n}\n```\n";

	private const string S2s = "```dot\ndigraph G {\nrankdir=LR\n\"edge1.example.com\" [label=<<b>edge1.example.com</b>>];\n" +
		"\"edge1.example.com\" -> \"lab.example.com\";\n\"edge1.example.com\" -> \"partner.org\" [label=\"HTTP\"];\n}\n```\n";

	[Fact]
	public void ReadsEdgesAndAttributes()
	{
		IReadOnlyList<DotEdge> p = DotGraph.Edges(Parent);
		DotEdge e = Assert.Single(p);
		Assert.Equal("lab.example.com", e.From);
		Assert.Equal("edge1.example.com", e.To);
		Assert.True(e.Back);

		IReadOnlyList<DotEdge> s = DotGraph.Edges(S2s);
		Assert.Equal(2, s.Count);
		Assert.Null(s[0].Label);
		Assert.Equal("HTTP", s[1].Label);
		Assert.True(DotGraph.IsGraph(S2s));
		Assert.False(DotGraph.IsGraph("Unknown command."));
	}

	[Fact]
	public void MergesAnswersFromSeveralNeurons()
	{
		Guid account = Guid.NewGuid();
		WatchedNeuron edge = new() { AccountId = account, BareJid = "edge1.example.com", Name = "Edge 1", State = WatchState.Live };
		WatchedNeuron lab = new() { AccountId = account, BareJid = "lab.example.com", State = WatchState.Live };
		WatchedNeuron quiet = new() { AccountId = account, BareJid = "quiet.example.com", State = WatchState.Live };

		TopologySnapshot snap = TopologyService.Merge(
		[
			new(edge, "edge1.example.com", DotGraph.Edges(Parent), DotGraph.Edges(S2s), [], null),
			new(lab, "lab.example.com", [], DotGraph.Edges("\"lab.example.com\" -> \"edge1.example.com\";"), [], null),
			new(quiet, null, [], [], [], "The Neuron did not answer."),
		], DateTime.Now);

		Assert.Equal(3, snap.Nodes.Count);
		TopologyNode e1 = snap.Nodes.Single(n => n.Id == "edge1.example.com");
		Assert.Equal("Edge 1", e1.Label);
		Assert.Equal(TopologyNodeKind.Neuron, e1.Kind);
		Assert.Equal(TopologyNodeKind.Neuron, snap.Nodes.Single(n => n.Id == "lab.example.com").Kind);
		Assert.Equal(TopologyNodeKind.Broker, snap.Nodes.Single(n => n.Id == "partner.org").Kind);

		Assert.Contains(snap.Edges, x => x is { From: "edge1.example.com", To: "lab.example.com", Kind: TopologyLink.Parent });

		// Seen from both ends, the federation link between edge1 and lab is one link.
		Assert.Single(snap.Edges, x => x.Kind == TopologyLink.Federation && (x.From, x.To) is ("edge1.example.com", "lab.example.com") or ("lab.example.com", "edge1.example.com"));
		Assert.Contains(snap.Edges, x => x is { To: "partner.org", Label: "HTTP" });

		TopologyProblem problem = Assert.Single(snap.Problems);
		Assert.Equal("quiet.example.com", problem.NeuronJid);
	}

	[Fact]
	public void LaysOutWithoutOverlapAndTheSameEveryTime()
	{
		List<string> nodes = ["hub", .. Enumerable.Range(1, 12).Select(i => "leaf" + i), "alone", "pair1", "pair2"];
		List<(string, string)> edges = [.. Enumerable.Range(1, 12).Select(i => ("hub", "leaf" + i)), ("pair1", "pair2")];

		var a = TopologyLayout.Compute(nodes, edges, 170);
		var b = TopologyLayout.Compute(nodes, edges, 170);

		Assert.Equal(nodes.Count, a.Count);
		foreach (string n in nodes)
			Assert.Equal(a[n], b[n]);

		foreach (string n in nodes)
		{
			foreach (string m in nodes.Where(m => string.CompareOrdinal(m, n) > 0))
			{
				double d = Math.Sqrt(Math.Pow(a[n].X - a[m].X, 2) + Math.Pow(a[n].Y - a[m].Y, 2));
				Assert.True(d >= 170 * 0.74, $"{n} and {m} are {d:F0} apart");
			}
		}

		Assert.True(a.Values.Min(p => p.X) >= -0.001 && a.Values.Min(p => p.Y) >= -0.001);
	}
}
