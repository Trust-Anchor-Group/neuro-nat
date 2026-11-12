using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.ViewModels;

/// <summary>One Neuron in the fleet overview.</summary>
public sealed partial class FleetRowViewModel : ObservableObject
{
	public FleetRowViewModel(Guid accountId, string bareJid)
	{
		this.AccountId = accountId;
		this.BareJid = bareJid;
	}

	public Guid AccountId { get; }
	public string BareJid { get; }

	/// <summary>What the last check for updates came to: "Update ready", "Up to date". Empty until one was asked for.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasUpdateNote))]
	private string updateNote = string.Empty;

	[ObservableProperty] private string updateTone = "idle";

	public bool HasUpdateNote => this.UpdateNote.Length > 0;

	[ObservableProperty] private string label = string.Empty;
	[ObservableProperty] private string accountLabel = string.Empty;
	[ObservableProperty] private int accountColorIndex;

	/// <summary>Whether the account is worth naming: only when there are several.</summary>
	[ObservableProperty] private bool showAccount;
	[ObservableProperty] private WatchState state;
	[ObservableProperty] private string stateText = string.Empty;

	/// <summary>The stream in two lines: what it does ("Listening"), and what it read before ("180 earlier events loaded").</summary>
	[ObservableProperty] private string stateLine = string.Empty;
	[ObservableProperty] private string historyLine = string.Empty;
	[ObservableProperty] private string software = "–";
	[ObservableProperty] private string cpuText = "–";
	[ObservableProperty] private int[] cpuSeries = [];
	[ObservableProperty] private string memoryText = "–";
	[ObservableProperty] private string diskText = "–";
	[ObservableProperty] private int errorsLastHour;
	[ObservableProperty] private int warningsLastHour;
	[ObservableProperty] private int openIssues;
	[ObservableProperty] private string lastEventText = "–";
	[ObservableProperty] private string? note;
	[ObservableProperty] private bool hasErrors;
	[ObservableProperty] private bool highCpu;
	[ObservableProperty] private bool lowDisk;
	[ObservableProperty] private bool lowMemory;

	// The numbers behind the texts, for sorting. Null when the Neuron has not said.
	internal double? Cpu { get; private set; }
	internal double? UsedMemory { get; private set; }
	internal double? FreeDisk { get; private set; }
	internal DateTime? LastEvent { get; private set; }

	private static string Mb(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";

	internal void Apply(WatchedNeuron n, AccountProfile? account)
	{
		this.Label = n.Label;
		this.AccountLabel = account?.ShortLabel ?? string.Empty;
		this.AccountColorIndex = account?.ColorIndex ?? 0;
		this.State = n.State;
		this.StateText = n.StatusText;
		this.StateLine = n.StateLine;
		this.HistoryLine = n.HistoryLine;
		this.Note = n.Error;
	}

	internal void Apply(NeuronHealth? h)
	{
		if (h is null)
			return;

		this.Software = h.Software ?? "–";
		this.Cpu = h.Cpu.Latest;
		this.UsedMemory = h.AllocatedMemory.Latest;
		this.FreeDisk = h.FreeDisk.Latest;

		if (h.Cpu.Latest is double cpu)
		{
			this.CpuText = cpu.ToString("0", CultureInfo.CurrentCulture) + " %";
			this.CpuSeries = [.. h.Cpu.Series.Select(v => (int)Math.Round(v))];
		}
		else
		{
			this.CpuText = "–";
			this.CpuSeries = [];
		}

		this.MemoryText = (h.AllocatedMemory.Latest, h.FreeMemory.Latest) switch
		{
			(double a, double f) => $"{Mb(a)} used, {Mb(f)} free",
			(double a, null) => $"{Mb(a)} used",
			(null, double f) => $"{Mb(f)} free",
			_ => "–",
		};

		this.DiskText = h.FreeDisk.Latest is double d ? $"{d:0.0} GB free" : "–";

		// The same thresholds the Neuron itself alerts on.
		this.HighCpu = h.Cpu.Latest >= 80;
		this.LowMemory = h.FreeMemory.Latest is double fm && fm < 1024;
		this.LowDisk = h.FreeDisk.Latest is double fd && fd < 2;

		bool noSamples = h.Cpu.Latest is null && h.AllocatedMemory.Latest is null && h.FreeMemory.Latest is null && h.FreeDisk.Latest is null;
		this.Note = h.Error ?? (noSamples ? "No performance samples" : null);
	}

	internal void Apply(EventHub.NeuronSummary? s, DateTime now)
	{
		this.ErrorsLastHour = s?.ErrorsLastHour ?? 0;
		this.WarningsLastHour = s?.WarningsLastHour ?? 0;
		this.OpenIssues = s?.OpenIssues ?? 0;
		this.HasErrors = this.ErrorsLastHour > 0;
		this.LastEvent = s?.LastEvent;
		this.LastEventText = s?.LastEvent is DateTime t ? RelativeTime.Format(t, now) : "–";
	}
}

/// <summary>
/// The Fleet workspace: every Neuron reachable through any account, with the state of its
/// event stream, its health, and how much is going wrong on it.
/// </summary>
public sealed partial class FleetViewModel : ObservableObject, IDisposable, IActionSource
{
	private readonly FleetService fleet;
	private readonly AccountManager accounts;
	private readonly DispatcherTimer timer;
	private int dirty = 1;

	public FleetViewModel(FleetService fleet, AccountManager accounts)
	{
		this.fleet = fleet;
		this.accounts = accounts;
		fleet.Monitors.NeuronsChanged += this.MarkDirty;
		fleet.HealthChanged += this.Fleet_HealthChanged;
		fleet.Monitors.Hub.IssueChanged += this.Hub_IssueChanged;

		this.timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => this.Tick());
		this.timer.Start();
	}

	/// <summary>Every Neuron. The shell's Neuron chips are built from this.</summary>
	public ObservableCollection<FleetRowViewModel> Rows { get; } = [];

	/// <summary>The Neurons the list shows: all of them, or those the filter box matches.</summary>
	public ObservableCollection<FleetRowViewModel> Shown { get; } = [];

	[ObservableProperty] private string filterText = string.Empty;
	[ObservableProperty] private bool hasShown;

	partial void OnFilterTextChanged(string value) => this.SyncShown();

	// ----- Sorting: click a column's name. Click it again for the other way round. -----

	/// <summary>The column the list is sorted by: name, stream, version, cpu, memory, disk, errors, issues or last.</summary>
	public string SortKey { get; private set; } = "name";

	/// <summary>Largest, latest or Z first.</summary>
	public bool SortDescending { get; private set; }

	/// <summary>The sort as it is remembered: the column, with "-" after it for largest first. Null for by name.</summary>
	public string? Sort
	{
		get => this.SortKey == "name" && !this.SortDescending ? null : this.SortKey + (this.SortDescending ? "-" : string.Empty);
		set
		{
			string key = value?.TrimEnd('-') ?? "name";
			if (!sortColumns.Contains(key))
				key = "name";

			this.SortKey = key;
			this.SortDescending = value is not null && value.EndsWith('-') && key == value.TrimEnd('-');
			this.SortChanged();
		}
	}

	private static readonly string[] sortColumns = ["name", "stream", "version", "cpu", "memory", "disk", "errors", "issues", "last"];

	/// <summary>Raised when the person sorts the list, so the choice can be remembered.</summary>
	public event EventHandler? Sorted;

	// The way a column sorts on the first click: where the trouble is. Most CPU, least disk, most errors, latest event.
	private static bool StartsDescending(string key) => key is "cpu" or "memory" or "errors" or "issues" or "last";

	[RelayCommand]
	private void SortBy(string? key)
	{
		if (key is null || !sortColumns.Contains(key))
			return;

		if (this.SortKey == key)
			this.SortDescending = !this.SortDescending;
		else
		{
			this.SortKey = key;
			this.SortDescending = StartsDescending(key);
		}

		this.SortChanged();
		this.Sorted?.Invoke(this, EventArgs.Empty);
	}

	private void SortChanged()
	{
		foreach (string name in new[] { nameof(this.NameArrow), nameof(this.StreamArrow), nameof(this.VersionArrow), nameof(this.CpuArrow), nameof(this.MemoryArrow),
			nameof(this.DiskArrow), nameof(this.ErrorsArrow), nameof(this.IssuesArrow), nameof(this.LastArrow), nameof(this.SortKey), nameof(this.SortDescending) })
		{
			this.OnPropertyChanged(name);
		}

		this.Refresh();
	}

	private string Arrow(string key) => this.SortKey != key ? string.Empty : this.SortDescending ? "↓" : "↑";

	public string NameArrow => this.Arrow("name");
	public string StreamArrow => this.Arrow("stream");
	public string VersionArrow => this.Arrow("version");
	public string CpuArrow => this.Arrow("cpu");
	public string MemoryArrow => this.Arrow("memory");
	public string DiskArrow => this.Arrow("disk");
	public string ErrorsArrow => this.Arrow("errors");
	public string IssuesArrow => this.Arrow("issues");
	public string LastArrow => this.Arrow("last");

	/// <summary>The rows in the order asked for. A Neuron that has not said its number goes last either way, and names break ties.</summary>
	private List<FleetRowViewModel> InOrder(IEnumerable<FleetRowViewModel> rows)
	{
		List<FleetRowViewModel> byName = [.. rows.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.AccountLabel, StringComparer.OrdinalIgnoreCase)];
		bool down = this.SortDescending;

		List<FleetRowViewModel> By<T>(Func<FleetRowViewModel, T?> value)
			where T : struct, IComparable<T>
		{
			IEnumerable<FleetRowViewModel> known = byName.Where(r => value(r) is not null);
			known = down ? known.OrderByDescending(r => value(r)!.Value) : known.OrderBy(r => value(r)!.Value);
			return [.. known, .. byName.Where(r => value(r) is null)];
		}

		List<FleetRowViewModel> ByText(Func<FleetRowViewModel, string> value)
			=> down ? [.. byName.OrderByDescending(value, StringComparer.OrdinalIgnoreCase)] : [.. byName.OrderBy(value, StringComparer.OrdinalIgnoreCase)];

		return this.SortKey switch
		{
			"stream" => ByText(r => r.StateText),
			"version" => ByText(r => r.Software),
			"cpu" => By(r => r.Cpu),
			"memory" => By(r => r.UsedMemory),
			"disk" => By(r => r.FreeDisk),
			"errors" => By<int>(r => r.ErrorsLastHour),
			"issues" => By<int>(r => r.OpenIssues),
			"last" => By(r => r.LastEvent),
			_ => down ? [.. byName.AsEnumerable().Reverse()] : byName,
		};
	}

	/// <summary>The bare JIDs of the Neurons to show; empty for all. Set by the shell from its Neuron filter.</summary>
	public IReadOnlySet<string> NeuronFilter
	{
		get => this.neuronFilter;
		set
		{
			value ??= NeuronSet.All;
			if (value.SameAs(this.neuronFilter))
				return;

			this.neuronFilter = value;
			this.SyncShown();
		}
	}

	private IReadOnlySet<string> neuronFilter = NeuronSet.All;

	private bool Matches(FleetRowViewModel r)
	{
		if (!this.neuronFilter.Admits(r.BareJid))
			return false;

		string q = this.FilterText.Trim();
		return q.Length == 0
			|| r.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
			|| r.BareJid.Contains(q, StringComparison.OrdinalIgnoreCase)
			|| r.AccountLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
			|| r.Software.Contains(q, StringComparison.OrdinalIgnoreCase)
			|| r.StateText.Contains(q, StringComparison.OrdinalIgnoreCase);
	}

	private void SyncShown()
	{
		List<FleetRowViewModel> want = [.. this.Rows.Where(this.Matches)];

		for (int i = this.Shown.Count - 1; i >= 0; i--)
		{
			if (!want.Contains(this.Shown[i]))
				this.Shown.RemoveAt(i);
		}

		for (int i = 0; i < want.Count; i++)
		{
			int existing = this.Shown.IndexOf(want[i]);
			if (existing < 0)
				this.Shown.Insert(i, want[i]);
			else if (existing != i)
				this.Shown.Move(existing, i);
		}

		this.HasShown = this.Shown.Count > 0;
	}

	// ----- Actions -----

	/// <summary>Set by the shell: the moves to other screens, and what can be done to a Neuron.</summary>
	public IShell? Shell { get; set; }

	/// <summary>Selects a Neuron in the list, clearing a filter that would hide it.</summary>
	public void Select(string bareJid)
	{
		FleetRowViewModel? row = this.Rows.FirstOrDefault(r => string.Equals(r.BareJid, bareJid, StringComparison.OrdinalIgnoreCase));
		if (row is null)
			return;

		if (!this.Shown.Contains(row))
			this.FilterText = string.Empty;

		this.SelectedRow = row;
	}

	private static readonly string[] columns = ["Neuron", "Account", "Stream", "Version", "CPU", "Memory", "Disk", "Errors 1 h", "Open problems", "Last event"];

	private static string?[] Cells(FleetRowViewModel r)
		=> [r.BareJid, r.AccountLabel, r.StateText, r.Software, r.CpuText, r.MemoryText, r.DiskText, r.ErrorsLastHour.ToString(), r.OpenIssues.ToString(), r.LastEventText];

	/// <summary>One Neuron's state in a few lines, for pasting into chat or a ticket.</summary>
	public static string Summarize(FleetRowViewModel r)
	{
		System.Text.StringBuilder sb = new();
		sb.Append(r.BareJid).Append(": ").AppendLine(r.StateText);
		sb.Append("Version: ").AppendLine(r.Software);
		sb.Append("CPU: ").Append(r.CpuText).Append(", memory: ").Append(r.MemoryText).Append(", disk: ").AppendLine(r.DiskText);
		sb.Append("Errors in the last hour: ").Append(r.ErrorsLastHour).Append(", open problems: ").Append(r.OpenIssues).Append(", last event: ").Append(r.LastEventText);
		if (!string.IsNullOrEmpty(r.Note))
			sb.AppendLine().Append(r.Note);

		return sb.ToString();
	}

	private static SaveFormat[] Formats(IReadOnlyList<FleetRowViewModel> rows) =>
	[
		new SaveFormat("Spreadsheet (CSV)", "csv", () => Export.Csv(columns, rows.Select(Cells))),
		new SaveFormat("Markdown", "md", () => Export.Markdown(columns, rows.Select(Cells))),
	];

	/// <summary>Toolbar Save: the whole list as the filter shows it.</summary>
	[RelayCommand]
	private Task Save() => Ui.Save(Ui.FileName("Neurons"), Formats([.. this.Shown]));

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		List<FleetRowViewModel> rows = [.. items.OfType<FleetRowViewModel>()];
		if (rows.Count == 0)
			return [];

		ActionList list = [];
		if (rows.Count == 1)
		{
			FleetRowViewModel row = rows[0];
			list.Open("Open", () => this.Open(row), icon: "Fleet");
			if (this.Shell is IShell shell)
				list.AddRange(shell.NeuronActions(row.BareJid, row.AccountId, WorkspaceKind.Fleet));
			else
				list.Copy("Copy address", () => row.BareJid, "the address", "Ctrl+C");

			return list;
		}

		string what = Ui.Count(rows.Count, "Neuron");
		list.Copy("Copy addresses", () => string.Join(Environment.NewLine, rows.Select(r => r.BareJid)), what, "Ctrl+C", inBar: true);
		list.Copy("Copy as a table", () => Export.Tabs(columns, rows.Select(Cells)), what + " as a table", "Ctrl+Shift+C");
		list.Save("Save " + what + "…", Ui.FileName("Neurons"), Formats(rows));
		list.Add(new ItemAction("Run a command on these…", ActionGroup.Do, () => this.Shell?.ShowConsole([.. rows.Select(r => (r.AccountId, r.BareJid))]))
		{
			InBar = true,
			BarText = "Console",
			Icon = "Console",
			IsEnabled = this.Shell is not null,
		});
		if (rows.Count == 2)
		{
			list.Add(new ItemAction("Compare", ActionGroup.Do, () => this.Shell?.CompareNeurons((rows[0].AccountId, rows[0].BareJid), (rows[1].AccountId, rows[1].BareJid)))
			{
				InBar = true,
				BarText = "Compare",
				Icon = "Compare",
				IsEnabled = this.Shell is not null,
			});
		}

		list.Do("Read their health now", () => this.RefreshNowCommand.Execute(null), icon: "Refresh");
		list.Add(new ItemAction("Check for updates", ActionGroup.Do, () => this.Shell?.CheckUpdatesAsync([.. rows.Select(r => (r.AccountId, r.BareJid))]) ?? Task.CompletedTask)
		{
			InBar = true,
			BarText = "Check for updates",
			Icon = "Updates",
			IsEnabled = this.Shell is not null,
		});
		return list;
	}

	[ObservableProperty] private string summary = "No Neurons yet";
	[ObservableProperty] private bool hasRows;
	[ObservableProperty] private FleetRowViewModel? selectedRow;

	/// <summary>Set by the shell: opens the Events workspace filtered to one Neuron.</summary>
	public Action<string>? ShowEventsFor { get; set; }

	private int tick;

	private void MarkDirty(object? sender, EventArgs e) => Interlocked.Exchange(ref this.dirty, 1);
	private void Fleet_HealthChanged(object? sender, NeuronHealth e) => Interlocked.Exchange(ref this.dirty, 1);
	private void Hub_IssueChanged(object? sender, Issue e) => Interlocked.Exchange(ref this.dirty, 1);

	private void Tick()
	{
		// Rebuild when something changed, and at least every 15 s so relative times stay fresh.
		if (Interlocked.Exchange(ref this.dirty, 0) == 1 || ++this.tick % 15 == 0)
			this.Refresh();
	}

	/// <summary>Rebuilds the rows from the engine. UI thread.</summary>
	public void Refresh()
	{
		DateTime now = DateTime.Now;
		IReadOnlyList<WatchedNeuron> neurons = this.fleet.Monitors.Neurons;
		IReadOnlyDictionary<string, EventHub.NeuronSummary> summaries = this.fleet.Monitors.Hub.SummarizeByNeuron(now);
		Dictionary<Guid, AccountProfile> profiles = this.accounts.Sessions.ToDictionary(s => s.Profile.Id, s => s.Profile);

		IReadOnlyDictionary<string, string> names = NeuronNames.Distinct(neurons);
		Converters.NeuronNameConverter.Update(names);

		List<FleetRowViewModel> keep = [];
		foreach (WatchedNeuron n in neurons.OrderBy(n => names[n.BareJid], StringComparer.OrdinalIgnoreCase))
		{
			FleetRowViewModel row = this.Rows.FirstOrDefault(r => r.AccountId == n.AccountId && string.Equals(r.BareJid, n.BareJid, StringComparison.OrdinalIgnoreCase))
				?? new FleetRowViewModel(n.AccountId, n.BareJid);

			row.Apply(n, profiles.TryGetValue(n.AccountId, out AccountProfile? p) ? p : null);
			row.Label = names[n.BareJid];
			row.ShowAccount = profiles.Count > 1;
			row.Apply(this.fleet.Get(n.AccountId, n.BareJid));
			row.Apply(summaries.TryGetValue(n.BareJid, out EventHub.NeuronSummary? s) ? s : null, now);
			keep.Add(row);
		}

		keep = this.InOrder(keep);

		for (int i = this.Rows.Count - 1; i >= 0; i--)
		{
			if (!keep.Contains(this.Rows[i]))
				this.Rows.RemoveAt(i);
		}

		for (int i = 0; i < keep.Count; i++)
		{
			int existing = this.Rows.IndexOf(keep[i]);
			if (existing < 0)
				this.Rows.Insert(i, keep[i]);
			else if (existing != i)
				this.Rows.Move(existing, i);
		}

		int live = neurons.Count(n => n.State == WatchState.Live);
		int failing = keep.Count(r => r.HasErrors);
		this.SyncShown();
		this.HasRows = keep.Count > 0;
		this.Summary = keep.Count == 0
			? "No Neurons yet"
			: $"{keep.Count} Neuron{(keep.Count == 1 ? "" : "s")}, {live} reporting" + (failing > 0 ? $", {failing} with errors in the last hour" : string.Empty);
		this.Refreshed?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Raised on the UI thread after the rows were rebuilt; the shell's Neuron chips and status strip follow it.</summary>
	public event EventHandler? Refreshed;

	[RelayCommand]
	private Task RefreshNow() => this.fleet.RefreshAsync();

	/// <summary>Set by the shell: opens a Neuron's page.</summary>
	public Action<FleetRowViewModel>? OpenNeuron { get; set; }

	/// <summary>Opens the page of a Neuron: the one given, or the one selected.</summary>
	[RelayCommand]
	private void Open(FleetRowViewModel? row)
	{
		row ??= this.SelectedRow;
		if (row is not null)
			this.OpenNeuron?.Invoke(row);
	}

	[RelayCommand]
	private void OpenEvents(FleetRowViewModel? row)
	{
		row ??= this.SelectedRow;
		if (row is not null)
			this.ShowEventsFor?.Invoke(row.BareJid);
	}

	public void Dispose()
	{
		this.timer.Stop();
		this.fleet.Monitors.NeuronsChanged -= this.MarkDirty;
		this.fleet.HealthChanged -= this.Fleet_HealthChanged;
		this.fleet.Monitors.Hub.IssueChanged -= this.Hub_IssueChanged;
	}
}
