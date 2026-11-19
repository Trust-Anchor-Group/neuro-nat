using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Neuro.Nat.Engine.Alerts;
using Neuro.Nat.App.Watch;
using Neuro.Nat.Desktop.Install;
using Velopack;
using Velopack.Locators;

namespace Neuro.Nat.App.Tests;

public class AppUpdateTests
{
	private sealed class TrayUpdates : IAppUpdates
	{
		public AppUpdateStatus Status { get; private set; } = new(AppUpdatePhase.Idle, "");
		public event EventHandler? Changed;
		public int Restarts { get; private set; }
		public Task CheckAsync()
		{
			this.Status = new(AppUpdatePhase.Ready, "Update downloaded.", "2.0.0");
			this.Changed?.Invoke(this, EventArgs.Empty);
			return Task.CompletedTask;
		}
		public void InstallWhenQuit(bool restart)
		{
			Assert.True(restart);
			this.Restarts++;
			throw new IOException("Installer unavailable");
		}
	}

	[AvaloniaFact]
	public async Task TrayAndSettingsShareTheCheckAndOnlyRestartOnAnExplicitUpdate()
	{
		using var sample = await GettingAroundTests.SampleAsync();
		using AlertService alerts = new(sample.Monitors, new AlertSettings(), startTimer: false);
		TrayUpdates updates = new();
		using ClassicDesktopStyleApplicationLifetime desktop = new();
		using WatchAgent watch = new(desktop, new Window(), sample.Vm, alerts, updates);
		var settings = sample.Vm.Settings;
		NativeMenuItem item = TrayIcon.GetIcons(Application.Current!)!.Single().Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Check for updates");
		Assert.Same(settings.UpdateAppCommand, item.Command);
		item.Command!.Execute(null);
		// The feed finished but the screen still says "Check". A second click must not restart.
		await settings.UpdateAppCommand.ExecuteAsync(null);
		Assert.Equal(0, updates.Restarts);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(settings.AppUpdate.ActionLabel, item.Header);
		Assert.Contains("2.0.0", item.Header);
		await settings.UpdateAppCommand.ExecuteAsync(null);
		Assert.Equal(1, updates.Restarts);
		Assert.Equal(AppUpdatePhase.Failed, settings.AppUpdate.Phase);
		Assert.True(settings.UpdateAppCommand.CanExecute(null));
	}

	[Fact]
	public async Task ManualCheckChecksTheFeedAndStagesTheDownloadedVersion()
	{
		DirectoryInfo root = Directory.CreateTempSubdirectory("nat-updates-");
		try
		{
			string feed = root.CreateSubdirectory("feed").FullName;
			string packages = root.CreateSubdirectory("packages").FullName;
			const string file = "Neuro.Nat-2.0.0-full.nupkg";
			string manifest = Path.Combine(feed, "releases.win.json");
			string package = Path.Combine(feed, file);
			using (ZipArchive zip = ZipFile.Open(package, ZipArchiveMode.Create))
			using (StreamWriter writer = new(zip.CreateEntry("Neuro.Nat.nuspec").Open()))
				writer.Write("<package><metadata><id>Neuro.Nat</id><version>2.0.0</version></metadata></package>");

			using VelopackUpdates updates = new(new UpdateManager(feed, locator: new TestVelopackLocator("Neuro.Nat", "1.0.0", packages)));
			List<AppUpdatePhase> phases = [];
			updates.Changed += (_, _) => phases.Add(updates.Status.Phase);

			void WriteFeed(string version, string hash)
			{
				var asset = new { PackageId = "Neuro.Nat", Version = version, Type = "Full", FileName = file, SHA256 = hash, Size = new FileInfo(package).Length };
				File.WriteAllText(manifest, JsonSerializer.Serialize(new { Assets = new[] { asset } }));
			}

			WriteFeed("1.0.0", "");
			await updates.CheckAsync();
			Assert.Equal(AppUpdatePhase.Current, updates.Status.Phase);

			WriteFeed("2.0.0", new string('0', 64));
			await updates.CheckAsync();
			Assert.Equal(AppUpdatePhase.Failed, updates.Status.Phase);
			Assert.Null(updates.Status.ReadyVersion);

			WriteFeed("2.0.0", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))));
			phases.Clear();
			await updates.CheckAsync();
			Assert.Equal([AppUpdatePhase.Checking, AppUpdatePhase.Downloading, AppUpdatePhase.Ready], phases);
			Assert.Equal("2.0.0", updates.Status.ReadyVersion);
			Assert.True(File.Exists(Path.Combine(packages, file)));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}
}
