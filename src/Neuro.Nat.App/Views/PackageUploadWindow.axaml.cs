using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Neuro.Nat.Engine.Software;

namespace Neuro.Nat.App.Views;

public partial class PackageUploadWindow : Window
{
	private string? packagePath;
	private string? signaturePath;
	private CancellationTokenSource? transfer;
	public bool Submitted { get; private set; }

	public PackageUploadWindow() => this.InitializeComponent();

	public PackageUploadWindow(string neuron, string? address) : this()
	{
		this.Title = "Upload package for " + neuron;
		this.Address.Text = address;
	}

	private async Task<string?> Choose(string title, FilePickerFileType type)
	{
		try
		{
			var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
			{
				Title = title, FileTypeFilter = [type, FilePickerFileTypes.All],
			});
			if (files.Count == 0)
				return null;
			return files[0].TryGetLocalPath() ?? throw new IOException("Choose a file on this computer.");
		}
		catch (Exception ex)
		{
			this.Status.Text = ex.Message;
			this.Status.IsVisible = true;
			return null;
		}
	}

	private async void ChoosePackage_Click(object? sender, RoutedEventArgs e)
	{
		if (await this.Choose("Choose a package", new("Packages") { Patterns = ["*.package"] }) is string path)
		{
			this.packagePath = path;
			this.PackageName.Text = Path.GetFileName(path);
		}
	}

	private async void ChooseSignature_Click(object? sender, RoutedEventArgs e)
	{
		if (await this.Choose("Choose a package signature", new("Signatures") { Patterns = ["*.xml", "*.sig"] }) is string path)
		{
			this.signaturePath = path;
			this.SignatureName.Text = Path.GetFileName(path);
			this.RemoveSignature.IsVisible = true;
		}
	}

	private void RemoveSignature_Click(object? sender, RoutedEventArgs e)
	{
		this.signaturePath = null;
		this.SignatureName.Text = "Sign with broker keys";
		this.RemoveSignature.IsVisible = false;
	}

	private async void Send_Click(object? sender, RoutedEventArgs e)
	{
		this.Status.IsVisible = true;
		if (!Uri.TryCreate(this.Address.Text?.Trim(), UriKind.Absolute, out var broker) ||
			string.IsNullOrWhiteSpace(this.User.Text) || string.IsNullOrEmpty(this.Password.Text) || this.packagePath is null)
		{
			this.Status.Text = "Enter the broker’s HTTPS address and web credentials, then choose a package.";
			return;
		}

		using CancellationTokenSource cancel = new();
		this.transfer = cancel;
		this.CloseButton.Content = "Cancel transfer";
		this.Send.IsEnabled = this.Form.IsEnabled = false;
		this.Progress.Value = 0;
		this.Progress.IsVisible = true;
		this.Status.Text = "Sending " + Path.GetFileName(this.packagePath) + " to " + broker.Authority + "…";
		try
		{
			string password = this.Password.Text;
			this.Password.Clear();
			await BrokerPackageUpload.SendAsync(broker, this.User.Text.Trim(), password, this.packagePath, this.signaturePath,
				this.Downloadable.IsChecked == true, new Progress<int>(percent => this.Progress.Value = percent), cancel.Token);
			this.Submitted = true;
			this.Status.Text = "Sent to " + broker.Authority + " for processing. Check Downloaded packages for the published file.";
		}
		catch (OperationCanceledException) when (cancel.IsCancellationRequested)
		{
			this.Status.Text = "Transfer cancelled. Check the broker before sending again.";
		}
		catch (Exception ex)
		{
			this.Status.Text = ex.Message;
		}
		finally
		{
			this.transfer = null;
			this.Send.IsVisible = !this.Submitted;
			this.Send.IsEnabled = !this.Submitted;
			this.Form.IsEnabled = !this.Submitted;
			this.Progress.IsVisible = false;
			this.CloseButton.Content = "Close";
		}
	}

	protected override void OnClosing(WindowClosingEventArgs e)
	{
		if (this.transfer is not null && !e.IsProgrammatic)
		{
			this.transfer.Cancel();
			e.Cancel = true;
		}
		base.OnClosing(e);
	}

	private void Close_Click(object? sender, RoutedEventArgs e)
	{
		if (this.transfer is not null)
			this.transfer.Cancel();
		else
			this.Close();
	}
}
