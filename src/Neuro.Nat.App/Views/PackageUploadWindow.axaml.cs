using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Engine.Software;

namespace Neuro.Nat.App.Views;

public partial class PackageUploadWindow : Window
{
	private string? packagePath;
	private string? signaturePath;
	private CancellationTokenSource? transfer;
	private readonly Guid account;
	private readonly BrokerUploadSessions? sessions;
	public bool Submitted { get; private set; }

	public PackageUploadWindow() => this.InitializeComponent();

	public PackageUploadWindow(UpdateRowViewModel row, UpdatesViewModel vm) : this()
	{
		this.Title = $"Upload package for {row.Label} ({row.AccountLabel})";
		this.account = row.AccountId;
		this.sessions = vm.UploadSessions;
		this.Address.Text = vm.UploadAddress;
		this.SignIn.SelectionChanged += (_, _) => this.ShowMethod();
		this.Address.TextChanged += (_, _) =>
		{
			this.Password.Clear();
			this.SelectSession();
		};
		this.SelectSession();
		this.Opened += async (_, _) =>
		{
			try
			{
				string[] ids = await vm.UploadLegalIdsAsync(this.account);
				this.LegalId.ItemsSource = ids;
				if (ids.Length == 1 && string.IsNullOrWhiteSpace(this.LegalId.Text))
					this.LegalId.Text = ids[0];
			}
			catch (Exception ex)
			{
				AppLog.Error("Could not list Legal IDs for package upload.", ex);
			}
		};
	}

	private void SelectSession(bool selectDefault = true)
	{
		string? identity = Uri.TryCreate(this.Address.Text?.Trim(), UriKind.Absolute, out var broker)
			? this.sessions?.SignedInAs(this.account, broker) : null;
		this.Reuse.IsVisible = identity is not null;
		this.Reuse.Content = identity is null ? "Current session" : $"Current session ({identity})";
		if (selectDefault || this.SignIn.SelectedItem == this.Reuse && identity is null)
			this.SignIn.SelectedItem = identity is null ? this.ById : this.Reuse;
	}

	private void ShowMethod()
	{
		this.PasswordFields.IsVisible = this.SignIn.SelectedItem == this.ByPassword;
		this.LegalFields.IsVisible = this.SignIn.SelectedItem == this.ById;
		if (!this.PasswordFields.IsVisible)
			this.Password.Clear();
		this.Send.Content = this.LegalFields.IsVisible ? "Request approval and upload" : "Upload";
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
		if (this.transfer is not null || this.sessions is null)
			return;
		if (!Uri.TryCreate(this.Address.Text?.Trim(), UriKind.Absolute, out var broker) || this.packagePath is null)
		{
			this.Status.Text = "Enter the broker’s HTTPS address and choose a package.";
			return;
		}

		UploadSignIn mode = this.SignIn.SelectedItem == this.Reuse ? UploadSignIn.ExistingSession :
			this.SignIn.SelectedItem == this.ByPassword ? UploadSignIn.WebAccount : UploadSignIn.LegalId;
		string identity = (mode == UploadSignIn.LegalId ? this.LegalId.Text : this.User.Text)?.Trim() ?? "";
		if (mode == UploadSignIn.LegalId && identity.Length == 0)
		{
			this.Status.Text = "Select or enter the Legal ID linked to your web admin account on this broker.";
			return;
		}
		using CancellationTokenSource cancel = new();
		this.transfer = cancel;
		this.CloseButton.Content = "Cancel transfer";
		this.Send.IsEnabled = this.Form.IsEnabled = false;
		this.Progress.Value = 0;
		this.Progress.IsVisible = true;
		string sending = $"Sending {Path.GetFileName(this.packagePath)} to {broker.Authority}…";
		this.Status.Text = mode == UploadSignIn.LegalId ? $"Approve the sign-in for {broker.Authority} in your identity app or NAT Inbox." : sending;
		try
		{
			string? password = this.Password.Text;
			this.Password.Clear();
			await this.sessions.UploadAsync(this.account, broker, mode, identity, password, this.packagePath, this.signaturePath,
				this.Downloadable.IsChecked == true, new Progress<int>(percent =>
				{
					if (this.transfer != cancel)
						return;
					this.Status.Text = sending;
					this.Progress.Value = percent;
				}), cancel.Token);
			this.Submitted = true;
			this.Status.Text = "Sent to " + broker.Authority + " for processing. Check Downloaded packages for the published file.";
		}
		catch (OperationCanceledException) when (cancel.IsCancellationRequested)
		{
			this.Status.Text = "Transfer cancelled. Check the broker before sending again.";
		}
		catch (Exception ex)
		{
			this.Status.Text = ex.Message + (mode == UploadSignIn.LegalId ? " You can also use web credentials." : "");
		}
		finally
		{
			this.transfer = null;
			this.Send.IsVisible = !this.Submitted;
			this.Send.IsEnabled = !this.Submitted;
			this.Form.IsEnabled = !this.Submitted;
			this.Progress.IsVisible = false;
			this.CloseButton.Content = "Close";
			this.SelectSession(selectDefault: this.Submitted || mode == UploadSignIn.ExistingSession);
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		this.Password.Clear();
		this.transfer?.Cancel();
		base.OnClosed(e);
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
