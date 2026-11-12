using Avalonia;
using Avalonia.Threading;
using Neuro.Nat.App;
using Neuro.Nat.Desktop.Install;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Storage;
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

		// Started by a click on a NAT link in another program: the link is the argument.
		string? startLink = LinkIn(args);

		// One NAT per user: starting it again brings the running one (perhaps in the tray) forward,
		// with the link to open when there is one.
		using SingleInstance? single = SingleInstance.TryClaim();
		if (single is null)
		{
			if (!startHidden || startLink is not null)
				SingleInstance.AskRunningToShow(startLink);
			return 0;
		}

		single.Listen(App.App.ShowMainWindow, App.App.OpenLink);

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
			EngineRuntime.PrepareTypes(uiAssemblies);

			HostStores? stores = OpenStores(dataFolder, args);
			if (stores is null)
				return AskAboutDatabase(args, single);

			AccountManager accounts = new(stores.Accounts, stores.SignIns);

			AttentionInbox attention = new(stores.Inbox);
			attention.Load();
			AppLog.Info($"Inbox items loaded: {attention.Items.Count}.");

			MonitorService monitors = new(accounts, new EventHub(), attention, stores.KnownNeurons);
			Engine.Chat.ChatService chat = new(accounts, monitors, stores.Chat);
			chat.Load();

			Engine.Identity.IdentityService identities = new(accounts, chat);

			Engine.Devices.DevicesService devices = new(accounts);
			Engine.PubSub.PubSubService pubsub = new(accounts);

			App.App.Services = new AppServices(accounts, monitors, new FleetService(monitors), chat, identities, devices, pubsub, dataFolder)
			{
				StartHidden = startHidden && startLink is null,
				StartLink = startLink,
				Autostart = OperatingSystem.IsWindows() ? new WindowsAutostart() : null,
				Updates = VelopackUpdates.TryStart(dataFolder),
				AlertSettings = stores.AlertSettings,
				UiSettings = stores.UiSettings,
				KeepsData = stores.KeepsData,
				NeuronCopies = stores.NeuronCopies,
				Runbooks = stores.Runbooks,

				// Features in projects of their own. NAT knows them only through App.Features.
				Features = new App.Features.FeatureSet([new Features.Telemetry.TelemetryFeature()]),
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

	/// <summary>The NAT link among the arguments NAT was started with, or null. It is checked again before anything in it is used.</summary>
	internal static string? LinkIn(IEnumerable<string> args)
		=> args.FirstOrDefault(a => Engine.Links.NatLink.LooksLike(a) && a.Length <= Engine.Links.NatLink.MaxLength)?.Trim();

	/// <summary>Restarts NAT and sets what it had saved aside first: the choice "Start fresh".</summary>
	private const string StartFreshArgument = "--start-fresh";

	/// <summary>Where everything NAT keeps lives while it runs.</summary>
	private sealed record HostStores(
		IAccountStore Accounts, ICredentialVault SignIns, IAttentionStore? Inbox, Engine.Chat.IChatStore? Chat,
		Engine.Alerts.IAlertSettingsStore AlertSettings, App.ViewModels.IUiSettingsStore UiSettings, IKnownNeuronStore KnownNeurons, bool KeepsData)
	{
		/// <summary>Copies of what comparisons read.</summary>
		public INeuronCopyStore NeuronCopies { get; init; } = new MemoryNeuronCopyStore();
		/// <summary>Runbooks and the records of their runs.</summary>
		public IRunbookStore Runbooks { get; init; } = new InMemoryRunbookStore();
	}

	/// <summary>
	/// Opens the encrypted database and moves the old plain files into it. Returns null when the
	/// database cannot be opened; the user is then asked what to do.
	/// </summary>
	private static HostStores? OpenStores(string dataFolder, string[] args)
	{
		string databaseFolder = Path.Combine(dataFolder, "database");

		if (args.Contains(StartFreshArgument, StringComparer.OrdinalIgnoreCase))
		{
			string? aside = LocalDatabase.SetAside(databaseFolder, DateTime.Now);
			AppLog.Info(aside is null ? "Starting fresh: there was no database to move aside." : $"Starting fresh: the database was moved aside to {aside}.");
			if (aside is not null)
				Tell($"What NAT had saved was moved to this folder:\n\n{aside}\n\nNothing was deleted. NAT starts fresh now.");
		}

		if (!OperatingSystem.IsWindows())
		{
			// No vault for the database key here yet, so no database: nothing is kept between runs, and Settings says so.
			EngineRuntime.Initialize();
			AppLog.Info("No vault for a database key on this system: nothing is kept between runs.");
			return new HostStores(new InMemoryAccountStore(), new InMemoryCredentialVault(), null, null,
				new Engine.Alerts.MemoryAlertSettingsStore(), new App.ViewModels.MemoryUiSettingsStore(), new InMemoryKnownNeuronStore(), KeepsData: false);
		}

		DatabaseOpening opening;
		byte[]? key = null;
		try
		{
			key = DatabaseKeys.ForCurrentPlatform(dataFolder) ?? throw new InvalidOperationException("No database key.");
			opening = LocalDatabase.OpenAsync(databaseFolder, key).GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			opening = new DatabaseOpening(null, "The database key could not be read.", ex);
		}
		finally
		{
			if (key is not null)
				System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
		}

		if (opening.Database is not Waher.Persistence.Files.FilesProvider db)
		{
			AppLog.Error("The database could not be opened: " + opening.Problem, opening.Error);
			return null;
		}

		EngineRuntime.UseDatabase(db);
		AppLog.Info("Database open; everything NAT keeps is encrypted.");

		// Once: what was kept in plain files before moves into the database. What fails keeps its file and is tried again next start.
		LegacyImport import = new(db, dataFolder)
		{
			ReadOldSignIn = id => OperatingSystem.IsWindows()
				? new DpapiCredentialVault(Path.Combine(dataFolder, "secrets")).GetAsync(id)
				: Task.FromResult<AccountSecret?>(null),
		};
		if (import.HasWork())
		{
			IReadOnlyList<ImportStep> steps = import.RunAsync().GetAwaiter().GetResult();
			foreach (ImportStep s in steps)
			{
				if (s.Failed)
					AppLog.Error($"Could not move the old {s.What} into the database; the old file stays and this is tried again next start.", s.Error);
				else
					AppLog.Info($"Moved the old {s.What} into the database ({s.Count}) and removed the old file.");
			}

			if (steps.Any(s => s.Failed))
				Tell("NAT could not move some of what it saved earlier into its encrypted store. Nothing was lost: the old files stay, and NAT tries again the next time it starts.");
		}

		DatabaseSettings settings = new(db);
		return new HostStores(new DatabaseAccountStore(db), new DatabaseCredentialVault(db), new DatabaseAttentionStore(db), new DatabaseChatStore(db),
			new Engine.Alerts.DatabaseAlertSettingsStore(settings), new App.ViewModels.DatabaseUiSettingsStore(settings), new DatabaseKnownNeuronStore(db), KeepsData: true)
		{
			NeuronCopies = new DatabaseNeuronCopyStore(db),
			Runbooks = new DatabaseRunbookStore(db),
		};
	}

	/// <summary>
	/// What NAT saved cannot be opened: never start empty without a word. Asks Try again, Start
	/// fresh or Quit. The first two start NAT again, since a failed open keeps the files busy
	/// until this process ends.
	/// </summary>
	private static int AskAboutDatabase(string[] args, SingleInstance single)
	{
		App.ViewModels.DatabaseProblemViewModel problem = new();
		App.App.DatabaseProblem = problem;
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

		AppLog.Info($"Database problem: the user chose {problem.Choice}.");
		if (problem.Choice is not (App.ViewModels.DatabaseChoice.TryAgain or App.ViewModels.DatabaseChoice.StartFresh))
			return 1;

		List<string> again = [.. args.Where(a => !string.Equals(a, StartFreshArgument, StringComparison.OrdinalIgnoreCase))];
		if (problem.Choice == App.ViewModels.DatabaseChoice.StartFresh)
			again.Add(StartFreshArgument);

		// Let go of "one NAT per user" first, or the new start would only bring this one forward.
		single.Dispose();
		System.Diagnostics.ProcessStartInfo start = new(Environment.ProcessPath ?? throw new InvalidOperationException("The path of NAT is not known.")) { UseShellExecute = false };
		foreach (string a in again)
			start.ArgumentList.Add(a);
		System.Diagnostics.Process.Start(start);
		return 0;
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
