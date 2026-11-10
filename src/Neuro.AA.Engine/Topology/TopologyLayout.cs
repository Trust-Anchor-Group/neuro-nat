namespace Neuro.AA.Engine.Topology;

/// <summary>
/// Places the points of a network for drawing: a force-directed layout (Fruchterman-Reingold)
/// per connected part, started from a circle in breadth-first order so the result is the same
/// every time for the same network, then the parts packed in rows, biggest first.
/// </summary>
public static class TopologyLayout
{
	/// <summary>
	/// Positions (centers) by node id. Coordinates start at 0,0; linked nodes are about
	/// <paramref name="spacing"/> apart. With <paramref name="size"/>, the boxes drawn around the
	/// nodes are kept from overlapping, with <paramref name="gap"/> between them.
	/// </summary>
	public static IReadOnlyDictionary<string, (double X, double Y)> Compute(IReadOnlyList<string> nodes, IReadOnlyList<(string From, string To)> edges,
		double spacing = 170, Func<string, (double W, double H)>? size = null, double gap = 24)
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
			Dictionary<string, (double X, double Y)> pos = Force(part, adjacent, spacing);
			Separate(part, pos, size, gap);

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
