using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Neuro.Nat.App.Controls;

/// <summary>
/// Draws a line icon authored on a 24 x 24 grid. The stroke follows the inherited text
/// colour, so icons in buttons, rail items and lists pick up hover, selected and disabled
/// states without extra styling.
/// </summary>
public sealed class LineIcon : Control
{
	public static readonly StyledProperty<Geometry?> DataProperty =
		AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

	public static readonly StyledProperty<IBrush?> ForegroundProperty =
		TextElement.ForegroundProperty.AddOwner<LineIcon>();

	public static readonly StyledProperty<double> StrokeWidthProperty =
		AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeWidth), 1.7);

	static LineIcon()
	{
		AffectsRender<LineIcon>(DataProperty, ForegroundProperty, StrokeWidthProperty);
		WidthProperty.OverrideDefaultValue<LineIcon>(16);
		HeightProperty.OverrideDefaultValue<LineIcon>(16);
	}

	public Geometry? Data
	{
		get => this.GetValue(DataProperty);
		set => this.SetValue(DataProperty, value);
	}

	public IBrush? Foreground
	{
		get => this.GetValue(ForegroundProperty);
		set => this.SetValue(ForegroundProperty, value);
	}

	/// <summary>Stroke width in grid units (the grid is 24 wide).</summary>
	public double StrokeWidth
	{
		get => this.GetValue(StrokeWidthProperty);
		set => this.SetValue(StrokeWidthProperty, value);
	}

	public override void Render(DrawingContext context)
	{
		Geometry? data = this.Data;
		IBrush? brush = this.Foreground;
		if (data is null || brush is null)
			return;

		double scale = Math.Min(this.Bounds.Width, this.Bounds.Height) / 24.0;
		if (scale <= 0)
			return;

		Pen pen = new(brush, this.StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
		using (context.PushTransform(Matrix.CreateScale(scale, scale)))
			context.DrawGeometry(null, pen, data);
	}
}
