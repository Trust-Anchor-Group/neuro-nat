namespace Neuro.AA.Engine.Topology;

/// <summary>
/// Places the points of a network for drawing. A connected part where something has a parent is
/// drawn as a tree: the parent on top, what connects to it in a row under it, and a long list
/// of children that have none of their own hanging in columns. A part with no parents in it
/// gets a force-directed layout (Fruchterman-Reingold), started from a circle in breadth-first
/// order. Either way the result is the same every time for the same network. The parts are
/// then packed in rows, biggest first.
/// </summary>
public static class TopologyLayout
{
	/// <summary>
	/// Positions (centers) by node id. Coordinates start at 0,0; linked nodes are about
	/// <paramref name="spacing"/> apart. With <paramref name="size"/>, the boxes drawn around the
	/// nodes are kept from overlapping, with <paramref name="gap"/> between them.
	/// <paramref name="parents"/> names the links in <paramref name="edges"/> that go from a
	/// child to its parent: those parts become trees.
	/// </summary>
	public static IReadOnlyDictionary<string, (double X, double Y)> Compute(IReadOnlyList<string> nodes, IReadOnlyList<(string From, string To)> edges,
		double spacing = 170, Func<string, (double W, double H)>? size = null, double gap = 24, IReadOnlyList<(string Child, string Parent)>? parents = null)
	{
		size ??= _ => (spacing * 0.75, spacing * 0.75);
		Dictionary<string, HashSet<string>> adjacent = nodes.Distinct().ToDictionary(n => n, _ => new HashSet<string>());
		foreach ((string a, string b) in edges)
		{
			if (a != b && adjacent.TryGetValue(a, out HashSet<string>? x) && adjacent.TryGetValue(b, out HashSet<string>? y))
			{
				x.Add(b);
				y.Add(a);
			}
		}

		// Connected parts, each in breadth-first order from its best connected point.
		List<List<string>> parts = [];
		HashSet<string> placed = [];
		foreach (string start in adjacent.Keys.OrderByDescending(n => adjacent[n].Count).ThenBy(n => n, StringComparer.Ordinal))
		{
			if (!placed.Add(start))
				continue;

			List<string> part = [start];
			for (int i = 0; i < part.Count; i++)
			{
				foreach (string next in adjacent[part[i]].OrderByDescending(n => adjacent[n].Count).ThenBy(n => n, StringComparer.Ordinal))
				{
					if (placed.Add(next))
						part.Add(next);
				}
			}

			parts.Add(part);
		}

		List<(List<string> Part, Dictionary<string, (double X, double Y)> Pos, double W, double H)> laid = [];
		foreach (List<string> part in parts)
		{
			Dictionary<string, (double X, double Y)>? pos = Tree(part, parents, adjacent, size, gap);
			if (pos is null)
			{
				pos = Force(part, adjacent, spacing);
				Separate(part, pos, size, gap);
			}

			// Bounds of the boxes, not just the centers, so parts do not overlap when packed.
			double minX = part.Min(n => pos[n].X - size(n).W / 2), minY = part.Min(n => pos[n].Y - size(n).H / 2);
			foreach (string n in part)
				pos[n] = (pos[n].X - minX, pos[n].Y - minY);
			laid.Add((part, pos, part.Max(n => pos[n].X + size(n).W / 2), part.Max(n => pos[n].Y + size(n).H / 2)));
		}

		// Shelf packing: biggest parts first, rows about as wide as the whole is tall.
		gap = Math.Max(gap, spacing / 2);
		double area = laid.Sum(l => (l.W + gap) * (l.H + gap));
		double rowWidth = Math.Max(laid.Count == 0 ? 0 : laid.Max(l => l.W), Math.Sqrt(area) * 1.6);
		Dictionary<string, (double X, double Y)> result = [];
		double x0 = 0, y0 = 0, rowHeight = 0;
		foreach (var l in laid.OrderByDescending(l => l.Part.Count).ThenByDescending(l => l.W * l.H))
		{
			if (x0 > 0 && x0 + l.W > rowWidth)
			{
				x0 = 0;
				y0 += rowHeight + gap;
				rowHeight = 0;
			}

			foreach (string n in l.Part)
				result[n] = (l.Pos[n].X + x0, l.Pos[n].Y + y0);

			x0 += l.W + gap;
			rowHeight = Math.Max(rowHeight, l.H);
		}

		return result;
	}

	// ----- Trees -----

	/// <summary>The room between a row and the row under it: enough for the lines to turn in.</summary>
	public const double RowGap = 56;

	/// <summary>From this many children without children of their own, they hang in columns instead of a row.</summary>
	public const int HangFrom = 7;

	/// <summary>The room to the left of a hanging card, where its line comes down.</summary>
	public const double HangIndent = 22;

	private const double HangGap = 14;

	/// <summary>
	/// Lays a connected part out as trees, or returns null when nothing in it has a parent.
	/// Points that are in the part through other links only are put in rows under the trees.
	/// </summary>
	private static Dictionary<string, (double X, double Y)>? Tree(List<string> part, IReadOnlyList<(string Child, string Parent)>? parents,
		Dictionary<string, HashSet<string>> adjacent, Func<string, (double W, double H)> size, double gap)
	{
		if (parents is null || parents.Count == 0)
			return null;

		HashSet<string> inPart = [.. part];
		Dictionary<string, string> parentOf = [];
		foreach ((string child, string parent) in parents.OrderBy(p => p.Child, StringComparer.Ordinal).ThenBy(p => p.Parent, StringComparer.Ordinal))
		{
			if (child == parent || !inPart.Contains(child) || !inPart.Contains(parent) || parentOf.ContainsKey(child))
				continue;

			// Never a circle: a parent that is already under this child is not taken.
			bool circle = false;
			for (string? up = parent; up is not null; up = parentOf.GetValueOrDefault(up))
			{
				if (up == child)
				{
					circle = true;
					break;
				}
			}

			if (!circle)
				parentOf[child] = parent;
		}

		if (parentOf.Count == 0)
			return null;

		Dictionary<string, List<string>> children = [];
		foreach ((string child, string parent) in parentOf.OrderBy(p => p.Key, StringComparer.Ordinal))
		{
			if (!children.TryGetValue(parent, out List<string>? list))
				children[parent] = list = [];
			list.Add(child);
		}

		List<string> roots = [.. part.Where(n => !parentOf.ContainsKey(n) && children.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal)];
		List<string> loose = [.. part.Where(n => !parentOf.ContainsKey(n) && !children.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal)];

		Dictionary<string, (double X, double Y)> pos = [];
		Dictionary<string, (double W, double H)> extent = [];

		// How much room a point takes with everything under it.
		(double W, double H) Measure(string n)
		{
			(double w, double h) = size(n);
			if (!children.TryGetValue(n, out List<string>? kids))
				return extent[n] = (w, h);

			List<(double W, double H)> row = Row(kids, out _, out _);
			double rowW = row.Sum(r => r.W) + gap * (row.Count - 1);
			return extent[n] = (Math.Max(w, rowW), h + RowGap + row.Max(r => r.H));
		}

		// The row under a point: each child that has children with all of its own, then the
		// rest side by side, or hanging in columns when they are many.
		List<(double W, double H)> Row(List<string> kids, out List<string> inRow, out List<List<string>> columns)
		{
			List<string> branches = [.. kids.Where(children.ContainsKey)];
			List<string> leaves = [.. kids.Where(k => !children.ContainsKey(k))];
			columns = [];
			inRow = [.. branches];
			List<(double W, double H)> row = [.. branches.Select(b => extent.TryGetValue(b, out var e) ? e : Measure(b))];

			if (leaves.Count >= HangFrom)
			{
				int count = Math.Clamp((int)Math.Ceiling(Math.Sqrt(leaves.Count / 2.0)), 2, 6);
				int perColumn = (int)Math.Ceiling(leaves.Count / (double)count);
				for (int i = 0; i < leaves.Count; i += perColumn)
					columns.Add([.. leaves.Skip(i).Take(perColumn)]);

				foreach (List<string> column in columns)
					row.Add((HangIndent + column.Max(l => size(l).W), column.Sum(l => size(l).H) + HangGap * (column.Count - 1)));
			}
			else
			{
				inRow.AddRange(leaves);
				row.AddRange(leaves.Select(size));
			}

			return row;
		}

		void Place(string n, double left, double top)
		{
			(double w, double h) = size(n);
			(double W, double _) = extent[n];
			pos[n] = (left + W / 2, top + h / 2);
			if (!children.TryGetValue(n, out List<string>? kids))
				return;

			List<(double W, double H)> row = Row(kids, out List<string> inRow, out List<List<string>> columns);
			double x = left + (W - (row.Sum(r => r.W) + gap * (row.Count - 1))) / 2;
			double y = top + h + RowGap;
			for (int i = 0; i < row.Count; i++)
			{
				if (i < inRow.Count)
				{
					if (children.ContainsKey(inRow[i]))
						Place(inRow[i], x, y);
					else
						pos[inRow[i]] = (x + row[i].W / 2, y + size(inRow[i]).H / 2);
				}
				else
				{
					double cy = y;
					foreach (string leaf in columns[i - inRow.Count])
					{
						(double lw, double lh) = size(leaf);
						pos[leaf] = (x + HangIndent + lw / 2, cy + lh / 2);
						cy += lh + HangGap;
					}
				}

				x += row[i].W + gap;
			}
		}

		double at = 0, bottom = 0;
		foreach (string root in roots)
		{
			(double W, double H) = Measure(root);
			Place(root, at, 0);
			at += W + gap * 2;
			bottom = Math.Max(bottom, H);
		}

		// What is only linked some other way: in rows under the trees, each as near as it can
		// be to straight under what it is linked to, so its line is short and crosses little.
		if (loose.Count > 0)
		{
			double width = Math.Max(at - gap * 2, loose.Max(n => size(n).W) * Math.Min(3, loose.Count) + gap * 2);
			double Wanted(string n)
			{
				List<double> near = [.. adjacent[n].Where(pos.ContainsKey).Select(m => pos[m].X)];
				return near.Count == 0 ? 0 : near.Average();
			}

			Dictionary<string, double> wanted = loose.ToDictionary(n => n, Wanted);
			double cursor = 0, y = bottom + RowGap, rowHeight = 0;
			foreach (string n in loose.OrderBy(n => wanted[n]).ThenBy(n => n, StringComparer.Ordinal))
			{
				(double w, double h) = size(n);
				double x = Math.Max(cursor, wanted[n] - w / 2);
				if (x + w > width && cursor > 0)
				{
					cursor = 0;
					y += rowHeight + gap;
					rowHeight = 0;
					x = Math.Max(0, wanted[n] - w / 2);
				}

				x = Math.Max(0, Math.Min(x, Math.Max(cursor, width - w)));
				pos[n] = (x + w / 2, y + h / 2);
				cursor = x + w + gap;
				rowHeight = Math.Max(rowHeight, h);
			}
		}

		return pos;
	}

	// ----- Without parents: pushed apart until it settles -----

	/// <summary>Pushes apart boxes that overlap, along the axis where they overlap least.</summary>
	private static void Separate(List<string> part, Dictionary<string, (double X, double Y)> pos, Func<string, (double W, double H)> size, double gap)
	{
		for (int pass = 0; pass < 60; pass++)
		{
			bool moved = false;
			for (int i = 0; i < part.Count; i++)
			{
				for (int j = i + 1; j < part.Count; j++)
				{
					string a = part[i], b = part[j];
					(double ax, double ay) = pos[a];
					(double bx, double by) = pos[b];
					(double aw, double ah) = size(a);
					(double bw, double bh) = size(b);

					double ox = (aw + bw) / 2 + gap - Math.Abs(ax - bx);
					double oy = (ah + bh) / 2 + gap - Math.Abs(ay - by);
					if (ox <= 0 || oy <= 0)
						continue;

					moved = true;
					if (ox < oy)
					{
						double sx = ax < bx || (ax == bx && i < j) ? -1 : 1;
						pos[a] = (ax + sx * ox / 2, ay);
						pos[b] = (bx - sx * ox / 2, by);
					}
					else
					{
						double sy = ay < by || (ay == by && i < j) ? -1 : 1;
						pos[a] = (ax, ay + sy * oy / 2);
						pos[b] = (bx, by - sy * oy / 2);
					}
				}
			}

			if (!moved)
				break;
		}
	}

	private static Dictionary<string, (double X, double Y)> Force(List<string> part, Dictionary<string, HashSet<string>> adjacent, double k)
	{
		int n = part.Count;
		Dictionary<string, (double X, double Y)> pos = [];
		if (n == 1)
		{
			pos[part[0]] = (0, 0);
			return pos;
		}

		double radius = Math.Max(k, k * n / (2 * Math.PI));
		double[] x = new double[n], y = new double[n];
		Dictionary<string, int> index = [];
		for (int i = 0; i < n; i++)
		{
			index[part[i]] = i;
			double angle = 2 * Math.PI * i / n;
			x[i] = radius * Math.Cos(angle);
			y[i] = radius * Math.Sin(angle);
		}

		List<(int A, int B)> links = [];
		for (int i = 0; i < n; i++)
		{
			foreach (string m in adjacent[part[i]])
			{
				int j = index[m];
				if (j > i)
					links.Add((i, j));
			}
		}

		int iterations = n <= 60 ? 400 : 250;
		double[] dx = new double[n], dy = new double[n];
		double temperature = radius;
		for (int it = 0; it < iterations; it++)
		{
			Array.Clear(dx);
			Array.Clear(dy);

			for (int i = 0; i < n; i++)
			{
				for (int j = i + 1; j < n; j++)
				{
					double ex = x[i] - x[j], ey = y[i] - y[j];
					double d2 = ex * ex + ey * ey;
					if (d2 < 0.01)
					{
						// Same place: nudge apart, the same way every time.
						ex = 0.1 * (i - j);
						ey = 0.1;
						d2 = ex * ex + ey * ey;
					}

					double f = k * k / d2;
					dx[i] += ex * f;
					dy[i] += ey * f;
					dx[j] -= ex * f;
					dy[j] -= ey * f;
				}
			}

			foreach ((int a, int b) in links)
			{
				double ex = x[a] - x[b], ey = y[a] - y[b];
				double d = Math.Sqrt(ex * ex + ey * ey);
				if (d < 0.01)
					continue;

				double f = d / k;
				dx[a] -= ex * f;
				dy[a] -= ey * f;
				dx[b] += ex * f;
				dy[b] += ey * f;
			}

			for (int i = 0; i < n; i++)
			{
				double len = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]);
				if (len > 0)
				{
					double step = Math.Min(len, temperature);
					x[i] += dx[i] / len * step;
					y[i] += dy[i] / len * step;
				}
			}

			temperature = Math.Max(1, radius * (1 - (double)(it + 1) / iterations));
		}

		for (int i = 0; i < n; i++)
			pos[part[i]] = (x[i], y[i]);
		return pos;
	}
}
