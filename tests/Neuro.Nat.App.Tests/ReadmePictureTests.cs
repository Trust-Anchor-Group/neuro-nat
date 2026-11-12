using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The picture at the top of the README: the Neurons list, light and dark, with made-up Neurons.
/// Written to .impeccable/review; with NAT_MAKE_README_PICTURE=1 also to docs/images.
/// </summary>
public class ReadmePictureTests
{
	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task NeuronsList(string theme)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Readme." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "example.com", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false, cancel: TestContext.Current.CancellationToken);

		MonitorService monitors = new(manager, new EventHub());
		FleetService fleet = new(monitors);

		// Made-up Neurons and numbers.
		static SampleSeries Series(double start, double end, int n, string unit)
			=> new(end, [.. Enumerable.Range(0, n).Select(i => start + ((end - start) * i / (n - 1)) + (Math.Sin(i) * 3))], unit);

		NeuronHealth Health(double cpuFrom, double cpuTo, double alloc, double free, double disk) => new()
		{
			AccountId = Guid.Empty,
			NeuronJid = string.Empty,
			Software = "Waher.IoTGateway 3.11.2.0",
			Cpu = Series(cpuFrom, cpuTo, 30, "%"),
			AllocatedMemory = new SampleSeries(alloc, [alloc], "MB"),
			FreeMemory = new SampleSeries(free, [free], "MB"),
			FreeDisk = new SampleSeries(disk, [disk], "GB"),
			ReadAt = DateTime.Now,
		};

		void Neuron(string jid, WatchState state, NeuronHealth? h = null)
		{
			monitors.For(ops.Profile.Id)!.Preview(new WatchedNeuron
			{
				AccountId = ops.Profile.Id,
				BareJid = jid,
				State = state,
				HistoryState = state == WatchState.Live ? HistoryState.Loaded : HistoryState.NotLoaded,
				HistoryCount = state == WatchState.Live ? 180 : 0,
			});
			if (h is not null)
				fleet.Preview(h with { AccountId = ops.Profile.Id, NeuronJid = jid });
		}

		Neuron("id.example", WatchState.Live, Health(14, 19, 820, 6100, 210.5));
		Neuron("pay.example", WatchState.Live, Health(30, 71, 1640, 1210, 88.0));
		Neuron("lab.example", WatchState.Live, Health(4, 6, 380, 9900, 455.1));
		Neuron("edge.example", WatchState.Live, Health(9, 12, 510, 3300, 64.3));
		Neuron("backup.example", WatchState.Offline);

		DateTime now = DateTime.Now;
		void Event(int secondsAgo, Neuro.Nat.Engine.Sniffing.SnifferDirection d, string neuron, string text)
		{
			Neuro.Nat.Engine.Sniffing.SnifferLine line = new(0, now.AddSeconds(-secondsAgo), d, text);
			if (SniffedEventParser.Parse(line, new EventSource(ops.Profile.Id, neuron)) is NeuronEvent e)
				monitors.Hub.Add(e);
		}

		for (int i = 0; i < 6; i++)
			Event(i * 300 + 40, Neuro.Nat.Engine.Sniffing.SnifferDirection.Error, "pay.example", "TimeoutException in PaymentService.Settle\n\nModule: Waher.Payments");
		for (int i = 0; i < 3; i++)
			Event(i * 500 + 90, Neuro.Nat.Engine.Sniffing.SnifferDirection.Warning, "edge.example", "Endpoint 203.0.113.9 blocked after 5 failed logins\n\nModule: Waher.Security.LoginMonitor");

		MainWindowViewModel vm = new(manager, fleet);
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Fleet;
		vm.Fleet.Refresh();

		MainWindow window = new() { DataContext = vm, Width = 1280, Height = 720 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);

		string root = RenameTests.RepositoryRoot();
		string name = "neurons-" + theme.ToLowerInvariant() + ".png";
		List<string> targets = [Path.Combine(root, ".impeccable", "review", "readme-" + name)];
		if (Environment.GetEnvironmentVariable("NAT_MAKE_README_PICTURE") == "1")
			targets.Add(Path.Combine(root, "docs", "images", name));

		foreach (string target in targets)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			using FileStream f = File.Create(target);
			frame!.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
		}

		Assert.Equal(5, vm.Fleet.Rows.Count);
		window.Close();
	}
}
