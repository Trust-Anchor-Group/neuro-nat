using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Concentrator;
using Neuro.AA.Engine.Events;
using Waher.Networking.XMPP.Concentrator;
using Waher.Networking.XMPP.DataForms;
using Waher.Things;
using Waher.Things.DisplayableParameters;

namespace Neuro.AA.App.ViewModels;

/// <summary>A Neuron to browse, through one account.</summary>
public sealed record NeuronChoice(Guid AccountId, string BareJid, string Label, string AccountLabel, int AccountColorIndex);

/// <summary>An item in the sources tree: a data source, a node, or a line saying it is loading.</summary>
public abstract partial class TreeItemViewModel : ObservableObject
{
	protected TreeItemViewModel(SourcesViewModel owner, TreeItemViewModel? parent)
	{
		this.Owner = owner;
		this.Parent = parent;
	}

	public SourcesViewModel Owner { get; }
	public TreeItemViewModel? Parent { get; }
	public ObservableCollection<TreeItemViewModel> Children { get; } = [];

	[ObservableProperty] private string label = string.Empty;
	[ObservableProperty] private string? meta;
	[ObservableProperty] private bool isExpanded;
	[ObservableProperty] private bool isSource;
	[ObservableProperty] private bool hasProblem;
	[ObservableProperty] private bool hasWarning;
	[ObservableProperty] private bool isPlaceholder;

	private bool loaded;

	/// <summary>Shows an expander before the children are read.</summary>
	protected void ExpectChildren(bool hasChildren)
	{
		this.Children.Clear();
		this.loaded = !hasChildren;
		if (hasChildren)
			this.Children.Add(new PlaceholderItemViewModel(this.Owner, this, "Loading…"));
	}

	partial void OnIsExpandedChanged(bool value)
	{
		if (value && !this.loaded)
		{
			this.loaded = true;
			_ = this.LoadAsync();
		}
	}

	/// <summary>Reads the children again.</summary>
	public async Task LoadAsync()
	{
		try
		{
			IReadOnlyList<TreeItemViewModel> items = await this.ReadChildrenAsync();
			this.Children.Clear();
			foreach (TreeItemViewModel i in items)
				this.Children.Add(i);
			this.loaded = true;
		}
		catch (Exception ex)
		{
			this.Children.Clear();
			this.Children.Add(new PlaceholderItemViewModel(this.Owner, this, ex.Message) { HasProblem = true });
			this.loaded = false;
		}
	}

	/// <summary>Sets the children without asking the Neuron, for previews and UI tests.</summary>
	public void SetChildren(IEnumerable<TreeItemViewModel> items)
	{
		this.Children.Clear();
		foreach (TreeItemViewModel i in items)
			this.Children.Add(i);
		this.loaded = true;
	}

	protected abstract Task<IReadOnlyList<TreeItemViewModel>> ReadChildrenAsync();
}

public sealed class PlaceholderItemViewModel : TreeItemViewModel
{
	public PlaceholderItemViewModel(SourcesViewModel owner, TreeItemViewModel? parent, string text)
		: base(owner, parent)
	{
		this.Label = text;
		this.IsPlaceholder = true;
	}

	protected override Task<IReadOnlyList<TreeItemViewModel>> ReadChildrenAsync() => Task.FromResult<IReadOnlyList<TreeItemViewModel>>([]);
}

public sealed class SourceItemViewModel : TreeItemViewModel
{
	public SourceItemViewModel(SourcesViewModel owner, TreeItemViewModel? parent, DataSourceReference source)
		: base(owner, parent)
	{
		this.Source = source;
		this.Label = string.IsNullOrWhiteSpace(source.Name) ? source.SourceID : source.Name;
		this.Meta = source.Name != source.SourceID ? source.SourceID : null;
		this.IsSource = true;
		this.ExpectChildren(true);
	}

	public DataSourceReference Source { get; }

	protected override async Task<IReadOnlyList<TreeItemViewModel>> ReadChildrenAsync()
	{
		NeuronConcentrator c = this.Owner.Concentrator ?? throw new InvalidOperationException("The Neuron is not online.");
		List<TreeItemViewModel> items = [];

		if (this.Source.HasChildren)
		{
			foreach (DataSourceReference s in await c.GetChildSourcesAsync(this.Source.SourceID))
				items.Add(new SourceItemViewModel(this.Owner, this, s));
		}

		foreach (NodeInformation n in await c.GetRootNodesAsync(this.Source.SourceID))
			items.Add(new NodeItemViewModel(this.Owner, this, n));

		return items;
	}
}

public sealed partial class NodeItemViewModel : TreeItemViewModel
{
	public NodeItemViewModel(SourcesViewModel owner, TreeItemViewModel? parent, NodeInformation node)
		: base(owner, parent)
	{
		this.Apply(node);
		this.ExpectChildren(node.HasChildren);
	}

	[ObservableProperty] private NodeInformation node = null!;

	public void Apply(NodeInformation n)
	{
		this.Node = n;
		this.Label = string.IsNullOrWhiteSpace(n.LocalId) ? n.NodeId : n.LocalId;
		this.Meta = n.DisplayName;
		this.HasProblem = n.NodeState is NodeState.ErrorSigned or NodeState.ErrorUnsigned;
		this.HasWarning = n.NodeState is NodeState.WarningSigned or NodeState.WarningUnsigned;
	}

	protected override async Task<IReadOnlyList<TreeItemViewModel>> ReadChildrenAsync()
	{
		NeuronConcentrator c = this.Owner.Concentrator ?? throw new InvalidOperationException("The Neuron is not online.");
		return [.. (await c.GetChildNodesAsync(this.Node)).Select(n => new NodeItemViewModel(this.Owner, this, n))];
	}
}

/// <summary>A displayable parameter of a node.</summary>
public sealed record ParameterRow(string Name, string Value);

/// <summary>A message on a node.</summary>
public sealed record NodeMessageRow(string Time, string Text, bool IsError, bool IsWarning);

/// <summary>A command of a node, as a button.</summary>
public sealed record CommandRow(NodeCommand Command, string Name, string Kind, string Category);

/// <summary>A node type that can be added under the selected node.</summary>
public sealed record AddableType(string Name, string Type, IAsyncRelayCommand<AddableType> AddCommand);

/// <summary>What the right side of the Sources workspace shows.</summary>
public enum InspectorMode
{
	Details,
	Form,
	Report,
	Readout,
}

/// <summary>A sensor data field from a readout.</summary>
public sealed record ReadoutRow(string Name, string Value, string Kind, string Time, bool IsProblem);

/// <summary>
/// The Sources workspace: a Neuron's data sources and nodes, the selected node's parameters,
/// messages and commands, and the Neuron's own forms for editing and adding nodes.
/// </summary>
public sealed partial class SourcesViewModel : ObservableObject, IActionSource
{
	private readonly MonitorService monitors;
	private readonly AccountManager accounts;
	private Func<Task<FormOutcome>>? submit;
	private Action<FormOutcome>? onSubmitted;

	public SourcesViewModel(MonitorService monitors, AccountManager accounts)
	{
		this.monitors = monitors;
		this.accounts = accounts;
		monitors.NeuronsChanged += (_, _) => Dispatcher.UIThread.Post(this.SyncNeurons);
		this.Neurons.CollectionChanged += (_, _) => this.OnPropertyChanged(nameof(this.HasNeurons));
	}

	public ObservableCollection<NeuronChoice> Neurons { get; } = [];
	public ObservableCollection<TreeItemViewModel> Roots { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNeuron))]
	private NeuronChoice? selectedNeuron;

	public bool HasNeuron => this.SelectedNeuron is not null;
	public bool HasNeurons => this.Neurons.Count > 0;

	/// <summary>The concentrator of the selected Neuron, when it is online through its account.</summary>
	public NeuronConcentrator? Concentrator { get; private set; }

	[ObservableProperty] private bool isLoadingTree;
	[ObservableProperty] private string? treeError;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNode))]
	private TreeItemViewModel? selectedItem;

	/// <summary>
	/// The tree's own selection. Kept apart from <see cref="SelectedItem"/> so the tree letting go
	/// of its selection (for example while search results show) does not clear the inspector.
	/// </summary>
	[ObservableProperty] private TreeItemViewModel? treeSelection;

	partial void OnTreeSelectionChanged(TreeItemViewModel? value)
	{
		if (value is not null && !value.IsPlaceholder)
			this.SelectedItem = value;
	}

	// ---- Search ---------------------------------------------------------------------------

	[ObservableProperty] private string searchText = string.Empty;
	[ObservableProperty] private bool isSearching;
	[ObservableProperty] private bool hasSearchResults;
	[ObservableProperty] private string? searchSummary;
	[ObservableProperty] private NodeItemViewModel? searchSelection;

	/// <summary>Nodes in any source of the Neuron whose id, name or type matches the search.</summary>
	public ObservableCollection<NodeItemViewModel> SearchResults { get; } = [];

	partial void OnSearchSelectionChanged(NodeItemViewModel? value)
	{
		if (value is null)
			return;

		this.SelectedItem = value;

		// Search lists nodes without their parameters; read them now.
		_ = this.RefreshNode();
	}

	[RelayCommand]
	private async Task Search()
	{
		string text = this.SearchText.Trim();
		if (text.Length == 0 || this.Concentrator is not NeuronConcentrator c)
		{
			this.ClearSearch();
			return;
		}

		this.IsSearching = true;
		this.SearchResults.Clear();
		this.HasSearchResults = true;
		this.SearchSummary = "Searching…";
		int sources = 0;

		try
		{
			foreach (SourceItemViewModel src in this.Roots.OfType<SourceItemViewModel>().ToList())
			{
				IReadOnlyList<NodeInformation> nodes;
				try
				{
					nodes = await c.GetAllNodesAsync(src.Source.SourceID);
					sources++;
				}
				catch (Exception)
				{
					continue;   // Sources this account may not list are skipped.
				}

				foreach (NodeInformation n in nodes)
				{
					if (Contains(n.LocalId, text) || Contains(n.NodeId, text) || Contains(n.DisplayName, text) || Contains(n.NodeType, text))
					{
						this.SearchResults.Add(new NodeItemViewModel(this, null, n) { Meta = src.Label + " · " + (string.IsNullOrEmpty(n.DisplayName) ? ShortType(n.NodeType) : n.DisplayName) });
						if (this.SearchResults.Count >= 500)
							break;
					}
				}
			}

			this.SearchSummary = this.SearchResults.Count switch
			{
				0 => $"Nothing matches \u201c{text}\u201d in {sources} sources.",
				1 => "1 match",
				>= 500 => "The first 500 matches",
				_ => $"{this.SearchResults.Count} matches",
			};
		}
		finally
		{
			this.IsSearching = false;
		}
	}

	private static bool Contains(string? s, string text) => s is not null && s.Contains(text, StringComparison.OrdinalIgnoreCase);

	[RelayCommand]
	private void ClearSearch()
	{
		this.SearchResults.Clear();
		this.HasSearchResults = false;
		this.SearchSummary = null;
		this.SearchSelection = null;
		this.SearchText = string.Empty;
	}

	// ---- Clipboard ------------------------------------------------------------------------

	/// <summary>Copied nodes. Shared by all Neurons, so a subtree can be copied from one Neuron to another.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasClipboard), nameof(PasteTip))]
	private NodeCopy? clipboard;

	public bool HasClipboard => this.Clipboard is not null;
	public string PasteTip => this.Clipboard is NodeCopy c
		? $"Create a copy of {c.Label}{(c.Count > 1 ? $" and the {c.Count - 1} nodes under it" : string.Empty)} under this node"
		: string.Empty;

	[RelayCommand]
	private async Task Copy()
	{
		if (this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c)
			return;

		this.IsBusy = true;
		try
		{
			this.Clipboard = await NodeCopy.CopyAsync(c, item.Node);
			this.Say(this.Clipboard.Count == 1 ? $"Copied {item.Label}." : $"Copied {item.Label} and the {this.Clipboard.Count - 1} nodes under it.");
		}
		catch (Exception ex)
		{
			this.Say(ex.Message, true);
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	[RelayCommand]
	private async Task Paste()
	{
		if (this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c || this.Clipboard is not NodeCopy copy)
			return;

		this.IsBusy = true;
		try
		{
			this.Say("Pasting…");
			NodeCopy.PasteResult r = await copy.PasteAsync(c, item.Node);
			item.IsExpanded = true;
			await item.LoadAsync();

			if (r.Top is not null && item.Children.OfType<NodeItemViewModel>().FirstOrDefault(x => x.Node.NodeId == r.Top.NodeId) is NodeItemViewModel created)
				this.TreeSelection = created;

			string text = r.Created == 1 ? "Created 1 node." : $"Created {r.Created} nodes.";
			if (r.Problems.Count > 0)
				text += " Not created: " + string.Join(" ", r.Problems);
			this.Say(text, r.Problems.Count > 0);
		}
		catch (Exception ex)
		{
			this.Say(ex.Message, true);
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	// ---- Sensor data and control -----------------------------------------------------------

	public ObservableCollection<ReadoutRow> ReadoutRows { get; } = [];

	[ObservableProperty] private string readoutTitle = string.Empty;
	[ObservableProperty] private string? readoutStatus;
	[ObservableProperty] private bool canRead;
	[ObservableProperty] private bool canControl;

	private Readout? readout;

	[RelayCommand]
	private async Task Read(string? scope)
	{
		if (this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c)
			return;

		bool all = scope == "all";
		Waher.Things.SensorData.FieldType types = all ? Waher.Things.SensorData.FieldType.All : Waher.Things.SensorData.FieldType.All & ~Waher.Things.SensorData.FieldType.Historical;

		this.ReadoutRows.Clear();
		this.ReadoutTitle = all ? "All sensor data, with history" : "Sensor data";
		this.ReadoutStatus = "Reading…";
		this.Mode = InspectorMode.Readout;

		try
		{
			Readout r = await c.ReadAsync(item.Node, types);
			this.readout = r;
			r.Changed += (_, _) => Dispatcher.UIThread.Post(() => this.ShowReadout(r));
			this.ShowReadout(r);
		}
		catch (Exception ex)
		{
			this.ReadoutStatus = ex.Message;
		}
	}

	private void ShowReadout(Readout r)
	{
		if (!ReferenceEquals(this.readout, r))
			return;

		this.ReadoutRows.Clear();
		foreach (Waher.Things.SensorData.Field f in r.Fields.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(f => f.Timestamp))
		{
			this.ReadoutRows.Add(new ReadoutRow(f.Name, f.ValueString, KindText(f.Type),
				Local(f.Timestamp).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), false));
		}

		foreach (Waher.Things.ThingError e in r.Errors)
			this.ReadoutRows.Add(new ReadoutRow("Error", e.ErrorMessage, string.Empty, Local(e.Timestamp).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), true));

		this.ReadoutStatus = r.State switch
		{
			Waher.Networking.XMPP.Sensor.SensorDataReadoutState.Done => r.Fields.Count == 1 ? "1 field" : $"{r.Fields.Count} fields",
			Waher.Networking.XMPP.Sensor.SensorDataReadoutState.Failure => r.Errors.Count > 0 ? "The node reported errors" : "The readout failed",
			Waher.Networking.XMPP.Sensor.SensorDataReadoutState.Cancelled => "Cancelled",
			_ => "Reading…",
		};
	}

	private static string KindText(Waher.Things.SensorData.FieldType t)
	{
		List<string> parts = [];
		foreach (Waher.Things.SensorData.FieldType f in new[] { Waher.Things.SensorData.FieldType.Momentary, Waher.Things.SensorData.FieldType.Identity,
			Waher.Things.SensorData.FieldType.Status, Waher.Things.SensorData.FieldType.Computed, Waher.Things.SensorData.FieldType.Peak,
			Waher.Things.SensorData.FieldType.Historical })
		{
			if ((t & f) != 0)
				parts.Add(f.ToString());
		}
		return string.Join(", ", parts);
	}

	[RelayCommand]
	private async Task Control()
	{
		if (this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c)
			return;

		this.IsBusy = true;
		this.Notice = null;
		try
		{
			DataForm f = await c.GetControlFormAsync(item.Node);
			this.OpenForm(f, "Control " + item.Label, "Values are sent to the device when you press Set.", "Set",
				() => c.SetControlAsync(item.Node, this.Form!), _ =>
				{
					this.CloseForm();
					this.Say("Values set.");
				});
		}
		catch (Exception ex)
		{
			this.Say(ex.Message, true);
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	// ---- Inspector ------------------------------------------------------------------------

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsDetails), nameof(IsForm), nameof(IsReport), nameof(IsReadout))]
	private InspectorMode mode = InspectorMode.Details;

	public bool IsDetails => this.Mode == InspectorMode.Details;
	public bool IsForm => this.Mode == InspectorMode.Form;
	public bool IsReport => this.Mode == InspectorMode.Report;
	public bool IsReadout => this.Mode == InspectorMode.Readout;

	public NodeItemViewModel? SelectedNode => this.SelectedItem as NodeItemViewModel;
	public bool HasNode => this.SelectedNode is not null;

	[ObservableProperty] private string nodeTitle = string.Empty;
	[ObservableProperty] private string nodeType = string.Empty;
	[ObservableProperty] private string nodeWhere = string.Empty;
	[ObservableProperty] private string nodeChanged = string.Empty;
	[ObservableProperty] private string? nodeStateText;
	[ObservableProperty] private bool nodeHasProblem;
	[ObservableProperty] private bool nodeHasWarning;
	[ObservableProperty] private bool canMove;
	[ObservableProperty] private bool canSniff;

	public ObservableCollection<ParameterRow> Parameters { get; } = [];
	public ObservableCollection<NodeMessageRow> Messages { get; } = [];
	public ObservableCollection<CommandRow> Commands { get; } = [];
	public ObservableCollection<AddableType> AddableTypes { get; } = [];

	[ObservableProperty] private bool hasMessages;
	[ObservableProperty] private bool hasParameters;
	[ObservableProperty] private bool hasCommands;
	[ObservableProperty] private bool isLoadingCommands;
	[ObservableProperty] private bool isLoadingAddable;
	[ObservableProperty] private bool hasAddable;
	[ObservableProperty] private string? addableNote;

	/// <summary>One line about the last thing that happened: saved, deleted, refused.</summary>
	[ObservableProperty] private string? notice;
	[ObservableProperty] private bool noticeIsError;

	[ObservableProperty] private bool deleteArmed;
	[ObservableProperty] private bool isBusy;

	// Form
	[ObservableProperty] private DataForm? form;
	[ObservableProperty] private string formTitle = string.Empty;
	[ObservableProperty] private string? formSubtitle;
	[ObservableProperty] private string submitLabel = "Save";
	[ObservableProperty] private string? formError;

	// Report
	[ObservableProperty] private QueryRun? report;
	[ObservableProperty] private string reportTitle = string.Empty;
	[ObservableProperty] private string? reportStatus;

	private bool syncing;

	/// <summary>
	/// Updates the list of Neurons that can be browsed, keeping the selection and the tree.
	/// Called when Neurons change state, and by the shell when the workspace opens.
	/// </summary>
	public void SyncNeurons()
	{
		Dictionary<Guid, AccountProfile> profiles = this.accounts.Sessions.ToDictionary(s => s.Profile.Id, s => s.Profile);
		NeuronChoice? keep = this.SelectedNeuron;

		// The Neuron being browsed stays in the list while it reconnects, so the tree is kept.
		List<NeuronChoice> live = [.. this.monitors.Neurons
			.Where(n => n.State == WatchState.Live || (keep is not null && n.AccountId == keep.AccountId && string.Equals(n.BareJid, keep.BareJid, StringComparison.OrdinalIgnoreCase)))
			.Select(n => new NeuronChoice(n.AccountId, n.BareJid, n.Label,
				profiles.TryGetValue(n.AccountId, out AccountProfile? p) ? p.Label : string.Empty,
				profiles.TryGetValue(n.AccountId, out AccountProfile? q) ? q.ColorIndex : 0))
			.OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase)];

		static bool Same(NeuronChoice a, NeuronChoice b) => a.AccountId == b.AccountId && string.Equals(a.BareJid, b.BareJid, StringComparison.OrdinalIgnoreCase);

		this.syncing = true;
		try
		{
			for (int i = this.Neurons.Count - 1; i >= 0; i--)
			{
				if (!live.Any(n => Same(n, this.Neurons[i])))
					this.Neurons.RemoveAt(i);
			}

			foreach (NeuronChoice n in live)
			{
				if (!this.Neurons.Any(x => Same(x, n)))
					this.Neurons.Add(n);
			}
		}
		finally
		{
			this.syncing = false;
		}

		// The list box may have cleared its selection while items moved; put it back without reloading.
		if (keep is not null && this.Neurons.FirstOrDefault(n => Same(n, keep)) is NeuronChoice again && !ReferenceEquals(this.SelectedNeuron, again))
		{
			this.syncing = true;
			try
			{
				this.SelectedNeuron = again;
			}
			finally
			{
				this.syncing = false;
			}
		}

		this.OnPropertyChanged(nameof(this.HasNeurons));

		if (this.SelectedNeuron is not NeuronChoice selected || !this.Neurons.Contains(selected))
		{
			this.SelectedNeuron = this.Neurons.FirstOrDefault();
			if (this.SelectedNeuron is null)
				_ = this.LoadTreeAsync();
			return;
		}

		// The Neuron may have reconnected with a new address; keep the tree, follow the address.
		NeuronConcentrator? fresh = this.monitors.For(selected.AccountId)?.Concentrator(selected.BareJid);
		if (fresh is not null && this.Concentrator is null)
			_ = this.LoadTreeAsync();
		else if (fresh is not null && (fresh.FullJid != this.Concentrator?.FullJid || !ReferenceEquals(fresh.Client, this.Concentrator?.Client)))
			this.Concentrator = fresh;
	}

	partial void OnSelectedNeuronChanged(NeuronChoice? value)
	{
		if (!this.syncing)
			_ = this.LoadTreeAsync();
	}

	[RelayCommand]
	private async Task LoadTreeAsync()
	{
		this.Roots.Clear();
		this.TreeSelection = null;
		this.SelectedItem = null;
		this.TreeError = null;
		this.SearchResults.Clear();
		this.HasSearchResults = false;
		NeuronChoice? n = this.SelectedNeuron;
		this.Concentrator = n is null ? null : this.monitors.For(n.AccountId)?.Concentrator(n.BareJid);

		if (n is null)
			return;

		if (this.Concentrator is null)
		{
			this.TreeError = "This Neuron is not online through its account right now.";
			return;
		}

		this.IsLoadingTree = true;
		try
		{
			foreach (DataSourceReference s in await this.Concentrator.GetRootSourcesAsync())
				this.Roots.Add(new SourceItemViewModel(this, null, s));

			if (this.Roots.Count == 0)
				this.TreeError = "The Neuron shows no data sources to this account.";
		}
		catch (Exception ex)
		{
			this.TreeError = ex.Message;
		}
		finally
		{
			this.IsLoadingTree = false;
		}
	}

	partial void OnSelectedItemChanged(TreeItemViewModel? value)
	{
		this.OnPropertyChanged(nameof(this.SelectedNode));
		this.Mode = InspectorMode.Details;
		this.DeleteArmed = false;
		this.Notice = null;
		this.Inspect();
	}

	private void Inspect()
	{
		this.Parameters.Clear();
		this.Messages.Clear();
		this.Commands.Clear();
		this.AddableTypes.Clear();
		this.HasAddable = false;
		this.AddableNote = null;
		this.HasCommands = false;
		this.HasMessages = false;

		if (this.SelectedNode is not NodeItemViewModel item)
			return;

		NodeInformation n = item.Node;
		this.NodeTitle = item.Label;
		this.NodeType = string.IsNullOrWhiteSpace(n.DisplayName) ? ShortType(n.NodeType) : n.DisplayName;
		this.NodeWhere = string.IsNullOrEmpty(n.Partition) ? n.SourceId : n.SourceId + " · " + n.Partition;
		this.NodeChanged = n.LastChanged == DateTime.MinValue ? string.Empty : "Changed " + Local(n.LastChanged).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
		this.NodeHasProblem = item.HasProblem;
		this.NodeHasWarning = item.HasWarning;
		this.NodeStateText = item.HasProblem ? "Error" : item.HasWarning ? "Warning" : n.NodeState == NodeState.Information ? "Information" : null;
		this.CanMove = item.Parent is NodeItemViewModel parent && parent.Node.ChildrenOrdered;
		this.CanSniff = n.Sniffable;
		this.CanRead = n.IsReadable;
		this.CanControl = n.IsControllable;

		foreach (Parameter p in n.ParameterList ?? [])
			this.Parameters.Add(new ParameterRow(p.Name, p.StringValue?.ToString() ?? string.Empty));
		this.HasParameters = this.Parameters.Count > 0;

		foreach (Message m in n.MessageList ?? [])
		{
			this.Messages.Add(new NodeMessageRow(Local(m.Timestamp).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), m.Body,
				m.Type == MessageType.Error, m.Type == MessageType.Warning));
		}
		this.HasMessages = this.Messages.Count > 0;

		if (n.HasCommands)
			_ = this.LoadCommandsAsync(item);
	}

	private async Task LoadCommandsAsync(NodeItemViewModel item)
	{
		if (this.Concentrator is not NeuronConcentrator c)
			return;

		this.IsLoadingCommands = true;
		try
		{
			IReadOnlyList<NodeCommand> cmds = await c.GetCommandsAsync(item.Node);
			if (!ReferenceEquals(this.SelectedItem, item))
				return;

			foreach (NodeCommand cmd in cmds.OrderBy(x => x.SortCategory).ThenBy(x => x.SortKey).ThenBy(x => x.Name))
			{
				this.Commands.Add(new CommandRow(cmd, cmd.Name.TrimEnd('.', '…', ' '), cmd.Type switch
				{
					CommandType.Query => "Report",
					CommandType.Parametrized => "Asks for values",
					_ => "Runs at once",
				}, cmd.SortCategory));
			}

			this.HasCommands = this.Commands.Count > 0;
		}
		catch (Exception ex)
		{
			if (ReferenceEquals(this.SelectedItem, item))
				this.Say("Commands: " + ex.Message, true);
		}
		finally
		{
			this.IsLoadingCommands = false;
		}
	}

	private static DateTime Local(DateTime t) => t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;

	private static string ShortType(string type)
	{
		int i = type.LastIndexOf('.');
		return i >= 0 ? type[(i + 1)..] : type;
	}

	private void Say(string text, bool error = false)
	{
		this.Notice = text;
		this.NoticeIsError = error;
	}

	/// <summary>Set by the shell: sends a title and Markdown to Chat to pick who gets it.</summary>
	public Action<string, string>? ShareTo { get; set; }

	[RelayCommand]
	private void Share()
	{
		if (this.SelectedNode is not NodeItemViewModel item)
			return;

		System.Text.StringBuilder sb = new();
		sb.Append("**Node** `").Append(item.Label).Append("` (").Append(this.NodeType).Append(") in `").Append(this.NodeWhere)
			.Append("` on `").Append(this.SelectedNeuron?.Label).AppendLine("`").AppendLine();
		if (this.Parameters.Count > 0)
		{
			sb.AppendLine("| Parameter | Value |");
			sb.AppendLine("|:--|:--|");
			foreach (ParameterRow p in this.Parameters)
				sb.Append("| ").Append(p.Name.Replace("|", "\\|")).Append(" | ").Append(p.Value.Replace("|", "\\|")).AppendLine(" |");
		}
		foreach (NodeMessageRow m in this.Messages)
			sb.AppendLine().Append("> ").Append(m.Time).Append(": ").Append(m.Text);

		this.ShareTo?.Invoke("Node: " + item.Label, sb.ToString());
	}

	// ---- Actions: right-click, shortcuts -------------------------------------------------

	/// <summary>Set by the shell: the moves to other screens.</summary>
	public IShell? Shell { get; set; }

	/// <summary>Opens a Neuron's sources. Used by "Go to" on other screens.</summary>
	public void Focus(Guid accountId, string neuronJid)
	{
		this.SyncNeurons();
		if (this.Neurons.FirstOrDefault(n => n.AccountId == accountId && string.Equals(n.BareJid, neuronJid, StringComparison.OrdinalIgnoreCase)) is NeuronChoice n)
			this.SelectedNeuron = n;
		else
			Ui.Say(neuronJid + " is not online, so its sources cannot be read");
	}

	private string ParametersAsText()
		=> string.Join(Environment.NewLine, this.Parameters.Select(p => p.Name + ": " + p.Value));

	private static readonly string[] readoutColumns = ["Field", "Value", "Kind", "Time"];

	private SaveFormat[] ReadoutFormats() =>
	[
		new SaveFormat("Spreadsheet (CSV)", "csv", () => Export.Csv(readoutColumns, this.ReadoutRows.Select(r => (IReadOnlyList<string?>)[r.Name, r.Value, r.Kind, r.Time]))),
		new SaveFormat("Markdown", "md", () => Export.Markdown(readoutColumns, this.ReadoutRows.Select(r => (IReadOnlyList<string?>)[r.Name, r.Value, r.Kind, r.Time]))),
	];

	/// <summary>Saves the sensor readings on screen.</summary>
	[RelayCommand]
	private Task SaveReadout() => Ui.Save(Ui.FileName("Readings " + this.NodeTitle), this.ReadoutFormats());

	[RelayCommand]
	private Task CopyReadout()
		=> Ui.Copy(Export.Tabs(readoutColumns, this.ReadoutRows.Select(r => (IReadOnlyList<string?>)[r.Name, r.Value, r.Kind, r.Time])), Ui.Count(this.ReadoutRows.Count, "reading"));

	/// <summary>Saves the tables of the report on screen.</summary>
	[RelayCommand]
	private Task SaveReport()
		=> this.Report is QueryRun run
			? Ui.Save(Ui.FileName(this.ReportTitle), [new SaveFormat("Spreadsheet (CSV)", "csv", () => Controls.ReportView.ToCsv(run))])
			: Task.CompletedTask;

	[RelayCommand]
	private Task CopyReport()
		=> this.Report is QueryRun run ? Ui.Copy(Controls.ReportView.ToCsv(run), "the report's tables") : Task.CompletedTask;

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		if (items.Count == 0)
			return [];

		ActionList list = [];

		if (items.All(i => i is ReadoutRow))
		{
			List<ReadoutRow> rows = [.. items.Cast<ReadoutRow>()];
			string what = Ui.Count(rows.Count, "reading");
			if (rows.Count == 1)
			{
				list.Copy("Copy value", () => rows[0].Value, "the value", "Ctrl+C");
				list.Copy("Copy row", () => Export.Tabs(readoutColumns, rows.Select(r => (IReadOnlyList<string?>)[r.Name, r.Value, r.Kind, r.Time]), false).TrimEnd(), "the row", "Ctrl+Shift+C");
			}
			else
				list.Copy("Copy " + what, () => Export.Tabs(readoutColumns, rows.Select(r => (IReadOnlyList<string?>)[r.Name, r.Value, r.Kind, r.Time])), what, "Ctrl+C", inBar: true);

			list.Save("Save all readings…", Ui.FileName("Readings " + this.NodeTitle), this.ReadoutFormats());
			return list;
		}

		if (items.Count != 1)
			return [];

		switch (items[0])
		{
			case ParameterRow p:
				list.Copy("Copy value", () => p.Value, "the value", "Ctrl+C");
				list.Copy("Copy name and value", () => p.Name + ": " + p.Value, p.Name);
				list.Copy("Copy all parameters", this.ParametersAsText, Ui.Count(this.Parameters.Count, "parameter"));
				break;

			case NodeMessageRow m:
				list.Copy("Copy message", () => m.Text, "the message", "Ctrl+C");
				list.Copy("Copy with time", () => m.Time + "\t" + m.Text, "the message with its time");
				break;

			case NodeSnifferViewModel watch:
				list.Copy("Copy node name", () => watch.NodeLabel, "the node's name", "Ctrl+C");
				list.Do("Stop watching", () => this.CloseSnifferCommand.Execute(watch), icon: "Close");
				list.Window("Open in its own window", () => Ui.OpenWindow?.Invoke(watch.Lines));
				break;

			case SourceItemViewModel source:
				list.Copy("Copy name", () => source.Label, "the name", "Ctrl+C");
				list.Do("Read again", () => _ = source.LoadAsync(), icon: "Refresh");
				break;

			case NodeItemViewModel node:
				// The menu acts on the node the tree has selected, which a right-click makes this one.
				bool current = ReferenceEquals(this.SelectedNode, node);
				list.Copy("Copy name", () => node.Label, "the name", "Ctrl+C");
				list.Copy("Copy path", () => Path(node), "the path");
				if (current)
					list.Copy("Copy parameters as text", this.ParametersAsText, Ui.Count(this.Parameters.Count, "parameter"), "Ctrl+Shift+C");

				if (current && this.CanSniff)
					list.GoTo("Its traffic", () => this.SniffCommand.Execute(null), "Sniffer");
				if (this.Shell is IShell shell && this.SelectedNeuron is NeuronChoice neuron)
					list.GoTo("Events that mention it", () => shell.ShowEvents(neuron.BareJid, node.Node.NodeId), "Events");

				if (!current)
					break;

				list.Do("Edit", () => this.EditCommand.Execute(null), icon: "Edit", key: "F2");
				if (this.AddableTypes.Count > 0)
				{
					list.Sub("Add under it", ActionGroup.Do, [.. this.AddableTypes.Take(40).Select(t =>
						new ItemAction(t.Name, ActionGroup.Do, () => t.AddCommand.Execute(t)))], "Plus");
				}
				else
					list.Do("Add under it…", () => this.LoadAddableThenSay(), icon: "Plus");

				if (this.CanRead)
				{
					list.Do("Read", () => this.ReadCommand.Execute("momentary"));
					list.Do("Read history", () => this.ReadCommand.Execute("all"));
				}

				if (this.CanControl)
					list.Do("Control", () => this.ControlCommand.Execute(null));

				list.Do("Copy node and what is under it", () => this.CopyCommand.Execute(null), icon: "Copy");
				if (this.HasClipboard)
					list.Do("Paste under it", () => this.PasteCommand.Execute(null));
				if (this.CanMove)
				{
					list.Do("Move up", () => this.MoveUpCommand.Execute(null), key: "Alt+Up");
					list.Do("Move down", () => this.MoveDownCommand.Execute(null), key: "Alt+Down");
				}

				list.Do("Read again", () => this.RefreshNodeCommand.Execute(null), icon: "Refresh", key: "F5");
				list.Do("Share to chat…", () => this.ShareCommand.Execute(null), icon: "Share");
				list.Do(this.DeleteArmed ? "Delete now" : "Delete…", () => this.DeleteCommand.Execute(null), danger: true, icon: "Trash", key: "Delete");
				break;
		}

		return list;
	}

	private async void LoadAddableThenSay()
	{
		await this.LoadAddableCommand.ExecuteAsync(null);
		Ui.Say(this.AddableTypes.Count > 0 ? "Right-click again, or use Add above, to pick what to add" : "Nothing can be added under this node");
	}

	private static string Path(TreeItemViewModel item)
	{
		List<string> parts = [];
		for (TreeItemViewModel? i = item; i is not null; i = i.Parent)
			parts.Insert(0, i.Label);

		return string.Join(" / ", parts);
	}

	// ---- Sniffers -------------------------------------------------------------------------

	/// <summary>Running sniffers, one tab each. They keep running while other nodes are looked at.</summary>
	public ObservableCollection<NodeSnifferViewModel> Sniffers { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSniffers))]
	private NodeSnifferViewModel? selectedSniffer;

	public bool HasSniffers => this.Sniffers.Count > 0;

	[RelayCommand]
	private async Task Sniff()
	{
		if (this.SelectedNode is not NodeItemViewModel item || this.SelectedNeuron is not NeuronChoice neuron)
			return;

		NodeSnifferViewModel? open = this.Sniffers.FirstOrDefault(s => s.Matches(neuron.AccountId, neuron.BareJid, item.Node));
		if (open is not null)
		{
			this.SelectedSniffer = open;
			return;
		}

		Guid account = neuron.AccountId;
		string jid = neuron.BareJid;
		NodeSniffer sniffer = new(() => this.monitors.For(account)?.Concentrator(jid), item.Node, item.Label);
		NodeSnifferViewModel vm = new(sniffer, account, jid, item.Label, neuron.Label);
		this.Sniffers.Add(vm);
		this.SelectedSniffer = vm;
		this.OnPropertyChanged(nameof(this.HasSniffers));
		await sniffer.StartAsync();
	}

	[RelayCommand]
	private async Task CloseSniffer(NodeSnifferViewModel? vm)
	{
		vm ??= this.SelectedSniffer;
		if (vm is null)
			return;

		int i = this.Sniffers.IndexOf(vm);
		this.Sniffers.Remove(vm);
		if (ReferenceEquals(this.SelectedSniffer, vm) || this.SelectedSniffer is null)
			this.SelectedSniffer = this.Sniffers.Count == 0 ? null : this.Sniffers[Math.Clamp(i, 0, this.Sniffers.Count - 1)];
		this.OnPropertyChanged(nameof(this.HasSniffers));
		await vm.DisposeAsync();
	}

	/// <summary>Stops every sniffer. Called when the app closes.</summary>
	public async Task StopSniffersAsync()
	{
		foreach (NodeSnifferViewModel s in this.Sniffers.ToList())
			await s.DisposeAsync();
		this.Sniffers.Clear();
	}

	// ---- Commands -------------------------------------------------------------------------

	[RelayCommand]
	private async Task RefreshNode()
	{
		if (this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c)
			return;

		try
		{
			item.Apply(await c.GetNodeAsync(item.Node));
			if (item.Node.HasChildren || item.Children.Count > 0)
				await item.LoadAsync();
			this.Inspect();
		}
		catch (Exception ex)
		{
			this.Say(ex.Message, true);
		}
	}

	[RelayCommand]
	private async Task Run(CommandRow? row)
	{
		if (row is null || this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c)
			return;

		NodeCommand cmd = row.Command;
		this.Notice = null;

		try
		{
			if (cmd.Type == CommandType.Simple)
			{
				if (!string.IsNullOrWhiteSpace(cmd.ConfirmationString) && !this.ConfirmPending(row))
					return;

				this.IsBusy = true;
				FormOutcome r = await c.ExecuteAsync(item.Node, cmd);
				this.Say(r.Ok ? (string.IsNullOrWhiteSpace(cmd.SuccessString) ? row.Name + ": done." : cmd.SuccessString) : r.Error ?? "The command failed.", !r.Ok);
				if (r.Ok)
					await this.RefreshNode();
				return;
			}

			this.IsBusy = true;
			DataForm f = await c.GetCommandFormAsync(item.Node, cmd);

			if (cmd.Type == CommandType.Query)
			{
				this.OpenForm(f, row.Name, item.Label, "Run report", null, null, run: async () =>
				{
					QueryRun run = await c.StartQueryAsync(item.Node, cmd, this.Form);
					this.ShowReport(run, row.Name);
				});
			}
			else
			{
				this.OpenForm(f, row.Name, item.Label, "Run", () => c.ExecuteAsync(item.Node, cmd, this.Form), r =>
				{
					this.Mode = InspectorMode.Details;
					this.Say(string.IsNullOrWhiteSpace(cmd.SuccessString) ? row.Name + ": done." : cmd.SuccessString);
					_ = this.RefreshNode();
				});
			}
		}
		catch (Exception ex)
		{
			this.Say(ex.Message, true);
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	private CommandRow? confirming;

	/// <summary>Commands that ask for confirmation run on the second click.</summary>
	private bool ConfirmPending(CommandRow row)
	{
		if (ReferenceEquals(this.confirming, row))
		{
			this.confirming = null;
			return true;
		}

		this.confirming = row;
		this.Say(row.Command.ConfirmationString + " Click again to go ahead.");
		return false;
	}

	private Func<Task>? runReport;

	private void OpenForm(DataForm f, string title, string? subtitle, string submitLabel, Func<Task<FormOutcome>>? submit, Action<FormOutcome>? onSubmitted, Func<Task>? run = null)
	{
		this.Form = f;
		this.FormTitle = title;
		this.FormSubtitle = subtitle;
		this.SubmitLabel = submitLabel;
		this.FormError = null;
		this.submit = submit;
		this.onSubmitted = onSubmitted;
		this.runReport = run;
		this.Mode = InspectorMode.Form;
	}

	[RelayCommand]
	private async Task SubmitForm()
	{
		if (this.Form is null)
			return;

		this.IsBusy = true;
		this.FormError = null;
		try
		{
			if (this.runReport is Func<Task> run)
			{
				await run();
				return;
			}

			if (this.submit is null)
				return;

			FormOutcome r = await this.submit();
			if (r.Ok)
				this.onSubmitted?.Invoke(r);
			else if (r.Form is not null)
			{
				this.Form = r.Form;
				this.FormError = "The Neuron did not accept some values. See the remarks under the fields.";
			}
			else
				this.FormError = r.Error ?? "The Neuron refused.";
		}
		catch (Exception ex)
		{
			this.FormError = ex.Message;
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	[RelayCommand]
	private void CloseForm()
	{
		this.Form = null;
		this.submit = null;
		this.onSubmitted = null;
		this.runReport = null;
		this.Mode = InspectorMode.Details;
	}

	private void ShowReport(QueryRun run, string title)
	{
		this.Report?.Dispose();
		this.Report = run;
		this.ReportTitle = title;
		this.ReportStatus = "Running…";
		run.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
		{
			if (!ReferenceEquals(this.Report, run))
				return;

			this.ReportTitle = string.IsNullOrWhiteSpace(run.Title) ? title : run.Title;
			this.ReportStatus = run.State switch
			{
				QueryRunState.Running => run.Status ?? "Running…",
				QueryRunState.Done => run.RowCount == 1 ? "1 row" : $"{run.RowCount:N0} rows",
				QueryRunState.Aborted => "Stopped by the Neuron",
				_ => run.Error ?? "Failed",
			};

			if (run.State == QueryRunState.Failed && run.Rejected is DataForm rejected)
			{
				this.Form = rejected;
				this.FormError = "The Neuron did not accept some values. See the remarks under the fields.";
				this.Mode = InspectorMode.Form;
			}
		});
		this.Mode = InspectorMode.Report;
	}

	[RelayCommand]
	private void CloseReport()
	{
		this.Mode = this.Form is not null ? InspectorMode.Form : InspectorMode.Details;
	}

	[RelayCommand]
	private void BackToNode()
	{
		this.CloseForm();
		this.Report?.Dispose();
		this.Report = null;
	}

	[RelayCommand]
	private async Task Edit()
	{
		if (this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c)
			return;

		this.IsBusy = true;
		this.Notice = null;
		try
		{
			FormSession s = await c.EditAsync(item.Node);
			this.OpenForm(s.Form, "Edit " + item.Label, this.NodeType, "Save", async () =>
			{
				FormOutcome r = await s.SubmitAsync();
				return r;
			}, r =>
			{
				if (r.Node is not null)
					item.Apply(r.Node);
				this.CloseForm();
				this.Inspect();
				this.Say("Saved.");
			});
		}
		catch (Exception ex)
		{
			this.Say(ex.Message, true);
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	[RelayCommand]
	private async Task LoadAddable()
	{
		if (this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c)
			return;

		this.IsLoadingAddable = true;
		this.AddableTypes.Clear();
		this.AddableNote = null;
		try
		{
			foreach (LocalizedString t in (await c.GetAddableTypesAsync(item.Node)).OrderBy(t => t.Localized, StringComparer.CurrentCultureIgnoreCase))
				this.AddableTypes.Add(new AddableType(t.Localized, t.Unlocalized, this.AddCommand));

			this.HasAddable = this.AddableTypes.Count > 0;
			if (!this.HasAddable)
			{
				this.AddableNote = "Nothing can be added under this node.";
				this.Say(this.AddableNote);
			}
		}
		catch (Exception ex)
		{
			this.AddableNote = ex.Message;
			this.Say(ex.Message, true);
		}
		finally
		{
			this.IsLoadingAddable = false;
		}
	}

	[RelayCommand]
	private async Task Add(AddableType? type)
	{
		if (type is null)
			return;

		if (this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c)
			return;

		this.IsBusy = true;
		this.Notice = null;
		try
		{
			FormSession s = await c.AddAsync(item.Node, type.Type);
			this.OpenForm(s.Form, "New " + type.Name, "Under " + item.Label, "Create", s.SubmitAsync, async r =>
			{
				this.CloseForm();
				item.IsExpanded = true;
				await item.LoadAsync();
				NodeItemViewModel? created = r.Node is null ? null : item.Children.OfType<NodeItemViewModel>().FirstOrDefault(x => x.Node.NodeId == r.Node.NodeId);
				if (created is not null)
					this.TreeSelection = created;
				this.Say(type.Name + " created.");
			});
		}
		catch (Exception ex)
		{
			this.Say(ex.Message, true);
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	[RelayCommand]
	private async Task Delete()
	{
		if (this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c)
			return;

		if (!this.DeleteArmed)
		{
			this.DeleteArmed = true;
			this.Say($"Delete {item.Label} and everything under it? Click Delete again to go ahead.", true);
			return;
		}

		this.DeleteArmed = false;
		this.IsBusy = true;
		try
		{
			await c.DestroyAsync(item.Node);
			TreeItemViewModel? parent = item.Parent;
			if (parent is not null)
			{
				parent.Children.Remove(item);
				this.TreeSelection = parent;
			}
			this.Say(item.Label + " deleted.");
		}
		catch (Exception ex)
		{
			this.Say(ex.Message, true);
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	[RelayCommand]
	private Task MoveUp() => this.MoveAsync(-1);

	[RelayCommand]
	private Task MoveDown() => this.MoveAsync(1);

	private async Task MoveAsync(int step)
	{
		if (this.SelectedNode is not NodeItemViewModel item || this.Concentrator is not NeuronConcentrator c || item.Parent is not TreeItemViewModel parent)
			return;

		try
		{
			if (step < 0)
				await c.MoveUpAsync(item.Node);
			else
				await c.MoveDownAsync(item.Node);

			int i = parent.Children.IndexOf(item);
			int j = Math.Clamp(i + step, 0, parent.Children.Count - 1);
			if (i >= 0 && i != j)
			{
				parent.Children.Move(i, j);
				this.TreeSelection = item;
			}
		}
		catch (Exception ex)
		{
			this.Say(ex.Message, true);
		}
	}
}
