using System.ComponentModel;
using Avalonia.Controls;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

public partial class SourcesView : UserControl
{
	private SourcesViewModel? vm;

	public SourcesView() => this.InitializeComponent();

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);

		if (this.vm is not null)
			this.vm.PropertyChanged -= this.Vm_PropertyChanged;

		this.vm = this.DataContext as SourcesViewModel;

		if (this.vm is not null)
			this.vm.PropertyChanged += this.Vm_PropertyChanged;

		this.UpdateDock();
	}

	private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SourcesViewModel.HasSniffers))
			this.UpdateDock();
	}

	/// <summary>The sniffer dock takes room only while a sniffer runs; its height can be dragged.</summary>
	private void UpdateDock()
	{
		RowDefinition dock = this.InspectorGrid.RowDefinitions[2];
		bool show = this.vm?.HasSniffers == true;

		if (show && dock.Height.Value == 0)
			dock.Height = new GridLength(300);
		else if (!show)
			dock.Height = new GridLength(0);
	}
}
