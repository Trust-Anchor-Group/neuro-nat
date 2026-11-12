using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Neuro.Nat.App.Controls;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

/// <summary>
/// The big screen: one page at a time in large text, with no controls. It is drawn for a screen
/// 1280 wide and scaled to the one it is on. A page slides in as it turns, unless motion is off.
/// </summary>
public partial class BigScreenView : UserControl
{
	private const double DrawnFor = 1280;
	private BigScreenViewModel? vm;

	public BigScreenView()
	{
		this.InitializeComponent();
		this.SizeChanged += (_, e) => this.Fit(e.NewSize);

		// The space for the tiles decides how many go side by side and how many to a page.
		this.Pages.SizeChanged += (_, _) => this.FitTiles();

		this.DataContextChanged += (_, _) =>
		{
			if (this.vm is not null)
				this.vm.Turned -= this.Vm_Turned;
			this.vm = this.DataContext as BigScreenViewModel;
			if (this.vm is not null)
				this.vm.Turned += this.Vm_Turned;
			this.FitTiles();
		};
	}

	/// <summary>The Neurons page is the pages' space less the latest problems beside the tiles.</summary>
	private void FitTiles()
	{
		Size pages = this.Pages.Bounds.Size;
		if (this.vm is not null && pages.Width > 0 && pages.Height > 0)
			this.vm.Fit(new Size(pages.Width - this.LatestPanel.Width - this.LatestPanel.Margin.Left, pages.Height));
	}

	/// <summary>How much larger than drawn the big screen is on this screen.</summary>
	public double Factor { get; private set; } = 1;

	/// <summary>How many times a page slid in. It stays 0 with motion off.</summary>
	public int Slides { get; private set; }

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

	private void Vm_Turned(object? sender, int direction)
	{
		if (!Motion.Enabled || !this.IsVisible)
			return;

		this.Slides++;
		Animation slide = new()
		{
			Duration = TimeSpan.FromMilliseconds(320),
			Easing = new CubicEaseOut(),
			Children =
			{
				new KeyFrame
				{
					Cue = new Cue(0),
					Setters = { new Setter(TranslateTransform.XProperty, 90.0 * Math.Sign(direction)), new Setter(OpacityProperty, 0.0) },
				},
				new KeyFrame
				{
					Cue = new Cue(1),
					Setters = { new Setter(TranslateTransform.XProperty, 0.0), new Setter(OpacityProperty, 1.0) },
				},
			},
		};

		_ = slide.RunAsync(this.Turnable);
	}
}
