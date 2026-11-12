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

	/// <summary>
	/// Set by the desktop host instead of <see cref="Services"/> when what NAT saved cannot be
	/// opened. The app then only asks what to do, and the host acts on the choice.
	/// </summary>
	public static DatabaseProblemViewModel? DatabaseProblem { get; set; }

	/// <summary>Brings the window back, from the tray or when NAT is started a second time. Any thread.</summary>
	public static void ShowMainWindow()
	{
		// Before the window exists there is nothing to bring forward, and touching the dispatcher
		// from another thread that early would tie it to the wrong thread.
		if (showMainWindow is not null)
			Avalonia.Threading.Dispatcher.UIThread.Post(() => showMainWindow?.Invoke());
	}

	private static volatile Action? showMainWindow;

	/// <summary>
	/// Opens what a NAT link points at, brought by a click outside NAT or by a second start. Any
	/// thread. Before the window is ready the link waits, and opens once the app is up.
	/// </summary>
	public static void OpenLink(string link)
	{
		if (!Engine.Links.NatLink.LooksLike(link) || link.Length > Engine.Links.NatLink.MaxLength)
			return;

		lock (linkLock)
		{
			if (openLink is null)
			{
				waitingLink = link;
				return;
			}
		}

		Avalonia.Threading.Dispatcher.UIThread.Post(() => openLink?.Invoke(link));
	}

	private static readonly object linkLock = new();
	private static Action<string>? openLink;
	private static string? waitingLink;

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		AppLog.Info($"Framework initialized; lifetime: {this.ApplicationLifetime?.GetType().Name ?? "none"}.");

		if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime problemDesktop && DatabaseProblem is DatabaseProblemViewModel problem)
		{
			problemDesktop.MainWindow = new DatabaseProblemWindow(problem);
			base.OnFrameworkInitializationCompleted();
			return;
		}

		if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			try
			{
				AppServices services = Services ?? throw new InvalidOperationException("App.Services must be set by the host.");
				// What the registered features show is drawn with their own templates.
				foreach (Avalonia.Controls.Templates.IDataTemplate template in services.Features.Templates())
					this.DataTemplates.Add(template);

				// Their pages on the big screen come and go with what they list.
				services.Features.FeedBigScreen(BigScreenPages.Added);

				MainWindowViewModel vm = new(services.Accounts, services.Fleet, services.Chat, services.Identities, services.Devices, services.PubSub, features: services.Features);
				vm.Compare.Store = services.NeuronCopies;
				MainWindow window = new() { DataContext = vm };
				_ = vm.Console.Runbooks.UseAsync(services.Runbooks);
				UiHost.Attach(window);

				// Started with Windows: watch from the tray, and show the window only when asked.
				// The lifetime shows its main window at start, so it gets one only when shown.
				if (!services.StartHidden)
					desktop.MainWindow = window;

				// Noticing things: pop-ups, sounds and counts, in the window and from the tray.
				Engine.Alerts.IAlertSettingsStore alertStore = services.AlertSettings;
				Engine.Alerts.AlertService alerts = new(services.Monitors, alertStore.Load())
				{
					AccountLabel = id => services.Accounts.Sessions.Count > 1 ? services.Accounts.Sessions.FirstOrDefault(s => s.Profile.Id == id)?.Profile.ShortLabel : null,
				};
				// Messages, invitations, questions and requests pop up the same way.
				alerts.Watch(services.Chat);
				alerts.Watch(services.Devices);
				alerts.Watch(services.Identities);
				vm.Settings.Attach(alerts, alertStore, services.UiSettings, services.Autostart);
				vm.Settings.StorageNote = services.KeepsData ? null : SettingsViewModel.NothingKept;

				// Short motion where something changes, unless the system or Settings says less.
				bool systemAllowsMotion = Controls.Motion.SystemAllows();
				Controls.Motion.Set(systemAllowsMotion && !vm.Settings.LessMotion);
				vm.Settings.Changed += (_, _) => Controls.Motion.Set(systemAllowsMotion && !vm.Settings.LessMotion);

				// Windows of their own, where they were, and the big screen.
				ShellWindows.Attach(window, vm, () =>
				{
					MainWindowViewModel extra = new(services.Accounts, services.Fleet, services.Chat, services.Identities, services.Devices, services.PubSub, primary: vm);
					_ = extra.Console.Runbooks.UseAsync(services.Runbooks);
					return extra;
				});

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
					await services.Features.DisposeAsync();
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

				_ = StartAsync(vm, watch, services.StartLink);
			}
			catch (Exception ex)
			{
				AppLog.Error("Could not create the main window.", ex);
				throw;
			}
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static async Task StartAsync(MainWindowViewModel vm, Watch.WatchAgent watch, string? startLink)
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

		// The app is up: links open from now on, the one NAT was started with first.
		string? waiting;
		lock (linkLock)
		{
			openLink = link =>
			{
				watch.ShowWindow();
				vm.OpenLink(link, patient: true);
			};
			waiting = waitingLink;
			waitingLink = null;
		}

		if ((waiting ?? startLink) is string first)
			openLink(first);
	}
}
