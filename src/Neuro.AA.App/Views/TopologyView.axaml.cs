using Avalonia.Controls;
using Avalonia.Interactivity;
using Neuro.AA.App.Controls;
using Neuro.AA.App.ViewModels;

namespace Neuro.AA.App.Views;

public partial class TopologyView : UserControl
{
	public TopologyView()
	{
		this.InitializeComponent();
		this.Map.NodeActivated += (_, id) => (this.DataContext as TopologyViewModel)?.Activated(id);
	}

	private void ZoomIn_Click(object? sender, RoutedEventArgs e) => this.Map.ZoomBy(1.25);

	private void ZoomOut_Click(object? sender, RoutedEventArgs e) => this.Map.ZoomBy(1 / 1.25);
}
