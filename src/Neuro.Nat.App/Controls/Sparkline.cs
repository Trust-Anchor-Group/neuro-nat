using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Neuro.Nat.App.Controls;

/// <summary>
/// A small line of counts over time (oldest left). Draws in the inherited text colour,
/// with the last point marked. Empty buckets sit on the baseline.
/// </summary>
public sealed class Sparkline : Control
{
	public static readonly StyledProperty<int[]?> ValuesProperty =
		AvaloniaProperty.Register<Sparkline, int[]?>(nameof(Values));

	public static readonly StyledProperty<IBrush?> ForegroundProperty =
		TextElement.ForegroundProperty.AddOwner<Sparkline>();

	static Sparkline()
	{
		AffectsRender<Sparkline>(ValuesProperty, ForegroundProperty);
		HeightProperty.OverrideDefaultValue<Sparkline>(18);
		WidthProperty.OverrideDefaultValue<Sparkline>(72);
	}

	public int[]? Values
	{
		get => this.GetValue(ValuesProperty);
		set => this.SetValue(ValuesProperty, value);
	}

	public IBrush? Foreground
	{
		get => this.GetValue(ForegroundProperty);
		set => this.SetValue(ForegroundProperty, value);
	}

	public override void Render(DrawingContext context)
	{
		int[]? values = this.Values;
		IBrush? brush = this.Foreground;
		if (values is null || values.Length < 2 || brush is null)
			return;

		double w = this.Bounds.Width;
		double h = this.Bounds.Height - 2;
		int max = Math.Max(1, values.Max());
		double step = w / (values.Length - 1);

		StreamGeometry g = new();
		using (StreamGeometryContext ctx = g.Open())
		{
			for (int i = 0; i < values.Length; i++)
			{
				Point p = new(i * step, 1 + h - (values[i] * h / max));
				if (i == 0)
					ctx.BeginFigure(p, false);
				else
					ctx.LineTo(p);
			}
			ctx.EndFigure(false);
		}

		context.DrawGeometry(null, new Pen(brush, 1.25, lineJoin: PenLineJoin.Round), g);

		Point last = new(w, 1 + h - (values[^1] * h / max));
		context.DrawEllipse(brush, null, last, 1.75, 1.75);
	}
}
