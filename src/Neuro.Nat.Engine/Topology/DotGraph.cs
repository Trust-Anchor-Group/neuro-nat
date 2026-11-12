using System.Text.RegularExpressions;

namespace Neuro.Nat.Engine.Topology;

/// <summary>An edge statement from a GraphViz graph: <c>"a" -> "b" [label="x", dir=back]</c>.</summary>
public sealed record DotEdge(string From, string To, IReadOnlyDictionary<string, string> Attributes)
{
	public string? Label => this.Attributes.TryGetValue("label", out string? l) && l.Length > 0 ? l : null;

	/// <summary>True when the arrow is drawn from <see cref="To"/> back to <see cref="From"/>.</summary>
	public bool Back => this.Attributes.TryGetValue("dir", out string? d) && d == "back";
}

/// <summary>
/// Reads the small GraphViz graphs the Neuron's topology commands answer with (ParentGraph,
/// S2SGraph, GroupGraph, TopologyQuery). Only what those commands write is understood: quoted
/// node names, directed edges, and simple attribute lists.
/// </summary>
public static partial class DotGraph
{
	/// <summary>The edges in a chat answer. The graph may be wrapped in a <c>```dot</c> code block.</summary>
	public static IReadOnlyList<DotEdge> Edges(string answer)
	{
		string text = Body(answer);
		List<DotEdge> edges = [];

		foreach (Match m in Edge().Matches(text))
		{
			Dictionary<string, string> attrs = new(StringComparer.OrdinalIgnoreCase);
			if (m.Groups["attrs"].Success)
			{
				foreach (Match a in Attribute().Matches(m.Groups["attrs"].Value))
					attrs[a.Groups["k"].Value] = Unquote(a.Groups["v"].Value);
			}

			edges.Add(new DotEdge(Unescape(m.Groups["a"].Value), Unescape(m.Groups["b"].Value), attrs));
		}

		return edges;
	}

	/// <summary>True when the answer holds a GraphViz graph at all.</summary>
	public static bool IsGraph(string answer) => Body(answer).Contains("digraph", StringComparison.Ordinal);

	private static string Body(string answer)
	{
		int start = answer.IndexOf("```dot", StringComparison.OrdinalIgnoreCase);
		if (start < 0)
			return answer;

		start = answer.IndexOf('\n', start);
		if (start < 0)
			return answer;

		int end = answer.IndexOf("```", start, StringComparison.Ordinal);
		return end < 0 ? answer[start..] : answer[start..end];
	}

	private static string Unquote(string v)
	{
		if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
			return Unescape(v[1..^1]);
		if (v.Length >= 2 && v[0] == '<' && v[^1] == '>')
			return v[1..^1];
		return v;
	}

	private static string Unescape(string s) => s.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

	[GeneratedRegex(@"""(?<a>(?:[^""\\]|\\.)*)""\s*->\s*""(?<b>(?:[^""\\]|\\.)*)""\s*(?:\[(?<attrs>[^\]]*)\])?")]
	private static partial Regex Edge();

	[GeneratedRegex(@"(?<k>\w+)\s*=\s*(?<v>""(?:[^""\\]|\\.)*""|<[^>]*>|[^,\s\]]+)")]
	private static partial Regex Attribute();
}
