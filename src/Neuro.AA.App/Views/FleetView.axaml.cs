using Avalonia.Controls;
using Avalonia.Input;
using Neuro.AA.App.ViewModels;

namespace Neuro.AA.App.Views;

public partial class FleetView : UserControl
{
	public FleetView()
	{
		this.InitializeComponent();
		this.AddHandler(DoubleTappedEvent, this.OnDoubleTapped);
	}

	private void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (this.DataContext is FleetViewModel vm && vm.SelectedRow is not null && e.Source is Avalonia.Visual v &&
			Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<ListBoxItem>(v, true) is not null)
		{
			vm.OpenEventsCommand.Execute(vm.SelectedRow);
		}
	}
}
