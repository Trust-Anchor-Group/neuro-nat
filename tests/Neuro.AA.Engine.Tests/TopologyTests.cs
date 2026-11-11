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

	private static void AssertNoOverlap(IReadOnlyDictionary<string, (double X, double Y)> pos, double w, double h)
	{
		foreach (string n in pos.Keys)
		{
			foreach (string m in pos.Keys.Where(m => string.CompareOrdinal(m, n) > 0))
				Assert.True(Math.Abs(pos[n].X - pos[m].X) >= w || Math.Abs(pos[n].Y - pos[m].Y) >= h, $"{n} and {m} overlap");
		}
	}

	[Fact]
	public void WhatHasAParentIsDrawnAsATreeWithTheParentOnTop()
	{
		List<string> nodes = ["root", "a", "b", "a1", "a2", "peer"];
		List<(string, string)> parents = [("a", "root"), ("b", "root"), ("a1", "a"), ("a2", "a")];
		List<(string, string)> edges = [.. parents, ("peer", "b"), ("a", "root")];

		var pos = TopologyLayout.Compute(nodes, edges, 150, _ => (200, 70), 26, parents);
		var again = TopologyLayout.Compute(nodes, edges, 150, _ => (200, 70), 26, parents);
		Assert.Equal(nodes.Count, pos.Count);
		foreach (string n in nodes)
			Assert.Equal(pos[n], again[n]);

		// Rows: the parent, what connects to it, and what connects to those.
		Assert.Equal(pos["a"].Y, pos["b"].Y);
		Assert.Equal(pos["a1"].Y, pos["a2"].Y);
		Assert.True(pos["root"].Y + 70 < pos["a"].Y);
		Assert.True(pos["a"].Y + 70 < pos["a1"].Y);

		// A parent stands over the middle of its children.
		Assert.Equal((pos["a1"].X + pos["a2"].X) / 2, pos["a"].X, 3);
		Assert.True(pos["root"].X > pos["a"].X && pos["root"].X < pos["b"].X);

		// What is only linked some other way goes under the tree.
		Assert.True(pos["peer"].Y > pos["a1"].Y + 70);

		AssertNoOverlap(pos, 200, 70);
		Assert.True(pos.Values.Min(p => p.X) >= 99.999 && pos.Values.Min(p => p.Y) >= 34.999);
	}

	[Fact]
	public void ManyChildrenHangInColumnsUnderTheirParent()
	{
		List<string> leaves = [.. Enumerable.Range(1, 12).Select(i => "leaf" + i.ToString("00"))];
		List<string> nodes = ["hub", .. leaves];
		List<(string, string)> parents = [.. leaves.Select(l => (l, "hub"))];

		var pos = TopologyLayout.Compute(nodes, parents, 150, _ => (200, 70), 26, parents);

		// Twelve in a row would be far too wide: three columns of four, each column left-aligned.
		Assert.Equal(3, leaves.Select(l => pos[l].X).Distinct().Count());
		Assert.Equal(4, leaves.Select(l => pos[l].Y).Distinct().Count());
		Assert.All(leaves, l => Assert.True(pos[l].Y > pos["hub"].Y + 70));
		Assert.True(pos["hub"].X > leaves.Min(l => pos[l].X) && pos["hub"].X < leaves.Max(l => pos[l].X));
		AssertNoOverlap(pos, 200, 70);

		// A circle of parents is not followed around: the layout still ends, and places everything.
		var circle = TopologyLayout.Compute(["x", "y", "z"], [("x", "y"), ("y", "z"), ("z", "x")], 150, _ => (200, 70), 26, [("x", "y"), ("y", "z"), ("z", "x")]);
		Assert.Equal(3, circle.Count);
		AssertNoOverlap(circle, 200, 70);
	}
}
