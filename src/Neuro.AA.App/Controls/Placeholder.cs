using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Neuro.AA.App.Controls;

/// <summary>
/// What shows where rows are on their way: a few grey bars in the shape of the rows to come,
/// so the screen does not jump when they arrive. They breathe slowly while motion is on.
/// </summary>
public sealed class Placeholder : Control
{
	/// <summary>How many rows to hint at.</summary>
	public static readonly StyledProperty<int> RowsProperty = AvaloniaProperty.Register<Placeholder, int>(nameof(Rows), 3);

	/// <summary>The height of one row, with the space under it.</summary>
	public static readonly StyledProperty<double> RowHeightProperty = AvaloniaProperty.Register<Placeholder, double>(nameof(RowHeight), 34);

	/// <summary>The colour of the bars. The theme sets it to the line colour.</summary>
	public static readonly StyledProperty<IBrush?> BarProperty = AvaloniaProperty.Register<Placeholder, IBrush?>(nameof(Bar));

	// The bars' widths, as parts of the room there is: uneven, the way real rows are.
	private static readonly double[] widths = [0.62, 0.84, 0.48, 0.73, 0.56, 0.9];

	private DispatcherTimer? timer;
	private double phase;

	static Placeholder()
	{
		AffectsRender<Placeholder>(RowsProperty, RowHeightProperty, BarProperty);
		AffectsMeasure<Placeholder>(RowsProperty, RowHeightProperty);
		IsVisibleProperty.Changed.AddClassHandler<Placeholder>((p, _) => p.Breathe());
	}

	public int Rows
	{
		get => this.GetValue(RowsProperty);
		set => this.SetValue(RowsProperty, value);
	}

	public double RowHeight
	{
		get => this.GetValue(RowHeightProperty);
		set => this.SetValue(RowHeightProperty, value);
	}

	public IBrush? Bar
	{
		get => this.GetValue(BarProperty);
		set => this.SetValue(BarProperty, value);
	}

	protected override Size MeasureOverride(Size availableSize)
		=> new(double.IsFinite(availableSize.Width) ? Math.Min(availableSize.Width, 420) : 240, Math.Max(0, this.Rows) * this.RowHeight);

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		Motion.Changed += this.Motion_Changed;
		this.Breathe();
	}

	private void Motion_Changed(object? sender, EventArgs e) => this.Breathe();

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		Motion.Changed -= this.Motion_Changed;
		this.timer?.Stop();
	}

	/// <summary>Starts or stops the slow pulse: only while it shows, and only while motion is on.</summary>
	private void Breathe()
	{
		bool on = Motion.Enabled && this.IsVisible && TopLevel.GetTopLevel(this) is not null;
		if (!on)
		{
			this.timer?.Stop();
			this.phase = 0;
			this.InvalidateVisual();
			return;
		}

		this.timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) =>
		{
			this.phase += 0.06 * Math.PI;
			this.InvalidateVisual();
		});
		this.timer.Start();
	}

	public override void Render(DrawingContext context)
	{
		if (this.Bar is not IBrush bar)
			return;

		double width = this.Bounds.Width;
		double bars = Math.Max(8, Math.Min(12, this.RowHeight - 16));
		using (context.PushOpacity(0.75 + 0.25 * Math.Cos(this.phase)))
		{
			for (int i = 0; i < this.Rows; i++)
			{
				double y = i * this.RowHeight + (this.RowHeight - bars) / 2;
				context.DrawRectangle(bar, null, new RoundedRect(new Rect(0, y, Math.Round(width * widths[i % widths.Length]), bars), bars / 2));
			}
		}
	}
}
