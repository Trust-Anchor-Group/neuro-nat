using Avalonia.Controls;
using Avalonia.Interactivity;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

public partial class UpdatesView : UserControl
{
	public UpdatesView() => this.InitializeComponent();

	private async void Upload_Click(object? sender, RoutedEventArgs e)
	{
		if (this.DataContext is not UpdatesViewModel { Selected: { } row } vm || TopLevel.GetTopLevel(this) is not Window owner)
			return;

		PackageUploadWindow upload = new(row.Label, vm.UploadAddress);
		await upload.ShowDialog(owner);
		if (upload.Submitted && vm.Selected == row && row.IsLive && !vm.IsBusy)
			await vm.RefreshCommand.ExecuteAsync(null);
	}
}
