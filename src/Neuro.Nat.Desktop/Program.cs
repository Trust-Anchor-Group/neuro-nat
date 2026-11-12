using Avalonia;
using Avalonia.Threading;
using Neuro.Nat.App;
using Neuro.Nat.Desktop.Install;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;
using Velopack;

namespace Neuro.Nat.Desktop;

internal static class Program
{
	/// <summary>Starts NAT in the tray, without its window (used by "Start with Windows").</summary>
	public const string TrayArgument = "--tray";

	[STAThread]
	public static int Main(string[] args)
	{
		// First: the installer runs NAT with special arguments at install, update and
		// uninstall, and this handles them (and exits) before anything else starts.
		// Installing turns "Start with Windows" on; uninstalling removes it.
		VelopackApp installer = VelopackApp.Build();
		if (OperatingSystem.IsWindows())
			WindowsAutostart.AddInstallerHooks(installer);
		installer.Run();

		// Started with Windows: watch from the tray without opening the window.
		bool startHidden = args.Contains(TrayArgument, StringComparer.OrdinalIgnoreCase);

		// One NAT per user: starting it again brings the running one (perhaps in the tray) forward.
		using SingleInstance? single = SingleInstance.TryClaim();
		if (single is null)
		{
			if (!startHidden)
				SingleInstance.AskRunningToShow();
			return 0;
		}

		single.Listen(App.App.ShowMainWindow);

		// Before the log file is opened: the data folder of Neuro.AA, as the app was called before,
		// moves to its new name once. Not when NAT_DATA_FOLDER names a folder to use instead.
		string? dataFolderOverride = DataFolder.Override;
		string dataFolder = dataFolderOverride ?? Path.Combine(AppDataRoot(), DataFolder.FolderName);
		DataFolderMoveResult? move = null;
		if (dataFolderOverride is null)
		{
			move = DataFolderMove.Run(Path.Combine(AppDataRoot(), DataFolderMove.OldFolderName), dataFolder);
			if (move.Outcome == DataFolderMoveOutcome.Failed)
			{
				// Nothing was changed, so the next start tries again. No empty folder, no silent empty start.
				Tell("NAT could not take over the data of Neuro.AA, most likely because Neuro.AA is still running.\n\nClose Neuro.AA (also in the tray), then start NAT again.");
				return 1;
			}
		}

		Directory.CreateDirectory(dataFolder);

		AppLog.FilePath = Path.Combine(dataFolder, "logs", DataFolder.LogFileName);
		AppLog.Info($"Starting NAT {typeof(Program).Assembly.GetName().Version} on {Environment.OSVersion}.");
		if (dataFolderOverride is not null)
			AppLog.Info($"Using the data folder from {DataFolder.OverrideVariable}: {dataFolder}.");
		if (move?.Note is string moveNote)
			AppLog.Info(moveNote);

		if (OperatingSystem.IsWindows())
		{
			try
			{
				if (WindowsAutostart.MoveOldEntry())
					AppLog.Info("\"Start with Windows\" now starts NAT instead of Neuro.AA.");
			}
			catch (Exception ex)
			{
				AppLog.Error("Could not move the \"Start with Windows\" entry of Neuro.AA.", ex);
			}
		}

		AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Unhandled exception.", e.ExceptionObject as Exception);
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			AppLog.Error("Unobserved task exception.", e.Exception);
			e.SetObserved();
		};

		try
		{
			System.Reflection.Assembly[] uiAssemblies = [typeof(App.App).Assembly, typeof(Waher.Content.Markdown.MarkdownDocument).Assembly];
			byte[]? databaseKey = null;
			try
			{
				databaseKey = DatabaseKeys.ForCurrentPlatform(dataFolder);
			}
			catch (Exception ex)
			{
				AppLog.Error("The database key could not be read; signing keys are kept for this session only.", ex);
			}

			if (databaseKey is not null)
			{
				// Legal identity signing keys and identity state, encrypted with a key from the OS vault.
				EngineRuntime.InitializeAsync(Path.Combine(dataFolder, "database"), databaseKey, uiAssemblies).GetAwaiter().GetResult();
				System.Security.Cryptography.CryptographicOperations.ZeroMemory(databaseKey);
			}
			else
				EngineRuntime.Initialize(uiAssemblies);

			AppLog.Info($"Engine runtime initialized; signing keys {(EngineRuntime.KeepsKeys ? "kept encrypted between runs" : "kept for this session only")}.");

			AccountManager accounts = new(
				new JsonAccountStore(Path.Combine(dataFolder, "accounts.json")),
				CredentialVaults.ForCurrentPlatform(dataFolder));

			AttentionInbox attention = new(new JsonAttentionStore(Path.Combine(dataFolder, "attention.json")));
			attention.Load();
			AppLog.Info($"Attention items loaded: {attention.Items.Count}.");

			MonitorService monitors = new(accounts, new EventHub(), attention);
			Engine.Chat.ChatService chat = new(accounts, monitors, new Engine.Chat.JsonChatStore(Path.Combine(dataFolder, "chat.json")));
			chat.Load();

			Engine.Identity.IdentityService identities = new(accounts, chat);

			Engine.Devices.DevicesService devices = new(accounts);
			Engine.PubSub.PubSubService pubsub = new(accounts);

			App.App.Services = new AppServices(accounts, monitors, new FleetService(monitors), chat, identities, devices, pubsub, dataFolder)
			{
				StartHidden = startHidden,
				Autostart = OperatingSystem.IsWindows() ? new WindowsAutostart() : null,
				Updates = VelopackUpdates.TryStart(dataFolder),
			};
			if (startHidden)
				AppLog.Info("Started in the tray.");

			return BuildAvaloniaApp()
				.AfterSetup(_ => Dispatcher.UIThread.UnhandledException += (_, e) =>
				{
					AppLog.Error("Unhandled UI exception.", e.Exception);
					e.Handled = true;
				})
				.StartWithClassicDesktopLifetime(args);
		}
		catch (Exception ex)
		{
			AppLog.Error("Fatal error during startup.", ex);
			throw;
		}
	}

	/// <summary>Used by the Avalonia previewer as well as by <see cref="Main"/>.</summary>
	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder.Configure<App.App>()
			.UsePlatformDetect()
			// The WinUI compositor made the first ShowWindow wait ~25 s on some starts; the
			// redirection surface (with DirectX swap chain first) shows the window at once.
			.With(new Win32PlatformOptions
			{
				CompositionMode = [Win32CompositionMode.LowLatencyDxgiSwapChain, Win32CompositionMode.RedirectionSurface],
			})
			.WithInterFont()
			.LogToTrace();

	private static string AppDataRoot()
		=> Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);

	/// <summary>Says something to the user before the app's own windows can: a message box on Windows, the console elsewhere.</summary>
	private static void Tell(string text)
	{
		if (OperatingSystem.IsWindows())
			_ = MessageBox(IntPtr.Zero, text, "NAT", 0x30 /* MB_ICONWARNING */);
		else
			Console.Error.WriteLine(text);
	}

	[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MessageBoxW")]
	private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
