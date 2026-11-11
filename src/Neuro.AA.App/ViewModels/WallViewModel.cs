using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.ViewModels;

/// <summary>One Neuron on the wall: how it is doing, in a few large numbers.</summary>
public sealed partial class WallTileViewModel(Guid accountId, string bareJid) : ObservableObject
{
	public Guid AccountId { get; } = accountId;
	public string BareJid { get; } = bareJid;

	[ObservableProperty] private string label = string.Empty;

	/// <summary>ok, busy, bad or idle: the colour of the tile's edge and its dot.</summary>
	[ObservableProperty] private string tone = "idle";

	[ObservableProperty] private string stateText = string.Empty;
	[ObservableProperty] private string cpu = "–";
	[ObservableProperty] private string memory = "–";
	[ObservableProperty] private string disk = "–";
	[ObservableProperty] private bool highCpu;
	[ObservableProperty] private bool lowMemory;
	[ObservableProperty] private bool lowDisk;
	[ObservableProperty] private int errors;
	[ObservableProperty] private bool hasErrors;
	[ObservableProperty] private int issues;
	[ObservableProperty] private bool hasIssues;
	[ObservableProperty] private string lastEvent = "–";

	internal void Apply(FleetRowViewModel row)
	{
		this.Label = row.Label;
		this.Tone = row.State switch
		{
			WatchState.Live => row.HasErrors || row.HighCpu || row.LowDisk || row.LowMemory ? "busy" : "ok",
			WatchState.Waiting or WatchState.Discovering or WatchState.Subscribing => "busy",
			WatchState.Forbidden or WatchState.Offline or WatchState.Failed => "bad",
			_ => "idle",
		};
		this.StateText = row.State switch
		{
			WatchState.Live => row.HasErrors ? "Errors in the last hour" : row.HighCpu ? "Busy" : row.LowDisk ? "Disk nearly full" : row.LowMemory ? "Low on memory" : "All well",
			WatchState.Waiting => "Waiting for it to come online",
			WatchState.Discovering or WatchState.Subscribing => "Connecting",
			WatchState.Forbidden => "No access",
			WatchState.Offline => "Offline",
			WatchState.Failed => "Could not listen",
			_ => string.Empty,
		};
		this.Cpu = row.CpuText;
		this.Memory = row.UsedMemory is double mb ? (mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB") : "–";
		this.Disk = row.FreeDisk is double gb ? (gb >= 100 ? $"{gb:0} GB" : $"{gb:0.#} GB") : "–";
		this.LastEvent = row.LastEvent is null ? "none yet" : row.LastEventText;
		this.HighCpu = row.HighCpu;
		this.LowMemory = row.LowMemory;
		this.LowDisk = row.LowDisk;
		this.Errors = row.ErrorsLastHour;
		this.HasErrors = row.ErrorsLastHour > 0;
		this.Issues = row.OpenIssues;
		this.HasIssues = row.OpenIssues > 0;
	}
}

/// <summary>One warning or error on the wall.</summary>
public sealed partial class WallEventViewModel(NeuronEvent e, string neuron) : ObservableObject
{
	public NeuronEvent Event { get; } = e;
	public string Neuron { get; } = neuron;
	public string Title { get; } = e.Title;

	/// <summary>bad for an error, busy for a warning.</summary>
	public string Tone { get; } = e.Severity >= EventSeverity.Error ? "bad" : "busy";

	public string Kind { get; } = e.Severity switch
	{
		EventSeverity.Critical => "Critical",
		EventSeverity.Error => "Error",
		_ => "Warning",
	};

	[ObservableProperty] private string time = string.Empty;
}

/// <summary>
/// The Wall view: the Neurons and their latest problems, in large text with no controls, for
/// a big screen on the wall. It only reads; everything it shows is kept by the Neurons list
/// and the event stream.
/// </summary>
public sealed partial class WallViewModel : ObservableObject
{
	private const int MaxLatest = 8;
	private readonly FleetViewModel fleet;
	private readonly EventHub hub;
	private readonly ConcurrentQueue<NeuronEvent> arrived = new();
	private readonly DispatcherTimer timer;
	private bool running;

	public WallViewModel(FleetViewModel fleet, EventHub hub)
	{
		this.fleet = fleet;
		this.hub = hub;
		this.timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => this.Tick());
	}

	/// <summary>The time, for tests. Null for the real one.</summary>
	public Func<DateTime>? Now { get; set; }

	public ObservableCollection<WallTileViewModel> Tiles { get; } = [];

	/// <summary>The latest warnings and errors, newest first.</summary>
	public ObservableCollection<WallEventViewModel> Latest { get; } = [];

	[ObservableProperty] private bool hasTiles;

	/// <summary>How many tiles go side by side: few Neurons get large tiles.</summary>
	[ObservableProperty] private int columns = 3;
	[ObservableProperty] private bool hasLatest;
	[ObservableProperty] private string clock = string.Empty;
	[ObservableProperty] private string date = string.Empty;

	/// <summary>"4 of 5 Neurons reporting"</summary>
	[ObservableProperty] private string summary = "No Neurons yet";

	/// <summary>ok when every Neuron reports, busy when some do, bad when none does.</summary>
	[ObservableProperty] private string summaryTone = "idle";

	/// <summary>"12 errors in the last hour", or "No errors in the last hour".</summary>
	[ObservableProperty] private string errorsText = string.Empty;

	[ObservableProperty] private bool hasErrors;

	/// <summary>Starts following the Neurons and the events. Called as the wall opens.</summary>
	public void Start()
	{
		if (this.running)
			return;

		this.running = true;
		this.Latest.Clear();
		foreach (NeuronEvent e in this.hub.Snapshot().Where(e => e.Severity >= EventSeverity.Warning).OrderByDescending(e => e.Timestamp).Take(MaxLatest).Reverse())
			this.arrived.Enqueue(e);

		this.hub.EventAdded += this.Hub_EventAdded;
		this.fleet.Refreshed += this.Fleet_Refreshed;
		this.timer.Start();
		this.Refresh();
	}

	/// <summary>Stops following. Called as the wall closes.</summary>
	public void Stop()
	{
		if (!this.running)
			return;

		this.running = false;
		this.hub.EventAdded -= this.Hub_EventAdded;
		this.fleet.Refreshed -= this.Fleet_Refreshed;
		this.timer.Stop();
	}

	private void Hub_EventAdded(object? sender, NeuronEvent e)
	{
		if (e.Severity >= EventSeverity.Warning)
			this.arrived.Enqueue(e);
	}

	private void Fleet_Refreshed(object? sender, EventArgs e) => this.Refresh();

	private void Tick() => this.Refresh();

	/// <summary>Brings the wall up to date. The timer does it every second while the wall is open.</summary>
	public void Refresh()
	{
		DateTime now = this.Now?.Invoke() ?? DateTime.Now;
		this.Clock = now.ToString("HH:mm", CultureInfo.CurrentCulture);
		this.Date = now.ToString("dddd d MMMM", CultureInfo.CurrentCulture);

		// The tiles keep their order by name, whatever the list is sorted by: a wall should not shuffle.
		List<FleetRowViewModel> rows = [.. this.fleet.Rows.OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.AccountLabel, StringComparer.CurrentCultureIgnoreCase)];
		for (int i = this.Tiles.Count - 1; i >= 0; i--)
		{
			if (!rows.Any(r => r.AccountId == this.Tiles[i].AccountId && r.BareJid == this.Tiles[i].BareJid))
				this.Tiles.RemoveAt(i);
		}

		for (int i = 0; i < rows.Count; i++)
		{
			FleetRowViewModel row = rows[i];
			WallTileViewModel? tile = this.Tiles.FirstOrDefault(t => t.AccountId == row.AccountId && t.BareJid == row.BareJid);
			if (tile is null)
			{
				tile = new WallTileViewModel(row.AccountId, row.BareJid);
				this.Tiles.Insert(Math.Min(i, this.Tiles.Count), tile);
			}
			else if (this.Tiles.IndexOf(tile) is int at && at != i && i < this.Tiles.Count)
				this.Tiles.Move(at, i);

			tile.Apply(row);
		}

		this.HasTiles = this.Tiles.Count > 0;
		this.Columns = this.Tiles.Count switch
		{
			<= 1 => 1,
			<= 4 => 2,
			_ => 3,
		};

		int total = rows.Count;
		int live = rows.Count(r => r.State == WatchState.Live);
		this.Summary = total switch
		{
			0 => "No Neurons yet",
			1 => live == 1 ? "1 Neuron reporting" : "1 Neuron, not reporting",
			_ => live == total ? $"All {total} Neurons reporting" : $"{live} of {total} Neurons reporting",
		};
		this.SummaryTone = total == 0 ? "idle" : live == total ? "ok" : live == 0 ? "bad" : "busy";

		int errors = rows.GroupBy(r => r.BareJid, StringComparer.OrdinalIgnoreCase).Sum(g => g.Max(r => r.ErrorsLastHour));
		this.HasErrors = errors > 0;
		this.ErrorsText = errors switch
		{
			0 => "No errors in the last hour",
			1 => "1 error in the last hour",
			_ => $"{errors:N0} errors in the last hour",
		};

		while (this.arrived.TryDequeue(out NeuronEvent? e))
		{
			string neuron = rows.FirstOrDefault(r => string.Equals(r.BareJid, e.Source.NeuronJid, StringComparison.OrdinalIgnoreCase))?.Label ?? e.Source.NeuronJid;
			this.Latest.Insert(0, new WallEventViewModel(e, neuron));
		}

		while (this.Latest.Count > MaxLatest)
			this.Latest.RemoveAt(this.Latest.Count - 1);

		foreach (WallEventViewModel shown in this.Latest)
			shown.Time = RelativeTime.Format(shown.Event.Timestamp, now);

		this.HasLatest = this.Latest.Count > 0;
	}
}
