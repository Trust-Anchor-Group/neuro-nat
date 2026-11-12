using Avalonia;
using Avalonia.Controls;
using Neuro.Nat.Features.Telemetry.ViewModels;

namespace Neuro.Nat.Features.Telemetry.Views;

public partial class DashboardView : UserControl
{
	public DashboardView() => this.InitializeComponent();

	/// <summary>On screen: the dashboard reads what was kept and asks when an hour has passed.</summary>
	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		(this.DataContext as DashboardViewModel)?.Shown();
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (TopLevel.GetTopLevel(this) is not null)
			(this.DataContext as DashboardViewModel)?.Shown();
	}
}
