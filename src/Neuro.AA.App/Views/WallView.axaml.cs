using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Neuro.AA.App.Views;

/// <summary>
/// The Wall view: the Neurons and their latest problems in large text, with no controls. It is
/// drawn for a screen 1280 wide and scaled to the one it is on.
/// </summary>
public partial class WallView : UserControl
{
	private const double DrawnFor = 1280;

	public WallView()
	{
		this.InitializeComponent();
		this.SizeChanged += (_, e) => this.Fit(e.NewSize);

		// The tiles share the width there is; the box around them shrinks them when they are too many to fit.
		this.TileHost.SizeChanged += (_, e) =>
		{
			if (e.NewSize.Width > 0)
				this.TileList.Width = e.NewSize.Width;
		};
	}

	/// <summary>How much larger than drawn the wall is on this screen.</summary>
	public double Factor { get; private set; } = 1;

	private void Fit(Size size)
	{
		if (size.Width <= 0)
			return;

		double factor = Math.Clamp(Math.Round(size.Width / DrawnFor, 2), 0.6, 4);
		if (factor == this.Factor)
			return;

		this.Factor = factor;
		this.Scale.LayoutTransform = factor == 1 ? null : new ScaleTransform(factor, factor);
	}
}
