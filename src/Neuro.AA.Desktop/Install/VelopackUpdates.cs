using System.Reflection;
using Neuro.AA.App;
using Neuro.AA.App.Watch;
using Velopack;
using Velopack.Sources;

namespace Neuro.AA.Desktop.Install;

/// <summary>
/// Looks for new versions of Neuro.AA in the background and downloads them; the tray then offers
/// to restart into the update, and otherwise it is installed when Neuro.AA quits.
/// </summary>
/// <remarks>
/// Where updates come from, first match wins:
/// the <c>NEURO_AA_UPDATE_SOURCE</c> environment variable; the first line of
/// <c>%AppData%\Neuro.AA\update-source.txt</c>; the source baked in when the installer was built
/// (<c>UpdateSource</c> MSBuild property, GitHub releases of the repository by default).
/// A source is a GitHub repository URL, any other http(s) URL, or a folder (also a network share).
/// GitHub releases of a private repository need a token in <c>NEURO_AA_GITHUB_TOKEN</c>.
/// </remarks>
internal sealed class VelopackUpdates : IAppUpdates, IDisposable
{
	private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

	private readonly UpdateManager manager;
	private readonly CancellationTokenSource stop = new();
	private VelopackAsset? ready;

	private VelopackUpdates(UpdateManager manager, string source)
	{
		this.manager = manager;
		this.Source = source;
		this.ready = manager.UpdatePendingRestart;
	}

	public string Source { get; }

	public string? ReadyVersion => this.ready?.Version.ToString();

	public event EventHandler? Ready;

	/// <summary>Starts looking for updates, or returns null when Neuro.AA was not installed with its installer (a dev build).</summary>
	public static VelopackUpdates? TryStart(string dataFolder)
	{
		try
		{
			string source = SourceFor(dataFolder);
			UpdateManager manager = new(CreateSource(source));
			if (!manager.IsInstalled)
				return null;

			VelopackUpdates updates = new(manager, source);
			AppLog.Info($"Neuro.AA {manager.CurrentVersion} installed; updates from {source}.");
			_ = updates.RunAsync();
			return updates;
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not start looking for updates.", ex);
			return null;
		}
	}

	public void InstallWhenQuit(bool restart)
	{
		if (this.ready is null)
			return;

		// A small helper waits for Neuro.AA to exit, installs quietly, and starts it again if asked.
		this.manager.WaitExitThenApplyUpdates(this.ready, silent: true, restart: restart, restartArgs: []);
		AppLog.Info($"Update to {this.ready.Version} will be installed when Neuro.AA quits{(restart ? ", then started again" : "")}.");
	}

	private async Task RunAsync()
	{
		try
		{
			await Task.Delay(FirstCheck, this.stop.Token);
			while (!this.stop.IsCancellationRequested)
			{
				await this.CheckAsync();
				await Task.Delay(Interval, this.stop.Token);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task CheckAsync()
	{
		try
		{
			UpdateInfo? info = await this.manager.CheckForUpdatesAsync();
			if (info is null)
				return;

			VelopackAsset target = info.TargetFullRelease;
			if (this.ready is not null && this.ready.Version >= target.Version)
				return;

			AppLog.Info($"Downloading Neuro.AA {target.Version}.");
			await this.manager.DownloadUpdatesAsync(info, cancelToken: this.stop.Token);
			this.ready = target;
			AppLog.Info($"Neuro.AA {target.Version} is downloaded and will be installed on restart.");
			this.Ready?.Invoke(this, EventArgs.Empty);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			AppLog.Error($"Could not look for updates at {this.Source}.", ex);
		}
	}

	private static string SourceFor(string dataFolder)
	{
		string? fromEnvironment = Environment.GetEnvironmentVariable("NEURO_AA_UPDATE_SOURCE");
		if (!string.IsNullOrWhiteSpace(fromEnvironment))
			return fromEnvironment.Trim();

		string file = Path.Combine(dataFolder, "update-source.txt");
		if (File.Exists(file))
		{
			string? line = File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
			if (line is not null)
				return line;
		}

		return typeof(VelopackUpdates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => a.Key == "UpdateSource")?.Value
			?? "https://github.com/berggrenmille/Neuro-AA";
	}

	private static IUpdateSource CreateSource(string source)
	{
		if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
		{
			if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
				return new GithubSource(source, Environment.GetEnvironmentVariable("NEURO_AA_GITHUB_TOKEN"), prerelease: false);

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
