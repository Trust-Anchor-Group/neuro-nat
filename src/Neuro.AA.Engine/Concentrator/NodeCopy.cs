using Waher.Networking.XMPP.Concentrator;
using Waher.Networking.XMPP.DataForms;
using Waher.Networking.XMPP.DataForms.FieldTypes;

namespace Neuro.AA.Engine.Concentrator;

/// <summary>
/// A node and its subtree as copied from a Neuron: type and editable parameters, so it can be
/// created again under another node, on the same Neuron or another one.
/// </summary>
public sealed record NodeCopy(string NodeType, string Label, IReadOnlyDictionary<string, string[]> Values, IReadOnlyList<NodeCopy> Children)
{
	/// <summary>Nodes in this copy, itself included.</summary>
	public int Count => 1 + this.Children.Sum(c => c.Count);

	/// <summary>Copies a node, and its children unless <paramref name="withChildren"/> is false.</summary>
	public static async Task<NodeCopy> CopyAsync(NeuronConcentrator c, NodeInformation node, bool withChildren = true, int maxNodes = 500)
	{
		int budget = maxNodes;
		return await CopyAsync(c, node, withChildren, () => --budget >= 0);
	}

	private static async Task<NodeCopy> CopyAsync(NeuronConcentrator c, NodeInformation node, bool withChildren, Func<bool> take)
	{
		if (!take())
			throw new ConcentratorException("The subtree is too large to copy in one go.");

		// What the caller holds may be older than the node: it may have children by now.
		node = await c.GetNodeAsync(node);

		FormSession edit = await c.EditAsync(node);
		Dictionary<string, string[]> values = [];
		foreach (Field f in edit.Form.Fields)
		{
			if (f.ReadOnly || f is FixedField || string.IsNullOrEmpty(f.Var))
				continue;
			values[f.Var] = f.ValueStrings ?? [];
		}

		if (edit.Form.CanCancel)
			await edit.Form.Cancel();

		List<NodeCopy> children = [];
		if (withChildren && node.HasChildren)
		{
			foreach (NodeInformation child in await c.GetChildNodesAsync(node))
				children.Add(await CopyAsync(c, child, true, take));
		}

		return new NodeCopy(node.NodeType, string.IsNullOrEmpty(node.LocalId) ? node.NodeId : node.LocalId, values, children);
	}

	/// <summary>Outcome of a paste: what was created and what the Neuron refused.</summary>
	public sealed record PasteResult(int Created, IReadOnlyList<string> Problems, NodeInformation? Top);

	/// <summary>
	/// Creates the copied nodes under <paramref name="parent"/>. A node id that is already taken
	/// gets " (copy)" added. Children the Neuron refuses are skipped and reported.
	/// </summary>
	public async Task<PasteResult> PasteAsync(NeuronConcentrator c, NodeInformation parent, IProgress<int>? progress = null)
	{
		List<string> problems = [];
		int created = 0;

		async Task<NodeInformation?> Paste(NodeCopy copy, NodeInformation under)
		{
			FormSession add;
			try
			{
				add = await c.AddAsync(under, copy.NodeType);
			}
			catch (Exception ex)
			{
				problems.Add($"{copy.Label}: {ex.Message}");
				return null;
			}

			foreach (Field f in add.Form.Fields)
			{
				if (!f.ReadOnly && copy.Values.TryGetValue(f.Var, out string[]? v))
					await f.SetValue(v);
			}

			string? id = copy.Values.TryGetValue("NodeId", out string[]? ids) && ids.Length > 0 ? ids[0] : null;

			for (int attempt = 0; attempt < 6; attempt++)
			{
				FormOutcome r = await add.SubmitAsync();
				if (r.Ok)
				{
					created++;
					progress?.Report(created);
					NodeInformation? node = r.Node;
					if (node is not null)
					{
						foreach (NodeCopy child in copy.Children)
							await Paste(child, node);
					}
					return node;
				}

				Field? idField = add.Form["NodeId"];
				if (r.HasFieldErrors && idField is not null && idField.HasError && id is not null)
				{
					// Taken: try "X (copy)", "X (copy 2)", ...
					await idField.SetValue(attempt == 0 ? id + " (copy)" : $"{id} (copy {attempt + 1})");
					continue;
				}

				string remarks = string.Join("; ", add.Form.Fields.Where(f => f.HasError).Select(f => $"{f.Label}: {f.Error}"));
				problems.Add($"{copy.Label}: {(remarks.Length > 0 ? remarks : r.Error)}");
				if (add.Form.CanCancel)
					await add.Form.Cancel();
				return null;
			}

			problems.Add($"{copy.Label}: no free node id found.");
			return null;
		}

		NodeInformation? top = await Paste(this, parent);
		return new PasteResult(created, problems, top);
	}
}
