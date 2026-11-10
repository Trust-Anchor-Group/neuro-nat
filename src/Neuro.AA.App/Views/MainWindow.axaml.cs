using Avalonia;
using Avalonia.Controls;

namespace Neuro.AA.App.Views;

public partial class MainWindow : Window
{
	/// <summary>
	/// Width steps the views lay out for. The root gets a class "maxwN" for each step the window
	/// is at or below, and views style themselves with ".maxwN Selector". (Container queries did
	/// the same, but cost every list row a width subscription, which made scrolling slow.)
	/// </summary>
	private static readonly int[] steps = [780, 860, 900, 960, 1000, 1100, 1180, 1280];

	public MainWindow()
	{
		this.InitializeComponent();
		this.Root.SizeChanged += (_, e) => this.ApplyWidth(e.NewSize.Width);
	}

	private void ApplyWidth(double width)
	{
		foreach (int step in steps)
			this.Root.Classes.Set("maxw" + step, width <= step);
	}
}
