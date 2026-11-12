using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Software;

namespace Neuro.Nat.App.Tests;

/// <summary>Renders the Updates workspace with made-up Neurons, for design review.</summary>
public class UpdatesRenderTests
{
	[AvaloniaTheory]
	[InlineData("Light", 1440, 900, false)]
	[InlineData("Dark", 1440, 900, true)]
	[InlineData("Light", 1100, 720, false)]
	public async Task RendersUpdatesWorkspace(string theme, int width, int height, bool confirming)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.tagroot.io", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		MonitorService monitors = new(manager, new EventHub());
		DateTime now = DateTime.Now;

		// Synthetic sample data.
		string[] jids = ["lab.tagroot.io", "gbg.lab.tagroot.io", "sto.lab.tagroot.io"];
		string[] names = ["Lab", "Gothenburg edge", "Stockholm edge"];
		for (int i = 0; i < jids.Length; i++)
			monitors.For(ops.Profile.Id)!.Preview(new WatchedNeuron { AccountId = ops.Profile.Id, BareJid = jids[i], Name = names[i], State = WatchState.Live });

		NeuronNotification pending = new()
		{
			Id = "n1",
			AccountId = ops.Profile.Id,
			NeuronJid = jids[1],
			Received = now.AddMinutes(-3),
			Markdown = "New version of installed software has been downloaded\n=====\n\n| Package ||\n|:--|:--|\n| Filename: | `TAG.Neuron.3.11.2.package` |\n\nThe new software will be automatically installed in 15 minutes, unless cancelled from the chat interface.",
		};
		monitors.Attention.Preview(pending);
		NeuronNotification shown = monitors.Attention.Items.First();

		MainWindowViewModel vm = new(manager, new FleetService(monitors));
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Updates;
		UpdatesViewModel u = vm.Updates;
		u.Sync();

		SoftwarePackage[] packages =
		[
			new("TAG.Neuron.3.11.2.package", "48.2 MB", "2026-11-10 08:12", null, "2026-11-10 10:31", null, "lab.tagroot.io"),
			new("TAG.Neuron.3.11.1.package", "48.0 MB", "2026-10-28 14:02", null, "2026-10-28 14:30", "2026-10-28 14:46", "lab.tagroot.io"),
			new("TAG.Payments.2.4.0.package", "1.1 MB", "2026-10-02 09:40", null, "2026-10-02 10:05", "2026-10-02 10:05", "lab.tagroot.io"),
		];
		foreach (UpdateRowViewModel r in u.Rows)
		{
			bool gbg = r.NeuronJid == jids[1];
			r.Software = new NeuronSoftware
			{
				AccountId = r.AccountId,
				NeuronJid = r.NeuronJid,
				BuildTime = gbg ? "2026-10-28, 14:22:10" : "2026-11-10, 08:12:40",
				AutoDelayMinutes = gbg ? 15 : null,
				ContentAutoInstall = gbg,
				Installed = ["TAG.Neuron.package", "TAG.Payments.package"],
				Packages = packages,
				PendingUpdate = gbg ? shown : null,
				ReadAt = now,
			};
		}
		u.Sync();
		u.Selected = u.Rows.First(r => r.NeuronJid == jids[1]);
		if (confirming)
		{
			u.AskUpgradeCommand.Execute(null);
			u.UpgradeConfirmText = "Gothen";
		}

		MainWindow window = new() { DataContext = vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(300);
		Capture(window, $"updates-{theme.ToLowerInvariant()}-{width}");

		Assert.True(u.HasPending);
		Assert.Equal("15", u.AutoMinutes);
		Assert.Equal(3, u.Packages.Count);
		Assert.False(u.UpgradeNowCommand.CanExecute(null));
		window.Close();
	}

	private static void Capture(MainWindow window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);

		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;
		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		frame!.Save(Path.Combine(folder, name + ".png"));
	}
}
