using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;

namespace Neuro.Nat.App;

public partial class App : Application
{
	/// <summary>Set by the desktop host before the app starts.</summary>
	public static AppServices? Services { get; set; }

	/// <summary>Brings the window back, from the tray or when NAT is started a second time. Any thread.</summary>
	public static void ShowMainWindow()
	{
		// Before the window exists there is nothing to bring forward, and touching the dispatcher
		// from another thread that early would tie it to the wrong thread.
		if (showMainWindow is not null)
			Avalonia.Threading.Dispatcher.UIThread.Post(() => showMainWindow?.Invoke());
	}

	private static volatile Action? showMainWindow;

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		AppLog.Info($"Framework initialized; lifetime: {this.ApplicationLifetime?.GetType().Name ?? "none"}.");

		if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			try
			{
				AppServices services = Services ?? throw new InvalidOperationException("App.Services must be set by the host.");
				MainWindowViewModel vm = new(services.Accounts, services.Fleet, services.Chat, services.Identities, services.Devices, services.PubSub);
				MainWindow window = new() { DataContext = vm };
				UiHost.Attach(window);

				// Started with Windows: watch from the tray, and show the window only when asked.
				// The lifetime shows its main window at start, so it gets one only when shown.
				if (!services.StartHidden)
					desktop.MainWindow = window;

				// Noticing things: pop-ups, sounds and counts, in the window and from the tray.
				Engine.Alerts.JsonAlertSettingsStore alertStore = new(Path.Combine(services.DataFolder, "alerts.json"));
				Engine.Alerts.AlertService alerts = new(services.Monitors, alertStore.Load())
				{
					AccountLabel = id => services.Accounts.Sessions.FirstOrDefault(s => s.Profile.Id == id)?.Profile.Label,
				};
				// Messages, invitations, questions and requests pop up the same way.
				alerts.Watch(services.Chat);
				alerts.Watch(services.Devices);
				alerts.Watch(services.Identities);
				vm.Settings.Attach(alerts, alertStore, new JsonUiSettingsStore(Path.Combine(services.DataFolder, "ui.json")), services.Autostart);

				// Short motion where something changes, unless the system or Settings says less.
				bool systemAllowsMotion = Controls.Motion.SystemAllows();
				Controls.Motion.Set(systemAllowsMotion && !vm.Settings.LessMotion);
				vm.Settings.Changed += (_, _) => Controls.Motion.Set(systemAllowsMotion && !vm.Settings.LessMotion);

				// Windows of their own, where they were, and the Wall view.
				ShellWindows.Attach(window, vm, () => new MainWindowViewModel(services.Accounts, services.Fleet, services.Chat, services.Identities, services.Devices, services.PubSub, primary: vm));

				Watch.WatchAgent watch = new(desktop, window, vm, alerts, services.Updates);
				showMainWindow = watch.ShowWindow;
				desktop.ShutdownRequested += async (_, _) =>
				{
					watch.Dispose();
					alerts.Dispose();

					// An answer that could still be taken back is final now.
					try
					{
						await vm.Inbox.CommitAsync();
					}
					catch (Exception ex)
					{
						AppLog.Error("Could not send the last answer from the Inbox.", ex);
					}

					// Save the Attention list first: it is quick, and the rest may outlive the process.
					await services.Monitors.Attention.DisposeAsync();
					await services.Identities.DisposeAsync();
					await services.Devices.DisposeAsync();
					await services.PubSub.DisposeAsync();
					await services.Chat.DisposeAsync();
					await vm.Sources.StopSniffersAsync();
					foreach (MainWindowViewModel other in ShellWindows.Others.Distinct())
						await ShellWindows.LeaveAsync(other);
					await services.Fleet.DisposeAsync();
					await services.Monitors.DisposeAsync();
					await services.Accounts.DisposeAsync();

					// Last: everything above may still write keys or counters.
					await Engine.EngineRuntime.ShutdownAsync();
				};
				AppLog.Info("Main window created.");

				_ = StartAsync(vm);
			}
			catch (Exception ex)
			{
				AppLog.Error("Could not create the main window.", ex);
				throw;
			}
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static async Task StartAsync(MainWindowViewModel vm)
	{
		try
		{
			await vm.StartAsync();
			AppLog.Info($"Accounts loaded: {vm.Accounts.Count}.");
		}
		catch (Exception ex)
		{
			AppLog.Error("Starting the accounts failed.", ex);
		}
	}
}
