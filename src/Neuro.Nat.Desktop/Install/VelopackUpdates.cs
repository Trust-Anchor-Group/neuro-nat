using System.Reflection;
using Neuro.Nat.App;
using Neuro.Nat.App.Watch;
using Velopack;
using Velopack.Sources;

namespace Neuro.Nat.Desktop.Install;

/// <summary>
/// Looks for new versions of NAT in the background and downloads them; the tray then offers
/// to restart into the update, and otherwise it is installed when NAT quits.
/// </summary>
/// <remarks>
/// Where updates come from, first match wins:
/// the <c>NAT_UPDATE_SOURCE</c> environment variable; the first line of
/// <c>update-source.txt</c> in the data folder; the source built in (<c>UpdateSource</c> in
/// Directory.Build.props, GitHub releases of the repository). Without any of them NAT does not
/// look for updates.
/// A source is a GitHub repository URL, any other http(s) URL, or a folder (also a network share).
/// GitHub releases of a private repository need a token in <c>NAT_GITHUB_TOKEN</c>.
/// </remarks>
internal sealed class VelopackUpdates : IAppUpdates, IDisposable
{
	private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

	private readonly UpdateManager? manager;
	private readonly CancellationTokenSource stop = new();
	private volatile VelopackAsset? ready;
	private volatile AppUpdateStatus status;
	private int checking;

	internal VelopackUpdates(UpdateManager manager)
	{
		this.manager = manager;
		this.ready = manager.UpdatePendingRestart;
		this.status = this.ready is null ? new(AppUpdatePhase.Idle, "NAT checks for updates automatically.") : ReadyStatus(this.ready);
	}

	private VelopackUpdates(string unavailableReason) => this.status = new(AppUpdatePhase.Unavailable, unavailableReason);

	public AppUpdateStatus Status => this.status;

	public event EventHandler? Changed;

	/// <summary>Starts looking for updates, or returns null when NAT was not installed with its installer (a dev build).</summary>
	public static VelopackUpdates? TryStart(string dataFolder)
	{
		try
		{
			string? source = SourceFor(dataFolder, BuiltInSource);
			if (source is null)
			{
				AppLog.Info("No update source is built in; not looking for updates.");
				return new VelopackUpdates("Configure an update source to enable updates.");
			}

			UpdateManager manager = new(CreateSource(source));
			if (!manager.IsInstalled)
				return null;

			VelopackUpdates updates = new(manager);
			AppLog.Info($"NAT {manager.CurrentVersion} installed; updates from {source}.");
			_ = updates.RunAsync(updates.stop.Token);
			return updates;
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not start looking for updates.", ex);
			return new VelopackUpdates("The updater could not start. Check the NAT log and restart the app.");
		}
	}

	public void InstallWhenQuit(bool restart)
	{
		VelopackAsset target = this.ready ?? throw new InvalidOperationException("No downloaded update is ready.");

		// A small helper waits for NAT to exit, installs quietly, and starts it again if asked.
		this.manager!.WaitExitThenApplyUpdates(target, silent: true, restart: restart, restartArgs: []);
		AppLog.Info($"Update to {target.Version} will be installed when NAT quits{(restart ? ", then started again" : "")}.");
	}

	private async Task RunAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(FirstCheck, token);
			while (!token.IsCancellationRequested)
			{
				await this.CheckAsync();
				await Task.Delay(Interval, token);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private static AppUpdateStatus ReadyStatus(VelopackAsset target) =>
		new(AppUpdatePhase.Ready, $"NAT {target.Version} is downloaded. Restart now or install when NAT quits.", target.Version.ToString());

	private void SetStatus(AppUpdateStatus value)
	{
		this.status = value;
		this.Changed?.Invoke(this, EventArgs.Empty);
	}

	public async Task CheckAsync()
	{
		if (this.manager is null || Interlocked.Exchange(ref this.checking, 1) != 0)
			return;

		string? staged = this.ready?.Version.ToString();
		try
		{
			CancellationToken token = this.stop.Token;
			this.SetStatus(new(AppUpdatePhase.Checking, "Checking for updates…", staged));
			UpdateInfo? info = await this.manager.CheckForUpdatesAsync().ConfigureAwait(false);
			token.ThrowIfCancellationRequested();
			if (info is null || this.ready is not null && this.ready.Version >= info.TargetFullRelease.Version)
			{
				this.SetStatus(this.ready is null ? new(AppUpdatePhase.Current, "NAT is up to date.") : ReadyStatus(this.ready));
				return;
			}

			VelopackAsset target = info.TargetFullRelease;
			AppLog.Info($"Downloading NAT {target.Version}.");
			this.SetStatus(new(AppUpdatePhase.Downloading, $"Downloading NAT {target.Version}…", staged));
			await this.manager.DownloadUpdatesAsync(info, cancelToken: token).ConfigureAwait(false);
			this.ready = target;
			AppLog.Info($"NAT {target.Version} is downloaded and will be installed on restart.");
			this.SetStatus(ReadyStatus(target));
		}
		catch (OperationCanceledException) when (this.stop.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not check for or download an update.", ex);
			string message = this.status.Phase == AppUpdatePhase.Downloading ? "Download failed." : "Update check failed.";
			this.SetStatus(new(AppUpdatePhase.Failed, message + (staged is null ? " Check the connection and update source, then try again." : $" NAT {staged} is still ready to install."), staged));
		}
		finally
		{
			Volatile.Write(ref this.checking, 0);
		}
	}

	/// <summary>The update source built into the program (assembly metadata), or null when there is none.</summary>
	internal static string? BuiltInSource
		=> typeof(VelopackUpdates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => a.Key == "UpdateSource")?.Value is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

	/// <summary>Where updates come from: the environment variable, then update-source.txt, then <paramref name="builtIn"/>.</summary>
	internal static string? SourceFor(string dataFolder, string? builtIn)
	{
		string? fromEnvironment = Environment.GetEnvironmentVariable(SourceVariable);
		if (!string.IsNullOrWhiteSpace(fromEnvironment))
			return fromEnvironment.Trim();

		string file = Path.Combine(dataFolder, "update-source.txt");
		if (File.Exists(file))
		{
			string? line = File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
			if (line is not null)
				return line;
		}

		return builtIn;
	}

	/// <summary>The environment variable that overrides where updates come from.</summary>
	internal const string SourceVariable = "NAT_UPDATE_SOURCE";

	private static IUpdateSource CreateSource(string source)
	{
		if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
		{
			if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
				return new GithubSource(source, Environment.GetEnvironmentVariable("NAT_GITHUB_TOKEN"), prerelease: false);

			return new SimpleWebSource(uri);
		}

		return new SimpleFileSource(new DirectoryInfo(Environment.ExpandEnvironmentVariables(source)));
	}

	public void Dispose()
	{
		this.stop.Cancel();
		this.stop.Dispose();
	}
}
