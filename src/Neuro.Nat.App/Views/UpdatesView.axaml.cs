using Avalonia.Controls;
using Avalonia.Interactivity;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Engine.Software;

namespace Neuro.Nat.App.Views;

public partial class UpdatesView : UserControl
{
	public UpdatesView() => this.InitializeComponent();

	private async void Install_Click(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button { DataContext: SoftwarePackage { CanInstall: true } package } ||
			this.DataContext is not UpdatesViewModel { Selected: { } row, CanInstallOnSelected: true } vm ||
			TopLevel.GetTopLevel(this) is not Window owner)
			return;

		PackageInstallWindow dialog = new(package.FileName, row.NeuronJid);
		var result = await dialog.ShowDialog<(string Key, bool Backup)?>(owner);
		if (result is { } choice)
			await vm.InstallPackageAsync(row, package.FileName, choice.Key, choice.Backup);
	}

	private void Upload_Click(object? sender, RoutedEventArgs e)
	{
		if (this.DataContext is not UpdatesViewModel { Selected: { } row } vm || TopLevel.GetTopLevel(this) is not Window owner)
			return;

		PackageUploadWindow upload = new(row, vm);
		upload.Closed += async (_, _) =>
		{
			if (upload.Submitted && vm.Selected == row && row.IsLive && !vm.IsBusy)
				await vm.RefreshCommand.ExecuteAsync(null);
		};
		// The operator can also approve the signature request from the main window's Inbox.
		upload.Show(owner);
	}
}
