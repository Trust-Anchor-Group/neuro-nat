using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.ViewModels;

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

	[ObservableProperty] private string label = string.Empty;
	[ObservableProperty] private string accountLabel = string.Empty;
	[ObservableProperty] private int accountColorIndex;
	[ObservableProperty] private WatchState state;
	[ObservableProperty] private string stateText = string.Empty;
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

	private static string Mb(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";

	internal void Apply(WatchedNeuron n, AccountProfile? account)
	{
		this.Label = n.Label;
		this.AccountLabel = account?.Label ?? string.Empty;
		this.AccountColorIndex = account?.ColorIndex ?? 0;
		this.State = n.State;
		this.StateText = n.StatusText;
		this.Note = n.Error;
	}

	internal void Apply(NeuronHealth? h)
	{
		if (h is null)
			return;

		this.Software = h.Software ?? "–";

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
			(double a, double f) => $"{Mb(a)} used · {Mb(f)} free",
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
		this.Note = h.Error ?? (noSamples ? "No performance samples on this Neuron. They are recorded when it runs as a service." : null);
	}

	internal void Apply(EventHub.NeuronSummary? s, DateTime now)
	{
		this.ErrorsLastHour = s?.ErrorsLastHour ?? 0;
		this.WarningsLastHour = s?.WarningsLastHour ?? 0;
		this.OpenIssues = s?.OpenIssues ?? 0;
		this.HasErrors = this.ErrorsLastHour > 0;
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

	private bool Matches(FleetRowViewModel r)
	{
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

	private static readonly string[] columns = ["Neuron", "Account", "Stream", "Version", "CPU", "Memory", "Disk", "Errors 1 h", "Open issues", "Last event"];

	private static string?[] Cells(FleetRowViewModel r)
		=> [r.BareJid, r.AccountLabel, r.StateText, r.Software, r.CpuText, r.MemoryText, r.DiskText, r.ErrorsLastHour.ToString(), r.OpenIssues.ToString(), r.LastEventText];

	/// <summary>One Neuron's state in a few lines, for pasting into chat or a ticket.</summary>
	public static string Summarize(FleetRowViewModel r)
	{
		System.Text.StringBuilder sb = new();
		sb.Append(r.BareJid).Append(": ").AppendLine(r.StateText);
		sb.Append("Version: ").AppendLine(r.Software);
		sb.Append("CPU: ").Append(r.CpuText).Append(", memory: ").Append(r.MemoryText).Append(", disk: ").AppendLine(r.DiskText);
		sb.Append("Errors in the last hour: ").Append(r.ErrorsLastHour).Append(", open issues: ").Append(r.OpenIssues).Append(", last event: ").Append(r.LastEventText);
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
			list.Open("Show its events", () => this.ShowEventsFor?.Invoke(row.BareJid), icon: "Events");
			if (this.Shell is IShell shell)
				list.AddRange(shell.NeuronActions(row.BareJid, row.AccountId, WorkspaceKind.Fleet).Where(a => a.Text != "Events"));
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
		list.Do("Read their health now", () => this.RefreshNowCommand.Execute(null), icon: "Refresh");
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

		List<FleetRowViewModel> keep = [];
		foreach (WatchedNeuron n in neurons.OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase))
		{
			FleetRowViewModel row = this.Rows.FirstOrDefault(r => r.AccountId == n.AccountId && string.Equals(r.BareJid, n.BareJid, StringComparison.OrdinalIgnoreCase))
				?? new FleetRowViewModel(n.AccountId, n.BareJid);

			row.Apply(n, profiles.TryGetValue(n.AccountId, out AccountProfile? p) ? p : null);
			row.Apply(this.fleet.Get(n.AccountId, n.BareJid));
			row.Apply(summaries.TryGetValue(n.BareJid, out EventHub.NeuronSummary? s) ? s : null, now);
			keep.Add(row);
		}

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
			: $"{keep.Count} Neuron{(keep.Count == 1 ? "" : "s")} · {live} live" + (failing > 0 ? $" · {failing} with errors in the last hour" : string.Empty);
		this.Refreshed?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Raised on the UI thread after the rows were rebuilt; the shell's Neuron chips and status strip follow it.</summary>
	public event EventHandler? Refreshed;

	[RelayCommand]
	private Task RefreshNow() => this.fleet.RefreshAsync();

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
