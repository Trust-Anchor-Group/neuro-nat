using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.Tests;

/// <summary>Renders the add-account form with "create a new account" chosen, for review.</summary>
public class AddAccountRenderTests
{
	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task RendersCreateAccountForm(string theme)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		MainWindowViewModel vm = new(manager, new FleetService(new MonitorService(manager, new EventHub())));
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Accounts;
		vm.IsAddFormOpen = true;
		vm.NewHost = "lab.example.com";
		vm.NewUserName = "kim.lindqvist";
		vm.NewPassword = "secret";
		vm.NewCreate = true;
		vm.NewApiKey = "a1b2c3d4e5";
		vm.NewApiSecret = "secret";
		vm.NewEMail = "kim@example.com";

		Assert.Equal("Create and connect", vm.AddButtonText);
		Assert.True(vm.AddAccountCommand.CanExecute(null));
		vm.NewApiSecret = string.Empty;
		Assert.False(vm.AddAccountCommand.CanExecute(null));
		vm.NewApiSecret = "secret";

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(200);
		var frame = window.CaptureRenderedFrame();
		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;
		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		frame!.Save(Path.Combine(folder, $"accounts-create-{theme.ToLowerInvariant()}.png"));
		window.Close();
	}
}
