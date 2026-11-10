using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.AA.App.ViewModels;
using Neuro.AA.App.Views;
using Neuro.AA.Engine;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.Tests;

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
		string store = Path.Combine(Path.GetTempPath(), "NeuroAA.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		MainWindowViewModel vm = new(manager, new FleetService(new MonitorService(manager, new EventHub())));
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Accounts;
		vm.IsAddFormOpen = true;
		vm.NewHost = "lab.tagroot.io";
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
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.AA.slnx")))
			d = d.Parent;
		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		frame!.Save(Path.Combine(folder, $"accounts-create-{theme.ToLowerInvariant()}.png"));
		window.Close();
	}
}
