using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Neuro.Nat.Engine.Topology;

namespace Neuro.Nat.App.Controls;

/// <summary>A point on the map, placed by the layout.</summary>
public sealed record MapNode(string Id, string Label, string Sub, TopologyNodeKind Kind, double X, double Y, string Tone)
{
	/// <summary>How big the layout took the card to be. The card is drawn that size, so rows and columns line up. 0 to measure the text.</summary>
	public double Width { get; init; }
	public double Height { get; init; }

	/// <summary>Load figures shown under the name: CPU, free memory, free disk.</summary>
	public IReadOnlyList<MapLoad> Load { get; init; } = [];
}

/// <summary>One load figure on a card: a short name, its value, a tone, and for CPU how full the bar is.</summary>
public sealed record MapLoad(string Name, string Value, string Tone, double? Fraction);

/// <summary>A connection on the map.</summary>
public sealed record MapEdge(string From, string To, TopologyLink Kind, string? Label);

/// <summary>What the map draws.</summary>
public sealed record MapGraph(IReadOnlyList<MapNode> Nodes, IReadOnlyList<MapEdge> Edges)
{
	public static readonly MapGraph Empty = new([], []);
}

/// <summary>
/// The network map: Neurons as cards with their health, brokers they connect to, and contacts,
/// with parent links, federation links and group links. Wheel zooms around the pointer, dragging
/// pans, a click selects and a double click opens. Filter text dims what does not match.
/// </summary>
public sealed class TopologyMap : Control
{
	public static readonly StyledProperty<MapGraph?> GraphProperty =
		AvaloniaProperty.Register<TopologyMap, MapGraph?>(nameof(Graph));

	public static readonly StyledProperty<string?> SelectedIdProperty =
		AvaloniaProperty.Register<TopologyMap, string?>(nameof(SelectedId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

	public static readonly StyledProperty<string?> FilterProperty =
		AvaloniaProperty.Register<TopologyMap, string?>(nameof(Filter));

	/// <summary>Change it to fit the whole map in view.</summary>
	public static readonly StyledProperty<int> FitVersionProperty =
		AvaloniaProperty.Register<TopologyMap, int>(nameof(FitVersion));

	private const double CardHeight = 50;
	private const double ContactHeight = 30;
	private const double MinCardWidth = 132;
	private const double MaxCardWidth = 240;
	private const double TitleSize = 13.5;
	private const double ContactTitleSize = 12;
	private const double SubSize = 11.5;

	/// <summary>About how big a card is drawn, for the layout to keep cards apart before they are measured.</summary>
	public static (double W, double H) EstimateSize(string label, string sub, TopologyNodeKind kind, bool hasLoad = false)
	{
		double text = Math.Max(label.Length * (kind == TopologyNodeKind.Contact ? 6.9 : 8), sub.Length * 6.5);
		double w = Math.Clamp(text + 34, MinCardWidth, MaxCardWidth);
		double h = kind == TopologyNodeKind.Contact ? ContactHeight : CardHeight;
		return hasLoad ? (Math.Max(w, LoadCardWidth), h + LoadHeight) : (w, h);
	}

	/// <summary>Height of the load row under a Neuron's name.</summary>
	private const double LoadHeight = 26;

	/// <summary>Cards with a load row are at least this wide, so three figures fit.</summary>
	private const double LoadCardWidth = 214;

	private readonly Dictionary<string, (Rect Box, FormattedText Title, FormattedText? Sub)> cards = [];
	private double zoom = 1;
	private Vector offset;
	private Point? dragFrom;
	private Vector dragOffset;
	private bool dragged;
	private string? hoverId;
	private bool fitPending = true;

	static TopologyMap()
	{
		AffectsRender<TopologyMap>(GraphProperty, SelectedIdProperty, FilterProperty);
		FocusableProperty.OverrideDefaultValue<TopologyMap>(true);
		ClipToBoundsProperty.OverrideDefaultValue<TopologyMap>(true);
	}

	public TopologyMap()
	{
		this.ActualThemeVariantChanged += (_, _) =>
		{
			this.cards.Clear();
			this.InvalidateVisual();
		};
	}

	public MapGraph? Graph
	{
		get => this.GetValue(GraphProperty);
		set => this.SetValue(GraphProperty, value);
	}

	public string? SelectedId
	{
		get => this.GetValue(SelectedIdProperty);
		set => this.SetValue(SelectedIdProperty, value);
	}

	public string? Filter
	{
		get => this.GetValue(FilterProperty);
		set => this.SetValue(FilterProperty, value);
	}

	public int FitVersion
	{
		get => this.GetValue(FitVersionProperty);
		set => this.SetValue(FitVersionProperty, value);
	}

	/// <summary>Raised on a double click on a card.</summary>
	public event EventHandler<string>? NodeActivated;

	/// <summary>Zooms around the middle of the view.</summary>
	public void ZoomBy(double factor) => this.ZoomAt(new Point(this.Bounds.Width / 2, this.Bounds.Height / 2), factor);

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		if (change.Property == GraphProperty)
		{
			// A new scan keeps the view where it is, unless nothing was shown before.
			bool wasEmpty = change.OldValue is not MapGraph old || old.Nodes.Count == 0;
			this.cards.Clear();
			if (wasEmpty)
				this.fitPending = true;
		}
		else if (change.Property == FitVersionProperty)
		{
			this.fitPending = true;
			this.InvalidateVisual();
		}
	}

	protected override void OnSizeChanged(SizeChangedEventArgs e)
	{
		base.OnSizeChanged(e);
		if (e.PreviousSize.Width < 1 || e.PreviousSize.Height < 1)
			this.fitPending = true;
	}

	// ---- Drawing -----------------------------------------------------------------------------

	private IBrush Brush(string key, IBrush fallback)
		=> this.TryFindResource(key, this.ActualThemeVariant, out object? v) && v is IBrush b ? b : fallback;

	private static IBrush Fade(IBrush b, double opacity)
		=> b is ISolidColorBrush s ? new SolidColorBrush(s.Color, s.Opacity * opacity) : b;

	private Typeface Face(FontWeight weight)
	{
		FontFamily family = this.TryFindResource("Font.UI", out object? f) && f is FontFamily ff ? ff : FontFamily.Default;
		return new Typeface(family, FontStyle.Normal, weight);
	}

	private void Measure(MapGraph g)
	{
		if (this.cards.Count == g.Nodes.Count)
			return;

		this.cards.Clear();
		IBrush ink = this.Brush("Ink.Primary", Brushes.Black);
		IBrush ink2 = this.Brush("Ink.Secondary", Brushes.Gray);
		Typeface bold = this.Face(FontWeight.SemiBold);
		Typeface regular = this.Face(FontWeight.Normal);

		foreach (MapNode n in g.Nodes)
		{
			FormattedText title = new(n.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, n.Kind == TopologyNodeKind.Neuron ? bold : regular,
				n.Kind == TopologyNodeKind.Contact ? ContactTitleSize : TitleSize, ink)
			{
				MaxTextWidth = MaxCardWidth - 34,
				MaxLineCount = 1,
				Trimming = TextTrimming.CharacterEllipsis,
			};

			FormattedText? sub = string.IsNullOrEmpty(n.Sub) ? null : new(n.Sub, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, regular, SubSize, ink2)
			{
				MaxTextWidth = MaxCardWidth - 34,
				MaxLineCount = 1,
				Trimming = TextTrimming.CharacterEllipsis,
			};

			double w = Math.Clamp(Math.Max(title.Width, sub?.Width ?? 0) + 34, MinCardWidth, MaxCardWidth);
			double h = n.Kind == TopologyNodeKind.Contact ? ContactHeight : CardHeight;
			if (n.Load.Count > 0)
			{
				w = Math.Max(w, LoadCardWidth);
				h += LoadHeight;
			}
			if (n.Width > 0 && n.Height > 0)
				(w, h) = (n.Width, n.Height);

			this.cards[n.Id] = (new Rect(n.X - w / 2, n.Y - h / 2, w, h), title, sub);
		}
	}

	private void Fit(MapGraph g)
	{
		this.fitPending = false;
		if (g.Nodes.Count == 0 || this.Bounds.Width < 10 || this.Bounds.Height < 10)
			return;

		Rect all = this.cards.Values.Select(c => c.Box).Aggregate((a, b) => a.Union(b)).Inflate(48);
		this.zoom = Math.Clamp(Math.Min(this.Bounds.Width / all.Width, this.Bounds.Height / all.Height), 0.3, 1.2);
		this.offset = new Vector(
			(this.Bounds.Width - all.Width * this.zoom) / 2 - all.X * this.zoom,
			(this.Bounds.Height - all.Height * this.zoom) / 2 - all.Y * this.zoom);
	}

	private bool Matches(MapNode n)
	{
		string? f = this.Filter?.Trim();
		return string.IsNullOrEmpty(f) || n.Label.Contains(f, StringComparison.OrdinalIgnoreCase) || n.Id.Contains(f, StringComparison.OrdinalIgnoreCase) ||
			n.Sub.Contains(f, StringComparison.OrdinalIgnoreCase);
	}

	public override void Render(DrawingContext context)
	{
		IBrush ground = this.Brush("Surface.Ground", Brushes.White);
		context.FillRectangle(ground, new Rect(this.Bounds.Size));

		MapGraph? g = this.Graph;
		if (g is null || g.Nodes.Count == 0)
			return;

		this.Measure(g);
		if (this.fitPending)
			this.Fit(g);

		IBrush line = this.Brush("Line", Brushes.LightGray);
		IBrush lineStrong = this.Brush("Line.Strong", Brushes.Gray);
		IBrush panel = this.Brush("Surface.Panel", Brushes.White);
		IBrush hover = this.Brush("Surface.Hover", Brushes.WhiteSmoke);
		IBrush accent = this.Brush("Accent", Brushes.RoyalBlue);
		IBrush accentSubtle = this.Brush("Accent.Subtle", Brushes.AliceBlue);
		IBrush ink2 = this.Brush("Ink.Secondary", Brushes.Gray);
		IBrush ink3 = this.Brush("Ink.Tertiary", Brushes.Gray);

		// A quiet dot grid that moves with the map, so panning has something to hold on to.
		double step = 28 * this.zoom;
		if (step >= 10)
		{
			IBrush dot = Fade(line, 0.9);
			double sx = ((this.offset.X % step) + step) % step, sy = ((this.offset.Y % step) + step) % step;
			for (double x = sx; x < this.Bounds.Width; x += step)
			{
				for (double y = sy; y < this.Bounds.Height; y += step)
					context.FillRectangle(dot, new Rect(x, y, 1.2, 1.2));
			}
		}

		HashSet<string> matching = [.. g.Nodes.Where(this.Matches).Select(n => n.Id)];
		bool filtering = matching.Count < g.Nodes.Count;
		string? selected = this.SelectedId;

		using (context.PushTransform(Matrix.CreateScale(this.zoom, this.zoom) * Matrix.CreateTranslation(this.offset)))
		{
			Typeface labelFace = this.Face(FontWeight.Normal);

			// Two points can be linked in more than one way. A parent that is also federated is
			// one line, in the federation colour. Anything else that doubles is drawn side by side.
			static (string, string) Pair(MapEdge e) => string.CompareOrdinal(e.From, e.To) < 0 ? (e.From, e.To) : (e.To, e.From);
			HashSet<(string, string)> parentPairs = [.. g.Edges.Where(e => e.Kind == TopologyLink.Parent).Select(Pair)];
			HashSet<(string, string)> federatedPairs = [.. g.Edges.Where(e => e.Kind == TopologyLink.Federation).Select(Pair)];
			List<MapEdge> drawn = [.. g.Edges.Where(e => e.Kind != TopologyLink.Federation || !parentPairs.Contains(Pair(e)))];

			Dictionary<(string, string), int> pairCount = [];
			foreach (MapEdge e in drawn)
				pairCount[Pair(e)] = pairCount.GetValueOrDefault(Pair(e)) + 1;
			Dictionary<(string, string), int> pairIndex = [];

			// Which cards hang in a column under their parent: their line comes down beside them.
			Dictionary<string, string> parentOf = [];
			foreach (MapEdge e in g.Edges.Where(e => e.Kind == TopologyLink.Parent))
				parentOf.TryAdd(e.From, e.To);
			bool Hangs(string id, Rect box)
			{
				if (!parentOf.TryGetValue(id, out string? parent))
					return false;

				foreach ((string other, string otherParent) in parentOf)
				{
					if (other != id && otherParent == parent && this.cards.TryGetValue(other, out var o) &&
						Math.Abs(o.Box.Left - box.Left) < 1 && Math.Abs(o.Box.Center.Y - box.Center.Y) > 1)
						return true;
				}

				return false;
			}

			// Links under the cards. The ones of what is selected last, so they lie on top.
			foreach (MapEdge e in drawn.OrderBy(e => selected is not null && (e.From == selected || e.To == selected) ? 1 : 0))
			{
				if (!this.cards.TryGetValue(e.From, out var a) || !this.cards.TryGetValue(e.To, out var b))
					continue;

				(string, string) pair = Pair(e);
				int index = pairIndex.GetValueOrDefault(pair);
				pairIndex[pair] = index + 1;

				bool dim = filtering && !(matching.Contains(e.From) && matching.Contains(e.To));
				bool lit = selected is not null && (e.From == selected || e.To == selected);
				bool federated = e.Kind == TopologyLink.Federation || (e.Kind == TopologyLink.Parent && federatedPairs.Contains(pair));
				IBrush stroke = federated ? (lit ? accent : Fade(accent, 0.6))
					: e.Kind == TopologyLink.Group ? (lit ? ink2 : Fade(ink3, 0.7))
					: lit ? ink2 : lineStrong;
				if (dim)
					stroke = Fade(stroke, 0.25);

				double thickness = (lit ? 2 : 1.4) / Math.Max(this.zoom, 0.6);
				Pen pen = new(stroke, thickness,
					e.Kind == TopologyLink.Group ? new DashStyle([3, 3], 0) : e.Kind == TopologyLink.Parent ? null : new DashStyle([7, 4], 0))
				{
					LineJoin = PenLineJoin.Round,
				};

				Point labelAt;
				if (e.Kind == TopologyLink.Parent && b.Box.Bottom + 16 <= a.Box.Top)
				{
					// A tree: down from the parent, along under it, and into the child from above.
					// A child that hangs in a column is entered from its left side.
					Point into = new(b.Box.Center.X, b.Box.Bottom);
					double bar = b.Box.Bottom + Engine.Topology.TopologyLayout.RowGap / 2;
					List<Point> route = [];
					if (Hangs(e.From, a.Box))
					{
						double down = a.Box.Left - Engine.Topology.TopologyLayout.HangIndent / 2;
						route.Add(new Point(a.Box.Left, a.Box.Center.Y));
						route.Add(new Point(down, a.Box.Center.Y));
						route.Add(new Point(down, bar));
						labelAt = new Point(down, (a.Box.Center.Y + bar) / 2);
					}
					else
					{
						route.Add(new Point(a.Box.Center.X, a.Box.Top));
						route.Add(new Point(a.Box.Center.X, bar));
						labelAt = new Point(a.Box.Center.X, (a.Box.Top + bar) / 2);
					}

					route.Add(new Point(into.X, bar));
					route.Add(into);
					context.DrawGeometry(null, pen, Rounded(route, 6));
					Arrow(context, new Pen(stroke, thickness), new Point(into.X, bar), into);
				}
				else if (e.Kind != TopologyLink.Parent && Math.Abs(a.Box.Center.Y - b.Box.Center.Y) < 8 && Math.Abs(a.Box.Center.X - b.Box.Center.X) > (a.Box.Width + b.Box.Width) / 2 + 60)
				{
					// Two in the same row with others between them: under the row, not through it.
					var (left, right) = a.Box.Center.X < b.Box.Center.X ? (a.Box, b.Box) : (b.Box, a.Box);
					Point p1 = new(left.Center.X + left.Width / 4, left.Bottom), p2 = new(right.Center.X - right.Width / 4, right.Bottom);
					double dip = Math.Max(left.Bottom, right.Bottom) + 22 + index * 7;
					StreamGeometry arc = new();
					using (StreamGeometryContext c = arc.Open())
					{
						c.BeginFigure(p1, false);
						c.CubicBezierTo(new Point(p1.X, dip), new Point(p2.X, dip), p2);
						c.EndFigure(false);
					}

					context.DrawGeometry(null, pen, arc);
					labelAt = new Point((p1.X + p2.X) / 2, dip - 6);
				}
				else
				{
					Vector shift = default;
					if (pairCount[pair] > 1)
					{
						Point ca = a.Box.Center, cb = b.Box.Center;
						double ax = cb.X - ca.X, ay = cb.Y - ca.Y;
						double len = Math.Sqrt(ax * ax + ay * ay);
						if (len > 0.001)
						{
							// The same side whichever way the link points.
							double sign = string.CompareOrdinal(e.From, e.To) < 0 ? 1 : -1;
							Vector normal = new(-ay / len * sign, ax / len * sign);
							shift = normal * ((index - (pairCount[pair] - 1) / 2.0) * 7);
						}
					}

					Point p1 = Edge(a.Box, b.Box.Center) + shift, p2 = Edge(b.Box, a.Box.Center) + shift;
					context.DrawLine(pen, p1, p2);

					// Parent links point at the parent; the others have no direction.
					if (e.Kind == TopologyLink.Parent)
						Arrow(context, new Pen(stroke, thickness), p1, p2);

					labelAt = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
				}

				if (e.Label is string label && this.zoom >= 0.55)
				{
					FormattedText t = new(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, labelFace, 10.5, dim ? Fade(ink2, 0.4) : ink2);
					Rect chip = new(labelAt.X - t.Width / 2 - 5, labelAt.Y - t.Height / 2 - 1, t.Width + 10, t.Height + 2);
					context.DrawRectangle(ground, new Pen(line, 1), chip, 4, 4);
					context.DrawText(t, new Point(chip.X + 5, chip.Y + 1));
				}
			}

			// Cards.
			foreach (MapNode n in g.Nodes)
			{
				var (box, title, sub) = this.cards[n.Id];
				bool isSelected = n.Id == selected;
				double o = filtering && !matching.Contains(n.Id) ? 0.3 : 1;

				using (context.PushOpacity(o))
				{
					switch (n.Kind)
					{
						case TopologyNodeKind.Neuron:
							context.DrawRectangle(isSelected ? accentSubtle : n.Id == this.hoverId ? hover : panel,
								new Pen(isSelected ? accent : lineStrong, isSelected ? 2 : 1), box, 8, 8);
							break;

						case TopologyNodeKind.Broker:
							context.DrawRectangle(isSelected ? accentSubtle : n.Id == this.hoverId ? hover : ground,
								new Pen(isSelected ? accent : lineStrong, isSelected ? 2 : 1, new DashStyle([4, 3], 0)), box, 8, 8);
							break;

						default:
							context.DrawRectangle(isSelected ? accentSubtle : n.Id == this.hoverId ? hover : ground,
								new Pen(isSelected ? accent : line, isSelected ? 2 : 1), box, box.Height / 2, box.Height / 2);
							break;
					}

					// The name part is the card without its load row.
					Rect head = n.Load.Count > 0 ? new Rect(box.X, box.Y, box.Width, box.Height - LoadHeight) : box;
					if (n.Load.Count > 0)
						this.DrawLoad(context, n.Load, new Rect(box.X, head.Bottom, box.Width, LoadHeight), line);

					// Health dot for the Neurons you administer; a hollow ring for the rest.
					Point dotAt = new(box.X + 15, head.Center.Y);
					if (n.Kind == TopologyNodeKind.Neuron)
						context.DrawEllipse(this.Brush(ToneKey(n.Tone), Brushes.Gray), null, dotAt, 4.5, 4.5);
					else
						context.DrawEllipse(null, new Pen(ink3, 1.2), dotAt, 3.5, 3.5);

					double textX = box.X + 26;
					if (sub is null)
						context.DrawText(title, new Point(textX, head.Center.Y - title.Height / 2));
					else
					{
						double top = head.Center.Y - (title.Height + sub.Height) / 2;
						context.DrawText(title, new Point(textX, top));
						context.DrawText(sub, new Point(textX, top + title.Height));
					}
				}
			}
		}
	}

	/// <summary>The load row: a hairline, then each figure with its value in its tone; CPU gets a small bar.</summary>
	private void DrawLoad(DrawingContext context, IReadOnlyList<MapLoad> load, Rect row, IBrush line)
	{
		context.DrawLine(new Pen(line, 1), new Point(row.X + 8, row.Y + 0.5), new Point(row.Right - 8, row.Y + 0.5));
		Typeface regular = this.Face(FontWeight.Normal);
		Typeface bold = this.Face(FontWeight.SemiBold);
		IBrush ink3 = this.Brush("Ink.Tertiary", Brushes.Gray);
		double cell = (row.Width - 16) / load.Count;

		for (int i = 0; i < load.Count; i++)
		{
			MapLoad l = load[i];
			IBrush tone = l.Tone == "idle" ? this.Brush("Ink.Secondary", Brushes.Gray) : this.Brush(ToneKey(l.Tone), Brushes.Gray);
			FormattedText name = new(l.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, regular, 10, ink3);
			FormattedText value = new(l.Value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold, 11, tone);
			double x = row.X + 8 + i * cell;
			double y = row.Y + (row.Height - value.Height) / 2;
			context.DrawText(name, new Point(x, y + (value.Height - name.Height) / 2));
			context.DrawText(value, new Point(x + name.Width + 4, y));

			if (l.Fraction is double f)
			{
				double bx = x + name.Width + 4, bw = Math.Max(16, value.Width), by = y + value.Height + 1;
				context.FillRectangle(this.Brush("Surface.Pressed", Brushes.LightGray), new Rect(bx, by, bw, 2));
				context.FillRectangle(tone, new Rect(bx, by, bw * f, 2));
			}
		}
	}

	private static string ToneKey(string tone) => tone switch
	{
		"ok" => "State.Ok",
		"busy" => "State.Busy",
		"bad" => "State.Bad",
		_ => "State.Idle",
	};

	/// <summary>A line through the points, with its corners rounded.</summary>
	private static StreamGeometry Rounded(IReadOnlyList<Point> points, double radius)
	{
		StreamGeometry path = new();
		using StreamGeometryContext c = path.Open();
		c.BeginFigure(points[0], false);
		for (int i = 1; i < points.Count - 1; i++)
		{
			Point before = points[i - 1], corner = points[i], after = points[i + 1];
			Vector toBefore = before - corner, toAfter = after - corner;
			double r = Math.Min(radius, Math.Min(toBefore.Length, toAfter.Length) / 2);
			if (r < 0.5)
			{
				c.LineTo(corner);
				continue;
			}

			c.LineTo(corner + toBefore / toBefore.Length * r);
			c.QuadraticBezierTo(corner, corner + toAfter / toAfter.Length * r);
		}

		c.LineTo(points[^1]);
		c.EndFigure(false);
		return path;
	}

	/// <summary>Where the line from a card's center towards a point leaves the card.</summary>
	private static Point Edge(Rect box, Point towards)
	{
		Point c = box.Center;
		double dx = towards.X - c.X, dy = towards.Y - c.Y;
		if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
			return c;

		double sx = Math.Abs(dx) < 0.001 ? double.MaxValue : box.Width / 2 / Math.Abs(dx);
		double sy = Math.Abs(dy) < 0.001 ? double.MaxValue : box.Height / 2 / Math.Abs(dy);
		double s = Math.Min(sx, sy);
		return new Point(c.X + dx * s, c.Y + dy * s);
	}

	private static void Arrow(DrawingContext context, Pen pen, Point from, Point to)
	{
		double dx = to.X - from.X, dy = to.Y - from.Y;
		double len = Math.Sqrt(dx * dx + dy * dy);
		if (len < 12)
			return;

		dx /= len;
		dy /= len;
		const double size = 7;
		Point left = new(to.X - dx * size - dy * size * 0.55, to.Y - dy * size + dx * size * 0.55);
		Point right = new(to.X - dx * size + dy * size * 0.55, to.Y - dy * size - dx * size * 0.55);
		context.DrawLine(pen, left, to);
		context.DrawLine(pen, right, to);
	}

	// ---- Pointer -----------------------------------------------------------------------------

	/// <summary>The point under a position in the control, or null. For the right-click menu.</summary>
	public string? IdAt(Point p) => this.HitTest(p);

	private string? HitTest(Point p)
	{
		Point world = new((p.X - this.offset.X) / this.zoom, (p.Y - this.offset.Y) / this.zoom);
		foreach (var (id, c) in this.cards.Reverse())
		{
			if (c.Box.Contains(world))
				return id;
		}

		return null;
	}

	private void ZoomAt(Point at, double factor)
	{
		double z = Math.Clamp(this.zoom * factor, 0.25, 2.5);
		factor = z / this.zoom;
		this.offset = new Vector(at.X - (at.X - this.offset.X) * factor, at.Y - (at.Y - this.offset.Y) * factor);
		this.zoom = z;
		this.InvalidateVisual();
	}

	protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
	{
		base.OnPointerWheelChanged(e);
		this.ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? 1.12 : 1 / 1.12);
		e.Handled = true;
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		this.Focus();
		Point p = e.GetPosition(this);
		string? hit = this.HitTest(p);

		if (e.ClickCount >= 2 && hit is not null)
		{
			this.NodeActivated?.Invoke(this, hit);
			e.Handled = true;
			return;
		}

		this.dragFrom = p;
		this.dragOffset = this.offset;
		this.dragged = false;
		e.Pointer.Capture(this);
		e.Handled = true;
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		Point p = e.GetPosition(this);

		if (this.dragFrom is Point from)
		{
			Vector d = p - from;
			if (this.dragged || Math.Abs(d.X) + Math.Abs(d.Y) > 4)
			{
				this.dragged = true;
				this.offset = this.dragOffset + d;
				this.Cursor = new Cursor(StandardCursorType.SizeAll);
				this.InvalidateVisual();
			}

			return;
		}

		string? hit = this.HitTest(p);
		if (hit != this.hoverId)
		{
			this.hoverId = hit;
			this.Cursor = hit is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
			this.InvalidateVisual();
		}
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (this.dragFrom is Point p && !this.dragged)
			this.SelectedId = this.HitTest(p);

		this.dragFrom = null;
		this.dragged = false;
		this.Cursor = this.hoverId is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
		e.Pointer.Capture(null);
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		switch (e.Key)
		{
			case Key.OemPlus or Key.Add:
				this.ZoomBy(1.2);
				e.Handled = true;
				break;
			case Key.OemMinus or Key.Subtract:
				this.ZoomBy(1 / 1.2);
				e.Handled = true;
				break;
			case Key.D0 or Key.NumPad0:
				this.fitPending = true;
				this.InvalidateVisual();
				e.Handled = true;
				break;
			case Key.Escape:
				this.SelectedId = null;
				e.Handled = true;
				break;
		}
	}
}
