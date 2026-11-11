using Avalonia;
using Avalonia.Controls;

namespace Neuro.AA.App.Controls;

/// <summary>
/// Stacks its children like a list, and shows only those that fit whole: where nothing can be
/// scrolled, as on the Wall view, half a row at the bottom is worse than none.
/// </summary>
public sealed class WholeStack : Panel
{
	public WholeStack() => this.ClipToBounds = true;

	protected override Size MeasureOverride(Size availableSize)
	{
		double width = 0;
		double height = 0;
		foreach (Control child in this.Children)
		{
			child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
			if (height + child.DesiredSize.Height > availableSize.Height && height > 0)
				break;

			width = Math.Max(width, child.DesiredSize.Width);
			height += child.DesiredSize.Height;
		}

		return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : width, Math.Min(height, availableSize.Height));
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		double y = 0;
		bool full = false;
		foreach (Control child in this.Children)
		{
			double h = child.DesiredSize.Height;
			if (!full && (y + h <= finalSize.Height + 0.5 || y == 0))
			{
				child.Arrange(new Rect(0, y, finalSize.Width, h));
				y += h;
			}
			else
			{
				// Out of sight, below the edge.
				full = true;
				child.Arrange(new Rect(0, finalSize.Height + 100, finalSize.Width, h));
			}
		}

		return finalSize;
	}
}
