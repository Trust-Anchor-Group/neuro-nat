using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.AA.App.ViewModels;
using Neuro.AA.App.Views;
using Neuro.AA.Engine;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Chat;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.Tests;

/// <summary>
/// Drives the Console and Chat workspaces against the local test Neuron and saves frames for
/// review. A command that would change the Neuron is only taken up to its confirmation.
/// Needs the NEUROAA_TEST_* variables; does nothing without them.
/// </summary>
[Collection("Live Neuron")]
public class LiveChatTests
{
	private static async Task Until(Func<bool> condition, int timeoutMs = 30000)
	{
		long end = Environment.TickCount64 + timeoutMs;
		while (!condition())
		{
			if (Environment.TickCount64 > end)
				throw new TimeoutException("Condition not met in time.");
			Dispatcher.UIThread.RunJobs();
			await Task.Delay(50);
		}
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task ConsoleAndChat(string theme)
	{
		string? host = Environment.GetEnvironmentVariable("NEUROAA_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NEUROAA_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NEUROAA_TEST_PASSWORD");
		string? neuron = Environment.GetEnvironmentVariable("NEUROAA_TEST_NEURON");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(neuron))
			return;

		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "NeuroAA.LiveUi." + Guid.NewGuid().ToString("N"), "accounts.json");
		await using AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true, ConnectOnStartup = false },
			AccountSecret.FromPassword(password), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		await using ChatService chat = new(manager, monitors);
		MainWindowViewModel vm = new(manager, new FleetService(monitors), chat);
		await vm.StartAsync();
		await s.ConnectAsync();
		await Until(() => monitors.For(s.Profile.Id)?.Concentrator(neuron) is not null);

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();

		// Console: a command, a script, and a command that needs confirmation.
		vm.ShowConsoleCommand.Execute(null);
		ConsoleViewModel console = vm.Console;
		await Until(() => console.HasTargets && console.HasSuggestions);

		console.CommandText = "Clients";
		await console.RunCommand.ExecuteAsync(null);
		console.CommandText = "Versions";
		await console.RunCommand.ExecuteAsync(null);
		console.CommandText = "[1,2,3]*7";
		await console.RunCommand.ExecuteAsync(null);
		await Until(() => console.Runs.All(r => r.Results.All(x => !x.IsWaiting)));
		await Task.Delay(1500);

		console.CommandText = "Reb";
		Dispatcher.UIThread.RunJobs();
		Assert.Contains(console.Suggestions, x => x.Equals("reboot", StringComparison.OrdinalIgnoreCase));
		console.CommandText = "Reboot";
		await console.RunCommand.ExecuteAsync(null);
		Assert.True(console.IsConfirming);
		Assert.Equal(3, console.Runs.Count);
		await Task.Delay(300);
		Save(window, $"console-live-{theme.ToLowerInvariant()}");
		console.CancelConfirmCommand.Execute(null);
		console.CommandText = string.Empty;

		// Chat with the Neuron's own client.
		vm.ShowChatCommand.Execute(null);
		ChatViewModel chatVm = vm.Chat;
		await Until(() => chatVm.People.Any(p => string.Equals(p.BareJid, neuron, StringComparison.OrdinalIgnoreCase)));
		chatVm.SelectedPerson = chatVm.People.First(p => string.Equals(p.BareJid, neuron, StringComparison.OrdinalIgnoreCase));
		chatVm.Draft = "Hello. What is **6*7**?";
		await chatVm.SendCommand.ExecuteAsync(null);
		chatVm.Draft = "6*7";
		await chatVm.SendCommand.ExecuteAsync(null);
		await Until(() => chatVm.Messages.Count(m => !m.IsMine) >= 2, 20000);
		await Task.Delay(800);
		Save(window, $"chat-live-{theme.ToLowerInvariant()}");

		window.Close();
	}

	private static void Save(MainWindow window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);

		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.AA.slnx")))
			d = d.Parent;
		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		frame!.Save(Path.Combine(folder, name + ".png"));
	}
}
