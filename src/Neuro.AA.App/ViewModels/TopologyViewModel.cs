using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.App.Controls;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Events;
using Neuro.AA.Engine.Topology;

namespace Neuro.AA.App.ViewModels;

/// <summary>A connection of the selected point, listed beside the map.</summary>
public sealed record TopologyConnection(string Id, string Label, string Relation);

/// <summary>A labelled value about the selected point.</summary>
public sealed record TopologyFact(string Name, string Value);

/// <summary>
/// The Topology workspace: how your Neurons connect to their parent brokers, to federated
/// brokers, and (on request) to the contacts in their roster groups, merged from what every
/// Neuron you administer reports, with health from Fleet and Events on top.
/// </summary>
public sealed partial class TopologyViewModel : ObservableObject, IActionSource
{
	private readonly TopologyService topology;
	private readonly FleetService fleet;
	private readonly AccountManager accounts;
	private TopologySnapshot snapshot = TopologySnapshot.Empty;
	private bool groupsScanned;
	private CancellationTokenSource? scanCancel;

	public TopologyViewModel(TopologyService topology, FleetService fleet, AccountManager accounts)
	{
		this.topology = topology;
		this.fleet = fleet;
		this.accounts = accounts;
		fleet.HealthChanged += (_, _) => Dispatcher.UIThread.Post(this.Rebuild);
	}

	public ObservableCollection<TopologyFact> Facts { get; } = [];
	public ObservableCollection<TopologyConnection> Connections { get; } = [];
	public ObservableCollection<TopologyProblem> Problems { get; } = [];

	[ObservableProperty] private MapGraph graph = MapGraph.Empty;
	[ObservableProperty] private int fitVersion;
	[ObservableProperty] private string searchText = string.Empty;
	[ObservableProperty] private string summary = "Not scanned yet";
	[ObservableProperty] private bool isScanning;
	[ObservableProperty] private bool hasNodes;
	[ObservableProperty] private bool hasProblems;
	[ObservableProperty] private bool showParents = true;
	[ObservableProperty] private bool showFederation = true;

	/// <summary>Server-to-server links outside the parent tree (to brokers elsewhere). Off by default: they crowd the map.</summary>
	[ObservableProperty] private bool showOtherServers;
	[ObservableProperty] private bool showGroups;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelection))]
	private string? selectedId;

	public bool HasSelection => this.SelectedId is not null;

	[ObservableProperty] private string selectedTitle = string.Empty;
	[ObservableProperty] private string selectedKind = string.Empty;
	[ObservableProperty] private string selectedTone = "idle";
	[ObservableProperty] private bool selectedIsNeuron;

	/// <summary>Set by the shell: opens Events filtered to one Neuron.</summary>
	public Action<string>? ShowEventsFor { get; set; }

	/// <summary>Set by the shell: opens the console on one Neuron.</summary>
	public Action<Guid, string>? OpenConsoleFor { get; set; }

	partial void OnShowParentsChanged(bool value) => this.Rebuild();
	partial void OnShowFederationChanged(bool value) => this.Rebuild();
	partial void OnShowOtherServersChanged(bool value) => this.Rebuild();

	partial void OnShowGroupsChanged(bool value)
	{
		if (value && !this.groupsScanned)
			_ = this.ScanAsync();
		else
			this.Rebuild();
	}

	partial void OnSelectedIdChanged(string? value) => this.ShowSelection();

	/// <summary>Scans when the workspace opens for the first time, or when the last scan is old.</summary>
	public void Activate()
	{
		if (!this.IsScanning && DateTime.Now - this.snapshot.Taken > TimeSpan.FromMinutes(5))
			_ = this.ScanAsync();
	}

	[RelayCommand]
	private Task Scan() => this.ScanAsync();

	[RelayCommand]
	private void Fit() => this.FitVersion++;

	[RelayCommand]
	private void Select(TopologyConnection? c)
	{
		if (c is not null)
			this.SelectedId = c.Id;
	}

	[RelayCommand]
	private void ShowEvents()
	{
		if (this.Node(this.SelectedId) is { NeuronJid: string jid })
			this.ShowEventsFor?.Invoke(jid);
	}

	[RelayCommand]
	private void OpenConsole()
	{
		if (this.Node(this.SelectedId) is { AccountId: Guid a, NeuronJid: string jid })
			this.OpenConsoleFor?.Invoke(a, jid);
	}

	/// <summary>Set by the shell: opens a Neuron's page.</summary>
	public Action<Guid?, string>? OpenNeuronFor { get; set; }

	/// <summary>Opens the page of the selected Neuron.</summary>
	[RelayCommand]
	private void OpenPage()
	{
		if (this.Node(this.SelectedId) is { NeuronJid: string jid } n)
			this.OpenNeuronFor?.Invoke(n.AccountId, jid);
	}

	/// <summary>Opens what a double click on the map points at: the page of a Neuron.</summary>
	public void Activated(string id)
	{
		this.SelectedId = id;
		this.OpenPage();
	}

	private async Task ScanAsync()
	{
		this.scanCancel?.Cancel();
		CancellationTokenSource cancel = this.scanCancel = new CancellationTokenSource();
		bool groups = this.ShowGroups;
		this.IsScanning = true;
		this.Summary = "Asking your Neurons…";

		try
		{
			TopologySnapshot s = await this.topology.ScanAsync(groups, cancel.Token);
			if (cancel.IsCancellationRequested)
				return;

			this.Load(s, groups);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			this.Summary = "The scan failed: " + ex.Message;
		}
		finally
		{
			if (this.scanCancel == cancel)
				this.IsScanning = false;
		}
	}

	/// <summary>Shows a snapshot. Used by scans, and by tests with a made-up network.</summary>
	public void Load(TopologySnapshot s, bool includesGroups)
	{
		bool first = this.snapshot.Nodes.Count == 0;
		this.snapshot = s;
		this.groupsScanned = includesGroups;

		this.Problems.Clear();
		foreach (TopologyProblem p in s.Problems)
			this.Problems.Add(p);
		this.HasProblems = this.Problems.Count > 0;

		this.Rebuild();
		if (first)
			this.FitVersion++;

		this.Scanned?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Set by the shell: the moves to other screens.</summary>
	public IShell? Shell { get; set; }

	/// <summary>Actions for a point on the map (given by its id), or for the map itself when nothing is under the pointer.</summary>
	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		ActionList list = [];
		if (items.Count == 1 && this.Node(items[0] as string) is TopologyNode node)
		{
			if (node.NeuronJid is string jid && this.Shell is IShell shell)
				list.AddRange(shell.NeuronActions(jid, node.AccountId, WorkspaceKind.Topology));
			else
				list.Copy("Copy name", () => node.Label, "the name", "Ctrl+C");
		}

		list.Do("Fit the map to the window", () => this.FitVersion++);
		list.Do("Scan again", () => this.ScanCommand.Execute(null), icon: "Refresh", key: "F5");
		return list;
	}

	/// <summary>True once the Neurons have been asked how they are connected.</summary>
	public bool HasScan => this.snapshot.Nodes.Count > 0;

	/// <summary>Raised when a scan has been loaded.</summary>
	public event EventHandler? Scanned;

	/// <summary>
	/// What a Neuron is connected to, from the last scan: its parent, what is connected under it,
	/// and the servers it is federated with. Groups are left out; they are people, not Neurons.
	/// </summary>
	public IReadOnlyList<TopologyConnection> ConnectionsOf(string neuronJid)
	{
		if (this.snapshot.Nodes.FirstOrDefault(x => string.Equals(x.NeuronJid, neuronJid, StringComparison.OrdinalIgnoreCase)) is not TopologyNode n)
			return [];

		List<TopologyConnection> list = [];
		foreach (IGrouping<string, TopologyEdge> g in this.snapshot.Edges
			.Where(e => e.Kind != TopologyLink.Group && (e.From == n.Id || e.To == n.Id))
			.GroupBy(e => e.From == n.Id ? e.To : e.From)
			.OrderBy(g => g.Min(e => e.Kind)).ThenBy(g => this.Node(g.Key)?.Label ?? g.Key, StringComparer.OrdinalIgnoreCase))
		{
			string relation = string.Join(", ", g.OrderBy(e => e.Kind).Select(e => e.Kind == TopologyLink.Parent
				? (e.From == n.Id ? "its parent" : "connected under it")
				: e.Label is null ? "federated" : "federated over " + e.Label));
			list.Add(new TopologyConnection(g.Key, this.Node(g.Key)?.Label ?? g.Key, char.ToUpperInvariant(relation[0]) + relation[1..]));
		}

		return list;
	}

	/// <summary>The address of the Neuron behind a point on the map, when it is one.</summary>
	public string? NeuronAt(string id) => this.Node(id)?.NeuronJid;

	/// <summary>Selects a Neuron on the map by its address. Does nothing when the map does not have it yet.</summary>
	public void SelectNeuron(string neuronJid)
	{
		if (this.snapshot.Nodes.FirstOrDefault(n => string.Equals(n.NeuronJid, neuronJid, StringComparison.OrdinalIgnoreCase)) is TopologyNode node)
			this.SelectedId = node.Id;
	}

	private TopologyNode? Node(string? id) => id is null ? null : this.snapshot.Nodes.FirstOrDefault(n => n.Id == id);

	/// <summary>
	/// Each point's parent tree: points joined by parent links share a root. Federation means
	/// server-to-server links inside one tree; links to brokers outside it are "other servers".
	/// </summary>
	private Dictionary<string, string> Trees()
	{
		Dictionary<string, string> up = [];
		string Find(string x)
		{
			while (up.TryGetValue(x, out string? p) && p != x)
				x = p;
			return x;
		}

		foreach (TopologyNode n in this.snapshot.Nodes)
			up[n.Id] = n.Id;
		foreach (TopologyEdge e in this.snapshot.Edges.Where(e => e.Kind == TopologyLink.Parent))
		{
			string a = Find(e.From), b = Find(e.To);
			if (a != b)
				up[a] = b;
		}

		return this.snapshot.Nodes.ToDictionary(n => n.Id, n => Find(n.Id));
	}

	/// <summary>True for a server-to-server link inside one parent tree.</summary>
	private bool InTree(TopologyEdge e, Dictionary<string, string> trees)
		=> trees.TryGetValue(e.From, out string? a) && trees.TryGetValue(e.To, out string? b) && a == b;

	private IEnumerable<TopologyEdge> VisibleEdges
	{
		get
		{
			Dictionary<string, string> trees = this.Trees();
			return this.snapshot.Edges.Where(e => e.Kind switch
			{
				TopologyLink.Parent => this.ShowParents,
				TopologyLink.Federation => this.InTree(e, trees) ? this.ShowFederation : this.ShowOtherServers,
				_ => this.ShowGroups,
			});
		}
	}

	/// <summary>Lays out what is shown and colours the Neurons by health. UI thread.</summary>
	private void Rebuild()
	{
		List<TopologyEdge> edges = [.. this.VisibleEdges];

		// Neurons always show. Brokers and contacts only with a link that is shown, so hiding
		// the other servers also clears the brokers they lead to.
		HashSet<string> linked = [.. edges.SelectMany(e => new[] { e.From, e.To })];
		List<TopologyNode> nodes = [.. this.snapshot.Nodes.Where(n => n.Kind == TopologyNodeKind.Neuron || linked.Contains(n.Id))];

		Dictionary<Guid, AccountProfile> profiles = this.accounts.Sessions.ToDictionary(s => s.Profile.Id, s => s.Profile);
		Dictionary<string, string> subs = nodes.ToDictionary(n => n.Id, n => n.Kind switch
		{
			TopologyNodeKind.Neuron => n.Label != n.Id ? n.Id : profiles.TryGetValue(n.AccountId ?? Guid.Empty, out AccountProfile? a) ? "through " + a.Label : "Neuron",
			TopologyNodeKind.Broker => "Broker",
			_ => string.Empty,
		});
		Dictionary<string, TopologyNode> byId = nodes.ToDictionary(n => n.Id);

		IReadOnlyDictionary<string, (double X, double Y)> pos = TopologyLayout.Compute(
			[.. nodes.Select(n => n.Id)], [.. edges.Select(e => (e.From, e.To))], 150,
			id => TopologyMap.EstimateSize(byId[id].Label, subs[id], byId[id].Kind, this.Load(byId[id]).Count > 0), 26,
			[.. edges.Where(e => e.Kind == TopologyLink.Parent).Select(e => (e.From, e.To))]);

		IReadOnlyDictionary<string, EventHub.NeuronSummary> events = this.fleet.Monitors.Hub.SummarizeByNeuron(DateTime.Now);
		Dictionary<(Guid, string), WatchedNeuron> watched = this.fleet.Monitors.Neurons
			.GroupBy(n => (n.AccountId, n.BareJid.ToLowerInvariant())).ToDictionary(g => g.Key, g => g.First());

		List<MapNode> map = [];
		foreach (TopologyNode n in nodes)
		{
			(double x, double y) = pos.TryGetValue(n.Id, out var p) ? p : (0, 0);
			string sub = subs[n.Id];
			IReadOnlyList<MapLoad> load = this.Load(n);
			(double w, double h) = TopologyMap.EstimateSize(n.Label, sub, n.Kind, load.Count > 0);
			map.Add(new MapNode(n.Id, n.Label, sub, n.Kind, x, y, this.Tone(n, events, watched)) { Load = load, Width = w, Height = h });
		}

		this.Graph = new MapGraph(map, [.. edges.Select(e => new MapEdge(e.From, e.To, e.Kind, e.Label))]);
		this.HasNodes = map.Count > 0;

		int neurons = nodes.Count(n => n.Kind == TopologyNodeKind.Neuron);
		int brokers = nodes.Count(n => n.Kind == TopologyNodeKind.Broker);
		int federation = edges.Count(e => e.Kind == TopologyLink.Federation);
		this.Summary = this.snapshot.Taken == DateTime.MinValue ? "Not scanned yet"
			: string.Join(" · ", new[]
			{
				Count(neurons, "Neuron"),
				brokers > 0 ? Count(brokers, "other broker") : null,
				federation > 0 ? Count(federation, "federation link") : null,
				RelativeTime.Format(this.snapshot.Taken, DateTime.Now) is "now" ? "scanned just now" : "scanned " + RelativeTime.Format(this.snapshot.Taken, DateTime.Now),
			}.Where(s => s is not null));

		if (this.SelectedId is not null && !map.Any(m => m.Id == this.SelectedId))
			this.SelectedId = null;
		else
			this.ShowSelection();
	}

	/// <summary>CPU, free memory and free disk from Fleet, each with its own tone, for the card.</summary>
	private IReadOnlyList<MapLoad> Load(TopologyNode n)
	{
		if (n.Kind != TopologyNodeKind.Neuron || n.AccountId is not Guid a || n.NeuronJid is not string jid || this.fleet.Get(a, jid) is not NeuronHealth h)
			return [];

		List<MapLoad> load = [];
		if (h.Cpu.Latest is double cpu)
			load.Add(new MapLoad("CPU", $"{cpu:0}%", cpu >= 80 ? "bad" : cpu >= 60 ? "busy" : "ok", Math.Clamp(cpu / 100, 0, 1)));
		if (h.FreeMemory.Latest is double mem)
			load.Add(new MapLoad("RAM", mem >= 1024 ? $"{mem / 1024:0.0} GB" : $"{mem:0} MB", mem < 1024 ? "bad" : mem < 2048 ? "busy" : "ok", null));
		else if (h.AllocatedMemory.Latest is double alloc)
			load.Add(new MapLoad("Heap", alloc >= 1024 ? $"{alloc / 1024:0.0} GB" : $"{alloc:0} MB", "idle", null));
		if (h.FreeDisk.Latest is double disk)
			load.Add(new MapLoad("Disk", $"{disk:0} GB", disk < 2 ? "bad" : disk < 10 ? "busy" : "ok", null));
		return load;
	}

	private string Tone(TopologyNode n, IReadOnlyDictionary<string, EventHub.NeuronSummary> events, Dictionary<(Guid, string), WatchedNeuron> watched)
	{
		if (n.Kind != TopologyNodeKind.Neuron || n.AccountId is not Guid account || n.NeuronJid is not string jid)
			return "idle";

		if (!watched.TryGetValue((account, jid.ToLowerInvariant()), out WatchedNeuron? w) || w.State != WatchState.Live)
			return "idle";

		NeuronHealth? h = this.fleet.Get(account, jid);
		EventHub.NeuronSummary? s = events.TryGetValue(jid, out EventHub.NeuronSummary? x) ? x : null;
		if (h?.Error is not null || (s?.ErrorsLastHour ?? 0) > 0)
			return "bad";
		if ((s?.WarningsLastHour ?? 0) > 0 || (s?.OpenIssues ?? 0) > 0)
			return "busy";
		return "ok";
	}

	private void ShowSelection()
	{
		this.Facts.Clear();
		this.Connections.Clear();

		TopologyNode? n = this.Node(this.SelectedId);
		if (n is null)
		{
			this.SelectedTitle = string.Empty;
			this.SelectedIsNeuron = false;
			return;
		}

		this.SelectedTitle = n.Label;
		this.SelectedIsNeuron = n.Kind == TopologyNodeKind.Neuron;
		this.SelectedTone = this.Graph.Nodes.FirstOrDefault(m => m.Id == n.Id)?.Tone ?? "idle";
		this.SelectedKind = n.Kind switch
		{
			TopologyNodeKind.Neuron => this.SelectedTone switch
			{
				"bad" => "Neuron · errors in the last hour",
				"busy" => "Neuron · warnings or open problems",
				"ok" => "Neuron · healthy",
				_ => "Neuron · not monitored right now",
			},
			TopologyNodeKind.Broker => "Broker none of your accounts administers",
			_ => "Contact in a roster group",
		};

		this.Facts.Add(new TopologyFact("Address", n.Id));
		if (n.AccountId is Guid account && this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == account) is AccountSession session)
			this.Facts.Add(new TopologyFact("Seen through", session.Profile.Label));

		if (n.Kind == TopologyNodeKind.Neuron && n.AccountId is Guid a && n.NeuronJid is string jid)
		{
			NeuronHealth? h = this.fleet.Get(a, jid);
			if (h?.Software is string sw)
				this.Facts.Add(new TopologyFact("Software", sw));
			if (h?.Cpu.Latest is double cpu)
				this.Facts.Add(new TopologyFact("CPU", $"{cpu:F0} %"));
			if (h?.FreeMemory.Latest is double mem)
				this.Facts.Add(new TopologyFact("Free memory", $"{mem:F0} {h.FreeMemory.Unit}"));

			IReadOnlyDictionary<string, EventHub.NeuronSummary> events = this.fleet.Monitors.Hub.SummarizeByNeuron(DateTime.Now);
			if (events.TryGetValue(jid, out EventHub.NeuronSummary? s))
				this.Facts.Add(new TopologyFact("Last hour", $"{Count(s.ErrorsLastHour, "error")}, {Count(s.WarningsLastHour, "warning")}, {Count(s.OpenIssues, "open problem")}"));
		}

		// One line per connected point, with every way it is connected.
		foreach (IGrouping<string, TopologyEdge> g in this.VisibleEdges
			.Where(e => e.From == n.Id || e.To == n.Id)
			.GroupBy(e => e.From == n.Id ? e.To : e.From)
			.OrderBy(g => g.Min(e => e.Kind)).ThenBy(g => this.Node(g.Key)?.Label ?? g.Key, StringComparer.OrdinalIgnoreCase))
		{
			string relation = string.Join(", ", g.OrderBy(e => e.Kind).Select(e => e.Kind switch
			{
				TopologyLink.Parent => e.From == n.Id ? "its parent" : "connected under it",
				TopologyLink.Federation => e.Label is null ? "federated" : "federated over " + e.Label,
				_ => "in group " + (e.Label ?? "?"),
			}));
			this.Connections.Add(new TopologyConnection(g.Key, this.Node(g.Key)?.Label ?? g.Key, char.ToUpperInvariant(relation[0]) + relation[1..]));
		}
	}

	private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
}
