using Avalonia.Controls;
using Avalonia.Interactivity;
using Neuro.Nat.Engine.Software;

namespace Neuro.Nat.App.Views;

public partial class PackageInstallWindow : Window
{
	public PackageInstallWindow() => this.InitializeComponent();

	public PackageInstallWindow(string package, string neuron) : this() =>
		this.Target.Text = $"Install {package} on {neuron}";

	private void Install_Click(object? sender, RoutedEventArgs e)
	{
		string key = this.KeyBox.Text?.Trim() ?? string.Empty;
		if (!SoftwareService.IsInstallationKey(key))
		{
			this.KeyError.IsVisible = true;
			this.KeyBox.Focus();
			return;
		}

		this.Close((key, this.Backup.IsChecked == true));
	}

	protected override void OnClosed(EventArgs e)
	{
		this.KeyBox.Clear();
		base.OnClosed(e);
	}

	private void Cancel_Click(object? sender, RoutedEventArgs e) => this.Close();
}
