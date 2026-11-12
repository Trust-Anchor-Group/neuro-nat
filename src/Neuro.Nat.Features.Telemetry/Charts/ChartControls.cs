using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Neuro.Nat.Features.Telemetry.Charts;

/// <summary>
/// What NAT's charts share: the data, the colours (tokens only, looked up for the look in use),
/// text, the hatch over numbers that can still change, and the box that shows values on hover.
/// </summary>
public abstract class ChartBase : Control
{
	public static readonly StyledProperty<ChartData?> DataProperty = AvaloniaProperty.Register<ChartBase, ChartData?>(nameof(Data));

	protected const double TextSize = 11;

	static ChartBase()
	{
		AffectsRender<ChartBase>(DataProperty);
		AffectsMeasure<ChartBase>(DataProperty);
	}

	protected ChartBase()
	{
		this.ActualThemeVariantChanged += (_, _) => this.InvalidateVisual();
		this.ClipToBounds = false;
	}

	public ChartData? Data
	{
		get => this.GetValue(DataProperty);
		set => this.SetValue(DataProperty, value);
	}

	/// <summary>The point or row under the pointer, or -1.</summary>
	protected int Hover { get; private set; } = -1;

	protected Point Pointer { get; private set; }

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		this.Pointer = e.GetPosition(this);
		int i = this.HitTest(this.Pointer);
		if (i != this.Hover)
		{
			this.Hover = i;
			this.InvalidateVisual();
		}
	}

	protected override void OnPointerExited(PointerEventArgs e)
	{
		base.OnPointerExited(e);
		this.Hover = -1;
		this.InvalidateVisual();
	}

	/// <summary>Which point or row is at a place, or -1.</summary>
	protected abstract int HitTest(Point p);

	/// <summary>Shows the values of a point, for tests and for the keyboard.</summary>
	public void ShowValuesAt(int index)
	{
		this.Hover = index;
		this.Pointer = new Point(this.Bounds.Width / 2, 20);
		this.InvalidateVisual();
	}

	// ----- Tokens -----

	protected IBrush Brush(string key)
		=> this.TryFindResource(key, this.ActualThemeVariant, out object? o) && o is IBrush b ? b : Brushes.Transparent;

	protected IBrush SlotBrush(int slot) => this.Brush(slot is >= 1 and <= 6 ? "Chart." + slot.ToString(CultureInfo.InvariantCulture) : "Chart.Neutral");

	private Typeface? typeface;

	protected Typeface Face(FontWeight weight = FontWeight.Normal)
	{
		FontFamily family = this.TryFindResource("Font.UI", out object? f) && f is FontFamily ff ? ff : FontFamily.Default;
		return weight == FontWeight.Normal ? this.typeface ??= new Typeface(family) : new Typeface(family, FontStyle.Normal, weight);
	}

	protected FormattedText Text(string text, IBrush brush, double size = TextSize, FontWeight weight = FontWeight.Normal)
		=> new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, this.Face(weight), size, brush);

	// ----- Shared marks -----

	/// <summary>Thin diagonal lines over the days whose numbers can still change.</summary>
	protected void Hatch(DrawingContext ctx, Rect r)
	{
		if (r.Width <= 0 || r.Height <= 0)
			return;

		ctx.FillRectangle(this.Brush("Chart.Settling"), r);
		Pen pen = new(this.Brush("Chart.Hatch"), 1);
		using (ctx.PushClip(r))
		{
			for (double x = r.Left - r.Height; x < r.Right; x += 6)
				ctx.DrawLine(pen, new Point(x, r.Bottom), new Point(x + r.Height, r.Top));
		}
	}

	/// <summary>The box with a point's values, near the pointer and inside the chart.</summary>
	protected void ValueBox(DrawingContext ctx, string title, IReadOnlyList<(IBrush? Swatch, string Name, string Value)> rows, string? note)
	{
		IBrush ink = this.Brush("Ink.Primary"), soft = this.Brush("Ink.Secondary");
		FormattedText head = this.Text(title, ink, 12, FontWeight.SemiBold);
		List<(IBrush? Swatch, FormattedText Name, FormattedText Value)> lines = [.. rows.Select(r => (r.Swatch, this.Text(r.Name, soft, 12), this.Text(r.Value, ink, 12, FontWeight.SemiBold)))];
		FormattedText? foot = note is null ? null : this.Text(note, soft, TextSize);

		double nameW = lines.Count == 0 ? 0 : lines.Max(l => l.Name.Width + (l.Swatch is null ? 0 : 14));
		double valueW = lines.Count == 0 ? 0 : lines.Max(l => l.Value.Width);
		double w = Math.Max(head.Width, Math.Max(nameW + 16 + valueW, foot?.Width ?? 0)) + 20;
		double h = 10 + head.Height + 4 + lines.Count * 18 + (foot is null ? 0 : foot.Height + 4) + 8;

		double x = this.Pointer.X + 14;
		if (x + w > this.Bounds.Width)
			x = this.Pointer.X - 14 - w;
		x = Math.Max(0, x);
		double y = Math.Clamp(this.Pointer.Y - h / 2, 0, Math.Max(0, this.Bounds.Height - h));

		Rect box = new(x, y, w, h);
		ctx.DrawRectangle(this.Brush("Surface.Raised"), new Pen(this.Brush("Line.Strong"), 1), box, 6, 6);
		double ty = y + 8;
		ctx.DrawText(head, new Point(x + 10, ty));
		ty += head.Height + 4;
		foreach ((IBrush? swatch, FormattedText name, FormattedText value) in lines)
		{
			double nx = x + 10;
			if (swatch is not null)
			{
				ctx.DrawRectangle(swatch, null, new Rect(nx, ty + 4, 8, 8), 2, 2);
				nx += 14;
			}

			ctx.DrawText(name, new Point(nx, ty));
			ctx.DrawText(value, new Point(x + w - 10 - value.Width, ty));
			ty += 18;
		}

		if (foot is not null)
			ctx.DrawText(foot, new Point(x + 10, ty + 2));
	}

	/// <summary>Round steps for an axis from lo to hi.</summary>
	protected static (double Lo, double Hi, double Step) Ticks(double lo, double hi, int count = 4)
	{
		if (hi <= lo)
			hi = lo + 1;
		double raw = (hi - lo) / count;
		double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
		double step = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * mag).First(s => s >= raw);
		return (Math.Floor(lo / step) * step, Math.Ceiling(hi / step) * step, step);
	}
}

/// <summary>Lines or bars over time (a line, bars, or stacked bars), with an axis, hover and hatching.</summary>
public sealed class TimeChart : ChartBase
{
	private const double Left = 44, Bottom = 20, Top = 8, Right = 8;

	static TimeChart()
	{
		HeightProperty.OverrideDefaultValue<TimeChart>(200);
	}

	private Rect Plot => new(Left, Top, Math.Max(10, this.Bounds.Width - Left - Right), Math.Max(10, this.Bounds.Height - Top - Bottom));

	private double Step(ChartData d) => d.Kind == ChartKind.Line ? this.Plot.Width / Math.Max(1, d.Count - 1) : this.Plot.Width / Math.Max(1, d.Count);

	private double X(ChartData d, int i) => d.Kind == ChartKind.Line ? this.Plot.Left + i * this.Step(d) : this.Plot.Left + (i + 0.5) * this.Step(d);

	protected override int HitTest(Point p)
	{
		if (this.Data is not ChartData d || d.Count == 0 || !this.Plot.Inflate(new Thickness(0, 0, 0, Bottom)).Contains(p))
			return -1;
		int i = d.Kind == ChartKind.Line ? (int)Math.Round((p.X - this.Plot.Left) / this.Step(d)) : (int)Math.Floor((p.X - this.Plot.Left) / this.Step(d));
		return Math.Clamp(i, 0, d.Count - 1);
	}

	private (double Lo, double Hi, double Step) Scale(ChartData d)
	{
		IEnumerable<double> values = d.Kind == ChartKind.Stacked
			? Enumerable.Range(0, d.Count).Select(i => d.Series.Sum(s => i < s.Values.Count ? s.Values[i] ?? 0 : 0))
			: d.Series.SelectMany(s => s.Values).OfType<double>();
		List<double> all = [.. values];
		double hi = all.Count == 0 ? 1 : all.Max();
		double lo = 0;
		if (d.Percent && d.Kind == ChartKind.Line && all.Count > 0)
		{
			lo = Math.Max(0, Math.Floor((all.Min() - 2) / 10) * 10);
			hi = Math.Min(100, Math.Max(lo + 10, Math.Ceiling((hi + 1) / 10) * 10));
			return (lo, hi, hi - lo <= 20 ? 5 : 10);
		}

		return Ticks(0, Math.Max(hi, d.Percent ? 10 : 4));
	}

	public override void Render(DrawingContext ctx)
	{
		if (this.Data is not ChartData d || d.Count == 0 || this.Bounds.Width < Left + Right + 20 || this.Bounds.Height < Top + Bottom + 20)
			return;

		Rect plot = this.Plot;
		(double lo, double hi, double step) = this.Scale(d);
		double Y(double v) => plot.Bottom - (v - lo) / (hi - lo) * plot.Height;

		// Days that can still change.
		if (d.SettleFrom >= 0)
		{
			double sx = d.Kind == ChartKind.Line ? (d.SettleFrom == 0 ? plot.Left : this.X(d, d.SettleFrom) - this.Step(d) / 2) : plot.Left + d.SettleFrom * this.Step(d);
			this.Hatch(ctx, new Rect(sx, plot.Top, plot.Right - sx, plot.Height));
			FormattedText may = this.Text("May change", this.Brush("Ink.Tertiary"));
			if (plot.Right - sx > may.Width + 8)
				ctx.DrawText(may, new Point(plot.Right - may.Width - 4, plot.Top + 2));
		}

		// Grid and the axis on the left.
		Pen grid = new(this.Brush("Line"), 1);
		IBrush axis = this.Brush("Ink.Tertiary");
		for (double v = lo; v <= hi + step / 1000; v += step)
		{
			double y = Math.Round(Y(v)) + 0.5;
			ctx.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
			FormattedText t = this.Text(d.Percent ? v.ToString("0", CultureInfo.InvariantCulture) + "%" : ChartData.Short(v), axis);
			ctx.DrawText(t, new Point(plot.Left - 6 - t.Width, y - t.Height / 2));
		}

		// Dates under it, as many as fit.
		int every = Math.Max(1, (int)Math.Ceiling(56 / Math.Max(1, this.Step(d))));
		for (int i = 0; i < d.Count; i += every)
		{
			FormattedText t = this.Text(d.Labels[i], axis);
			double x = Math.Max(plot.Left - 4, Math.Min(this.X(d, i) - t.Width / 2, this.Bounds.Width - t.Width));
			ctx.DrawText(t, new Point(x, plot.Bottom + 4));
		}

		if (d.Kind == ChartKind.Line)
			this.DrawLines(ctx, d, Y);
		else
			this.DrawBars(ctx, d, Y, lo);

		if (this.Hover >= 0 && this.Hover < d.Count)
		{
			double x = this.X(d, this.Hover);
			ctx.DrawLine(new Pen(this.Brush("Line.Strong"), 1), new Point(Math.Round(x) + 0.5, plot.Top), new Point(Math.Round(x) + 0.5, plot.Bottom));
			int i = this.Hover;
			List<(IBrush?, string, string)> rows = [.. d.Series.Select(s => ((IBrush?)this.SlotBrush(s.Slot), s.Name, d.Format(i < s.Values.Count ? s.Values[i] : null)))];
			if (d.Kind == ChartKind.Stacked && d.Series.Count > 1)
				rows.Add((null, "Total", d.Format(d.Series.Sum(s => i < s.Values.Count ? s.Values[i] ?? 0 : 0))));
			this.ValueBox(ctx, d.TipOf(i), rows, d.Settling(i) ? "May still change" : null);
		}
	}

	private void DrawLines(DrawingContext ctx, ChartData d, Func<double, double> y)
	{
		foreach (ChartSeries s in d.Series)
		{
			Pen pen = new(this.SlotBrush(s.Slot), 2, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
			StreamGeometry g = new();
			using (StreamGeometryContext c = g.Open())
			{
				bool open = false;
				for (int i = 0; i < s.Values.Count && i < d.Count; i++)
				{
					if (s.Values[i] is not double v)
					{
						if (open)
							c.EndFigure(false);
						open = false;
						continue;
					}

					Point p = new(this.X(d, i), y(v));
					if (!open)
						c.BeginFigure(p, false);
					else
						c.LineTo(p);
					open = true;
				}

				if (open)
					c.EndFigure(false);
			}

			ctx.DrawGeometry(null, pen, g);

			int last = s.Values.Count - 1;
			while (last >= 0 && s.Values[last] is null)
				last--;
			if (last >= 0 && last < d.Count)
				ctx.DrawEllipse(this.SlotBrush(s.Slot), null, new Point(this.X(d, last), y(s.Values[last]!.Value)), 3, 3);

			if (this.Hover >= 0 && this.Hover < s.Values.Count && s.Values[this.Hover] is double hv)
				ctx.DrawEllipse(this.SlotBrush(s.Slot), new Pen(this.Brush("Surface.Panel"), 2), new Point(this.X(d, this.Hover), y(hv)), 4.5, 4.5);
		}
	}

	private void DrawBars(DrawingContext ctx, ChartData d, Func<double, double> y, double lo)
	{
		double step = this.Step(d);
		double w = Math.Max(1, Math.Min(28, step - Math.Max(2, step * 0.25)));
		for (int i = 0; i < d.Count; i++)
		{
			double x = this.X(d, i) - w / 2;
			double baseY = y(lo);
			IEnumerable<ChartSeries> layers = d.Kind == ChartKind.Stacked ? d.Series : d.Series.Take(1);
			bool first = true;
			foreach (ChartSeries s in layers)
			{
				double v = i < s.Values.Count ? s.Values[i] ?? 0 : 0;
				if (v <= 0)
					continue;
				double top = baseY - (y(lo) - y(lo + v));
				double h = baseY - top - (first ? 0 : 2);
				if (h > 0.5)
				{
					IBrush fill = this.SlotBrush(s.Slot);
					ctx.DrawRectangle(fill, null, new Rect(x, top, w, h), 1.5, 1.5);
				}

				baseY = top;
				first = false;
			}

			if (i == this.Hover)
				ctx.DrawRectangle(null, new Pen(this.Brush("Ink.Secondary"), 1), new Rect(x - 1.5, baseY - 1.5, w + 3, y(lo) - baseY + 1.5), 2, 2);
		}
	}
}

/// <summary>One bar per group across, with its name on the left and its number on the right.</summary>
public sealed class AcrossChart : ChartBase
{
	private const double RowHeight = 26;

	private double LabelWidth => Math.Min(this.Bounds.Width * 0.36, 180);

	private const double ValueWidth = 96;

	protected override Size MeasureOverride(Size availableSize)
		=> new(0, this.Data is ChartData d ? Math.Max(RowHeight, d.Count * RowHeight) : 0);

	protected override int HitTest(Point p)
		=> this.Data is ChartData d && p.Y >= 0 ? Math.Min(d.Count - 1, (int)(p.Y / RowHeight)) : -1;

	public override void Render(DrawingContext ctx)
	{
		if (this.Data is not ChartData d || d.Count == 0 || d.Series.Count == 0)
			return;

		ChartSeries s = d.Series[0];
		double max = d.Percent ? 100 : Math.Max(1, s.Values.OfType<double>().DefaultIfEmpty(0).Max());
		double barLeft = this.LabelWidth + 10, barWidth = Math.Max(20, this.Bounds.Width - barLeft - ValueWidth - 10);
		IBrush ink = this.Brush("Ink.Primary"), soft = this.Brush("Ink.Tertiary"), track = this.Brush("Surface.Hover");

		for (int i = 0; i < d.Count; i++)
		{
			double y = i * RowHeight;
			if (i == this.Hover)
				ctx.FillRectangle(this.Brush("Surface.Hover"), new Rect(0, y, this.Bounds.Width, RowHeight));

			FormattedText label = this.Text(d.Labels[i], ink, 12);
			label.MaxTextWidth = this.LabelWidth;
			label.MaxLineCount = 1;
			label.Trimming = TextTrimming.CharacterEllipsis;
			ctx.DrawText(label, new Point(0, y + (RowHeight - label.Height) / 2));

			ctx.DrawRectangle(track, null, new Rect(barLeft, y + 8, barWidth, RowHeight - 16), 2, 2);
			if (i < s.Values.Count && s.Values[i] is double v && v > 0)
				ctx.DrawRectangle(this.SlotBrush(d.Labels[i] == "Other" ? 0 : s.Slot), null, new Rect(barLeft, y + 7, Math.Max(2, barWidth * v / max), RowHeight - 14), 2, 2);

			string text = d.Format(i < s.Values.Count ? s.Values[i] : null);
			FormattedText value = this.Text(text, ink, 12, FontWeight.SemiBold);
			if (s.OutOf is { } of && i < of.Count && of[i] is double n)
			{
				FormattedText o = this.Text("of " + ChartData.Short(n), soft);
				ctx.DrawText(o, new Point(this.Bounds.Width - o.Width, y + (RowHeight - o.Height) / 2));
				ctx.DrawText(value, new Point(this.Bounds.Width - o.Width - 6 - value.Width, y + (RowHeight - value.Height) / 2));
			}
			else
				ctx.DrawText(value, new Point(this.Bounds.Width - value.Width, y + (RowHeight - value.Height) / 2));
		}

		if (d.Mark is double mark)
		{
			double x = Math.Round(barLeft + barWidth * mark / max) + 0.5;
			ctx.DrawLine(new Pen(this.Brush("Ink.Secondary"), 1, new DashStyle([3, 2], 0)), new Point(x, 2), new Point(x, d.Count * RowHeight - 2));
		}

		if (this.Hover >= 0 && this.Hover < d.Count)
		{
			int i = this.Hover;
			List<(IBrush?, string, string)> rows = [(this.SlotBrush(s.Slot), s.Name, d.Format(i < s.Values.Count ? s.Values[i] : null))];
			if (s.OutOf is { } of && i < of.Count && of[i] is double n)
				rows.Add((null, "Out of", ChartData.Fmt(n, false)));
			if (d.Mark is double m)
				rows.Add((null, d.MarkName ?? "Mark", d.Format(m)));
			this.ValueBox(ctx, d.Labels[i], rows, null);
		}
	}
}

/// <summary>A small line of a key number's days, without an axis.</summary>
public sealed class TrendLine : Control
{
	public static readonly StyledProperty<IReadOnlyList<double?>?> ValuesProperty = AvaloniaProperty.Register<TrendLine, IReadOnlyList<double?>?>(nameof(Values));

	static TrendLine()
	{
		AffectsRender<TrendLine>(ValuesProperty);
		HeightProperty.OverrideDefaultValue<TrendLine>(30);
	}

	public TrendLine() => this.ActualThemeVariantChanged += (_, _) => this.InvalidateVisual();

	public IReadOnlyList<double?>? Values
	{
		get => this.GetValue(ValuesProperty);
		set => this.SetValue(ValuesProperty, value);
	}

	public override void Render(DrawingContext ctx)
	{
		if (this.Values is not { Count: > 1 } values || !this.TryFindResource("Chart.1", this.ActualThemeVariant, out object? o) || o is not IBrush brush)
			return;

		List<double> known = [.. values.OfType<double>()];
		if (known.Count < 2)
			return;

		double lo = known.Min(), hi = known.Max();
		if (hi - lo < 0.001)
			(lo, hi) = (lo - 1, hi + 1);
		double w = this.Bounds.Width, h = this.Bounds.Height - 4, step = w / (values.Count - 1);
		StreamGeometry g = new();
		Point? last = null;
		using (StreamGeometryContext c = g.Open())
		{
			bool open = false;
			for (int i = 0; i < values.Count; i++)
			{
				if (values[i] is not double v)
					continue;
				Point p = new(i * step, 2 + h - (v - lo) / (hi - lo) * h);
				if (!open)
					c.BeginFigure(p, false);
				else
					c.LineTo(p);
				open = true;
				last = p;
			}

			if (open)
				c.EndFigure(false);
		}

		ctx.DrawGeometry(null, new Pen(brush, 1.5, lineJoin: PenLineJoin.Round), g);
		if (last is Point lp)
			ctx.DrawEllipse(brush, null, lp, 2.25, 2.25);
	}
}

/// <summary>A chart's numbers as a table, drawn the same way in both looks.</summary>
public sealed class TableChart : ChartBase
{
	private const double RowHeight = 22;

	protected override Size MeasureOverride(Size availableSize)
		=> new(0, this.Data is ChartData d ? (d.Count + 1) * RowHeight + 2 : 0);

	protected override int HitTest(Point p) => -1;

	/// <summary>The first column's name, such as "Day".</summary>
	public static readonly StyledProperty<string> FirstProperty = AvaloniaProperty.Register<TableChart, string>(nameof(First), "Day");

	public string First
	{
		get => this.GetValue(FirstProperty);
		set => this.SetValue(FirstProperty, value);
	}

	public override void Render(DrawingContext ctx)
	{
		if (this.Data is not ChartData d)
			return;

		ChartTable t = d.ToTable(this.First);
		int cols = t.Headers.Count;
		double first = Math.Min(200, this.Bounds.Width * 0.34);
		double rest = cols > 1 ? (this.Bounds.Width - first) / (cols - 1) : 0;
		IBrush head = this.Brush("Ink.Secondary"), ink = this.Brush("Ink.Primary");
		Pen line = new(this.Brush("Line"), 1);

		void Row(IReadOnlyList<string> cells, double y, IBrush brush, FontWeight weight)
		{
			for (int c = 0; c < cells.Count && c < cols; c++)
			{
				FormattedText ft = this.Text(cells[c], brush, 12, weight);
				double w = c == 0 ? first - 8 : rest - 8;
				ft.MaxTextWidth = Math.Max(10, w);
				ft.MaxLineCount = 1;
				ft.Trimming = TextTrimming.CharacterEllipsis;

				// Names on the left, numbers to the right of their column.
				double x = c == 0 ? 0 : first + c * rest - Math.Min(ft.Width, w);
				ctx.DrawText(ft, new Point(x, y + (RowHeight - ft.Height) / 2));
			}
		}

		Row(t.Headers, 0, head, FontWeight.SemiBold);
		ctx.DrawLine(line, new Point(0, RowHeight - 0.5), new Point(this.Bounds.Width, RowHeight - 0.5));
		for (int r = 0; r < t.Rows.Count; r++)
			Row(t.Rows[r], (r + 1) * RowHeight, ink, FontWeight.Normal);
	}
}

/// <summary>A small square in a series' colour, for legends. Slot -1 is the hatch of numbers that can still change.</summary>
public sealed class Swatch : Control
{
	public static readonly StyledProperty<int> SlotProperty = AvaloniaProperty.Register<Swatch, int>(nameof(Slot), 1);

	static Swatch()
	{
		AffectsRender<Swatch>(SlotProperty);
		WidthProperty.OverrideDefaultValue<Swatch>(10);
		HeightProperty.OverrideDefaultValue<Swatch>(10);
	}

	public Swatch() => this.ActualThemeVariantChanged += (_, _) => this.InvalidateVisual();

	public int Slot
	{
		get => this.GetValue(SlotProperty);
		set => this.SetValue(SlotProperty, value);
	}

	private IBrush Brush(string key) => this.TryFindResource(key, this.ActualThemeVariant, out object? o) && o is IBrush b ? b : Brushes.Transparent;

	public override void Render(DrawingContext ctx)
	{
		Rect r = new(this.Bounds.Size);
		if (this.Slot < 0)
		{
			ctx.DrawRectangle(this.Brush("Chart.Settling"), new Pen(this.Brush("Chart.Hatch"), 1), r.Deflate(0.5), 2, 2);
			Pen pen = new(this.Brush("Chart.Hatch"), 1);
			using (ctx.PushClip(r))
			{
				for (double x = -r.Height; x < r.Width; x += 4)
					ctx.DrawLine(pen, new Point(x, r.Height), new Point(x + r.Height, 0));
			}

			return;
		}

		ctx.DrawRectangle(this.Brush(this.Slot is >= 1 and <= 6 ? "Chart." + this.Slot.ToString(CultureInfo.InvariantCulture) : "Chart.Neutral"), null, r, 2, 2);
	}
}
