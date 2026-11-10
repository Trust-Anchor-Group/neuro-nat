using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.AA.App.ViewModels;
using Neuro.AA.App.Views;
using Neuro.AA.Engine;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Events;
using Neuro.AA.Engine.Sniffing;
using Neuro.AA.Engine.Topology;

namespace Neuro.AA.App.Tests;

/// <summary>Renders the Topology workspace with a made-up federation of Neurons, for design review.</summary>
public class TopologyRenderTests
{
	[AvaloniaTheory]
	[InlineData("Light", 1440, 900, true)]
	[InlineData("Dark", 1440, 900, true)]
	[InlineData("Light", 1100, 720, false)]
	public async Task RendersTopologyWorkspace(string theme, int width, int height, bool select)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "NeuroAA.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.tagroot.io", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		AccountSession admin = await manager.AddAsync(new AccountProfile { Host = "prod.example.com", UserName = "admin", ConnectOnStartup = false, ColorIndex = 3 }, AccountSecret.FromPassword("x"), connect: false);

		MonitorService monitors = new(manager, new EventHub());
		FleetService fleet = new(monitors);
		DateTime now = DateTime.Now;

		// Synthetic sample data: a lab with three edge Neurons under it, federated with production.
		List<TopologyService.NeuronAnswer> answers = [];
		WatchedNeuron Neuron(AccountSession s, string jid, string name)
		{
			WatchedNeuron n = new() { AccountId = s.Profile.Id, BareJid = jid, Name = name, State = WatchState.Live };
			monitors.For(s.Profile.Id)!.Preview(n);
			fleet.Preview(new NeuronHealth
			{
				AccountId = s.Profile.Id,
				NeuronJid = jid,
				Software = "Waher.IoTGateway 3.11.1.0",
				Cpu = new SampleSeries(14, [12, 14], "%"),
				FreeMemory = new SampleSeries(5210, [5210], "MB"),
				ReadAt = now,
			});
			return n;
		}

		void Answer(WatchedNeuron n, string? parent, params (string To, string? Label)[] federation)
		{
			string p = parent is null ? string.Empty : $"\"{parent}\" -> \"{n.BareJid}\" [dir=back];";
			string f = string.Concat(federation.Select(x => $"\"{n.BareJid}\" -> \"{x.To}\"" + (x.Label is null ? ";" : $" [label=\"{x.Label}\"];")));
			answers.Add(new(n, n.BareJid, DotGraph.Edges(p), DotGraph.Edges(f), [], null));
		}

		void Event(string jid, SnifferDirection d, string text)
		{
			if (SniffedEventParser.Parse(new SnifferLine(0, now.AddMinutes(-5), d, text), new EventSource(Guid.Empty, jid)) is NeuronEvent e)
				monitors.Hub.Add(e);
		}

		WatchedNeuron lab = Neuron(ops, "lab.tagroot.io", "Lab");
		WatchedNeuron gbg = Neuron(ops, "gbg.lab.tagroot.io", "Gothenburg edge");
		WatchedNeuron sto = Neuron(ops, "sto.lab.tagroot.io", "Stockholm edge");
		WatchedNeuron mmx = Neuron(ops, "mmx.lab.tagroot.io", "Malmö edge");
		WatchedNeuron prod = Neuron(admin, "prod.example.com", "Production");
		WatchedNeuron backup = Neuron(admin, "backup.prod.example.com", "Backup");

		Answer(lab, "tagroot.io", ("tagroot.io", null), ("prod.example.com", null), ("id.tagroot.io", null));
		Answer(gbg, "lab.tagroot.io", ("lab.tagroot.io", null));
		Answer(sto, "lab.tagroot.io", ("lab.tagroot.io", null), ("gbg.lab.tagroot.io", null));
		Answer(mmx, "lab.tagroot.io");
		Answer(prod, null, ("lab.tagroot.io", null), ("partner.org", "HTTP"), ("backup.prod.example.com", null));
		Answer(backup, "prod.example.com");
		answers.Add(new(new WatchedNeuron { AccountId = admin.Profile.Id, BareJid = "legacy.prod.example.com", Name = "Legacy", State = WatchState.Live },
			null, [], [], [], "The Neuron does not support topology graphs: Unknown command."));

		for (int i = 0; i < 4; i++)
			Event("gbg.lab.tagroot.io", SnifferDirection.Error, "SocketException: Connection reset by peer (lab.tagroot.io:5269)\n\nModule: Waher.Networking.XMPP.Server\nEventId: S2S");
		Event("prod.example.com", SnifferDirection.Warning, "Endpoint 203.0.113.40 blocked after 5 failed logins\n\nModule: Waher.Security.LoginMonitor\nEventId: Blocked");

		MainWindowViewModel vm = new(manager, fleet);
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Topology;
		vm.Topology.Load(TopologyService.Merge(answers, now.AddMinutes(-2)), includesGroups: false);
		if (select)
			vm.Topology.SelectedId = "gbg.lab.tagroot.io";

		MainWindow window = new() { DataContext = vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(300);
		Capture(window, $"topology-{theme.ToLowerInvariant()}-{width}");

		Assert.Equal(9, vm.Topology.Graph.Nodes.Count);
		Assert.Equal("bad", vm.Topology.Graph.Nodes.Single(n => n.Id == "gbg.lab.tagroot.io").Tone);
		Assert.Equal("busy", vm.Topology.Graph.Nodes.Single(n => n.Id == "prod.example.com").Tone);
		Assert.Equal("ok", vm.Topology.Graph.Nodes.Single(n => n.Id == "mmx.lab.tagroot.io").Tone);
		if (select)
			Assert.Contains(vm.Topology.Connections, c => c.Id == "lab.tagroot.io" && c.Relation.StartsWith("Its parent", StringComparison.Ordinal));

		window.Close();
	}

	private static void Capture(MainWindow window, string name)
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
