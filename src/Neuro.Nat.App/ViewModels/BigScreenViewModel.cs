using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.ViewModels;

/// <summary>
/// A page a feature adds to the big screen, after Neurons and Problems: App numbers will be one.
/// Add it to <see cref="BigScreenPages.Added"/> as the app starts. The big screen asks
/// <see cref="HasContent"/> every second and skips the page while it is false.
/// </summary>
public interface IBigScreenPage
{
	/// <summary>Short and never changing, such as "app-numbers": Settings keeps it when the page is turned off.</summary>
	string Key { get; }

	/// <summary>The page's name, in Settings and at the top of the page, such as "App numbers".</summary>
	string Title { get; }

	/// <summary>False while there is nothing to show: the page is skipped.</summary>
	bool HasContent { get; }

	/// <summary>What the page shows, under its title: a control, or a view model the app has a template for. Drawn for 1280 by 560.</summary>
	object? Content { get; }

	/// <summary>The big screen opened: start following what the page shows.</summary>
	void Start()
	{
	}

	/// <summary>The big screen closed.</summary>
	void Stop()
	{
	}
}

/// <summary>The big screen's pages, by key, and the place where features add theirs.</summary>
public static class BigScreenPages
{
	public const string Neurons = "neurons";
	public const string Problems = "problems";

	/// <summary>Pages added by features, shown after Neurons and Problems in this order. Empty by default.</summary>
	public static IList<IBigScreenPage> Added { get; } = [];
}

/// <summary>One page of the big screen, and whether it is shown now. Neurons and Problems take as many pages as they need, one part each.</summary>
public sealed partial class BigScreenPage(string key, string title, IBigScreenPage? added = null, int part = 0) : ObservableObject
{
	public string Key { get; } = key;
	public string Title { get; } = title;

	/// <summary>Which of the pages with this key: 0 for the first.</summary>
	public int Part { get; } = part;

	/// <summary>The feature's page, for pages added by a feature.</summary>
	public IBigScreenPage? Added { get; } = added;

	/// <summary>On in Settings and with something to show.</summary>
	[ObservableProperty] private bool isShown;

	[ObservableProperty] private bool isCurrent;
}

/// <summary>
/// How the Neurons page lays out its tiles, in px as the big screen is drawn (1280 wide, gaps
/// included). A tile never gets smaller than <see cref="MinScale"/> of its drawn size: when the
/// Neurons do not fit at that, they take more pages.
/// </summary>
public readonly record struct BigScreenTileLayout(int Columns, int Rows, int PerPage, int Pages, double Scale, bool Small)
{
	/// <summary>A tile with every number: its narrowest, and its height.</summary>
	public const double TileWidth = 264;
	public const double TileHeight = 238;

	/// <summary>A small tile, for many Neurons: the name, the state, CPU and errors.</summary>
	public const double SmallWidth = 186;
	public const double SmallHeight = 150;

	/// <summary>The smallest a tile is drawn, against its own size: still read from across a room.</summary>
	public const double MinScale = 0.85;

	public const int MaxColumns = 6;

	/// <summary>The space for the tiles on the Neurons page of a 1280 by 720 big screen.</summary>
	public static readonly Size DrawnArea = new(802, 552);

	/// <summary>
	/// Few Neurons get every number and full size, when all of them fit so. More get small tiles,
	/// side by side as far as the width allows, on as few pages as keep them at least <see cref="MinScale"/>.
	/// </summary>
	public static BigScreenTileLayout For(int count, Size area)
	{
		if (count <= 0 || area.Width <= 0 || area.Height <= 0)
			return new(1, 1, Math.Max(count, 0), 1, 1, false);

		(int columns, int rows, double scale) = Best(count, area, TileWidth, TileHeight);
		if (scale >= 1)
			return new(columns, rows, count, 1, 1, false);

		for (int pages = 1; ; pages++)
		{
			int per = (count + pages - 1) / pages;
			(columns, rows, scale) = Best(per, area, SmallWidth, SmallHeight);
			if (scale >= MinScale || per == 1)
				return new(columns, rows, per, (count + per - 1) / per, scale, true);
		}
	}

	/// <summary>The columns that let <paramref name="count"/> tiles be largest. Of equally large, the first as wide as tall.</summary>
	private static (int Columns, int Rows, double Scale) Best(int count, Size area, double width, double height)
	{
		(int Columns, int Rows, double Scale) best = (1, count, -1);
		for (int columns = 1; columns <= Math.Min(MaxColumns, count); columns++)
		{
			int rows = (count + columns - 1) / columns;
			double scale = Math.Min(1, Math.Min(area.Width / (columns * width), area.Height / (rows * height)));
			if (scale > best.Scale + 0.0001 || (scale > best.Scale - 0.0001 && best.Columns < best.Rows))
				best = (columns, rows, scale);
		}

		return best;
	}
}

/// <summary>One Neuron on the big screen: how it is doing, in a few large numbers.</summary>
public sealed partial class BigScreenTileViewModel(Guid accountId, string bareJid) : ObservableObject
{
	public Guid AccountId { get; } = accountId;
	public string BareJid { get; } = bareJid;

	[ObservableProperty] private string label = string.Empty;

	/// <summary>ok, busy, bad or idle: the colour of the tile's edge and its dot.</summary>
	[ObservableProperty] private string tone = "idle";

	[ObservableProperty] private string stateText = string.Empty;

	/// <summary>The state in a word or two, for a small tile.</summary>
	[ObservableProperty] private string shortState = string.Empty;

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
		this.ShortState = row.State switch
		{
			WatchState.Live => row.HasErrors ? "Errors" : row.HighCpu ? "Busy" : row.LowDisk ? "Disk nearly full" : row.LowMemory ? "Low on memory" : "All well",
			WatchState.Waiting => "Waiting",
			WatchState.Discovering or WatchState.Subscribing => "Connecting",
			_ => this.StateText,
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

/// <summary>One warning or error on the Neurons page.</summary>
public sealed partial class BigScreenEventViewModel(NeuronEvent e, string neuron) : ObservableObject
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

/// <summary>One open problem on the Problems page: what, where, since when and how many times.</summary>
public sealed partial class BigScreenProblemViewModel(string fingerprint) : ObservableObject
{
	public string Fingerprint { get; } = fingerprint;

	[ObservableProperty] private string title = string.Empty;

	/// <summary>"Gothenburg edge", "Lab and Production", or "Lab and 3 more".</summary>
	[ObservableProperty] private string where = string.Empty;

	/// <summary>"Since 10:42", or "Since 9 Nov" when it started on another day.</summary>
	[ObservableProperty] private string since = string.Empty;

	/// <summary>"latest 2 min ago", after <see cref="Since"/> as everywhere else in the app.</summary>
	[ObservableProperty] private string last = string.Empty;

	[ObservableProperty] private string count = string.Empty;

	/// <summary>"time" or "times", under the count.</summary>
	[ObservableProperty] private string countUnit = string.Empty;

	/// <summary>bad for errors and worse, busy for warnings.</summary>
	[ObservableProperty] private string tone = "busy";

	/// <summary>It opened while the big screen was showing.</summary>
	[ObservableProperty] private bool isNew;

	internal DateTime LastSeen { get; private set; }

	internal void Apply(Issue issue, IReadOnlyDictionary<string, string> names, DateTime now, bool isNew)
	{
		this.Title = issue.Title;
		IReadOnlyList<string> where = issue.Neurons;
		string Name(string jid) => names.TryGetValue(jid, out string? n) ? n : Converters.NeuronNameConverter.Name(jid);
		this.Where = where.Count switch
		{
			0 => string.Empty,
			1 => Name(where[0]),
			2 => $"{Name(where[0])} and {Name(where[1])}",
			_ => $"{Name(where[0])} and {where.Count - 1} more",
		};
		this.Since = "Since " + (issue.FirstSeen.Date == now.Date
			? issue.FirstSeen.ToString("HH:mm", CultureInfo.CurrentCulture)
			: issue.FirstSeen.ToString("d MMM", CultureInfo.CurrentCulture));
		this.Last = "latest " + RelativeTime.Format(issue.LastSeen, now);
		this.Count = issue.Count.ToString("N0", CultureInfo.CurrentCulture);
		this.CountUnit = issue.Count == 1 ? "time" : "times";
		this.Tone = issue.Severity >= EventSeverity.Error ? "bad" : "busy";
		this.IsNew = isNew;
		this.LastSeen = issue.LastSeen;
	}
}

/// <summary>
/// The big screen (F11): the Neurons, the open problems and the pages features add, one page at
/// a time in large text with no controls. It turns by itself, skips pages with nothing to show,
/// and turns to the page with the news when a Neuron goes down or a problem opens. Left and
/// Right turn, Space holds. It only reads; what it shows is kept by the Neurons list and the
/// event stream.
/// </summary>
public sealed partial class BigScreenViewModel : ObservableObject
{
	private const int MaxLatest = 8;

	/// <summary>The problems on one Problems page. More take more pages.</summary>
	public const int ProblemsPerPage = 5;

	private readonly FleetViewModel fleet;
	private readonly EventHub hub;
	private readonly SettingsViewModel? settings;
	private readonly IList<IBigScreenPage> added;
	private readonly ConcurrentQueue<NeuronEvent> arrived = new();
	private readonly DispatcherTimer timer;
	private readonly Dictionary<(Guid, string), WatchState> states = [];
	private readonly HashSet<string> open = [];
	private readonly HashSet<string> fresh = [];
	private bool running;
	private bool seen;
	private TimeSpan left;
	private Size area = BigScreenTileLayout.DrawnArea;
	private BigScreenTileLayout layout = BigScreenTileLayout.For(0, BigScreenTileLayout.DrawnArea);

	public BigScreenViewModel(FleetViewModel fleet, EventHub hub, SettingsViewModel? settings = null, IList<IBigScreenPage>? added = null)
	{
		this.fleet = fleet;
		this.hub = hub;
		this.settings = settings;
		this.added = added ?? BigScreenPages.Added;
		this.timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => this.Tick());
		this.Pages.Add(new BigScreenPage(BigScreenPages.Neurons, "Neurons") { IsShown = true, IsCurrent = true });
		this.Pages.Add(new BigScreenPage(BigScreenPages.Problems, "Problems"));
		this.current = this.Pages[0];
		this.left = this.Dwell;
	}

	/// <summary>The time, for tests. Null for the real one.</summary>
	public Func<DateTime>? Now { get; set; }

	/// <summary>Every page in the order they turn, shown or not.</summary>
	public ObservableCollection<BigScreenPage> Pages { get; } = [];

	/// <summary>The pages shown now: a dot each at the bottom.</summary>
	public ObservableCollection<BigScreenPage> Dots { get; } = [];

	/// <summary>The page on the screen.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNeuronsPage), nameof(IsProblemsPage), nameof(IsAddedPage), nameof(AddedContent))]
	private BigScreenPage current;

	public bool IsNeuronsPage => this.Current.Key == BigScreenPages.Neurons;
	public bool IsProblemsPage => this.Current.Key == BigScreenPages.Problems;
	public bool IsAddedPage => this.Current.Added is not null;
	public object? AddedContent => this.Current.Added?.Content;

	/// <summary>Space: the page stays until Space, Left or Right.</summary>
	[ObservableProperty] private bool held;

	[ObservableProperty] private bool hasDots;

	/// <summary>How long a page stays, from Settings.</summary>
	public TimeSpan Dwell => TimeSpan.FromSeconds(this.settings?.BigScreenSeconds ?? 20);

	/// <summary>How long a page with news stays: half as long again.</summary>
	public TimeSpan NewsDwell => this.Dwell * 1.5;

	/// <summary>How long until the page turns.</summary>
	public TimeSpan Left => this.left;

	/// <summary>Raised when the page turned: +1 forward, -1 back.</summary>
	public event EventHandler<int>? Turned;

	// ----- What the pages show -----

	/// <summary>Every Neuron, by name.</summary>
	public ObservableCollection<BigScreenTileViewModel> Tiles { get; } = [];

	/// <summary>The Neurons on the Neurons page showing now, or the last one shown.</summary>
	public ObservableCollection<BigScreenTileViewModel> PageTiles { get; } = [];

	/// <summary>The latest warnings and errors, newest first.</summary>
	public ObservableCollection<BigScreenEventViewModel> Latest { get; } = [];

	/// <summary>Every open problem, the ones that opened while the screen showed first, then the latest.</summary>
	public ObservableCollection<BigScreenProblemViewModel> Problems { get; } = [];

	/// <summary>The problems on the Problems page showing now, or the last one shown.</summary>
	public ObservableCollection<BigScreenProblemViewModel> PageProblems { get; } = [];

	[ObservableProperty] private bool hasTiles;

	/// <summary>How many tiles go side by side: few Neurons get large tiles.</summary>
	[ObservableProperty] private int columns = 1;

	/// <summary>Many Neurons: small tiles, with the name, the state, CPU and errors.</summary>
	[ObservableProperty] private bool smallTiles;

	/// <summary>How wide the tiles are drawn before they are made <see cref="TileScale"/> as large, to fill the width there is.</summary>
	[ObservableProperty] private double tileListWidth = BigScreenTileLayout.DrawnArea.Width;

	[ObservableProperty] private double tileScale = 1;

	/// <summary>How the tiles are laid out now.</summary>
	public BigScreenTileLayout Layout => this.layout;

	/// <summary>The space for the tiles, as the view last told it.</summary>
	public Size TileArea => this.area;
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

	/// <summary>How many problems are open.</summary>
	[ObservableProperty] private int openProblems;

	// ----- The top of the page: what it is about -----

	[ObservableProperty] private string headline = string.Empty;
	[ObservableProperty] private string headTone = "idle";
	[ObservableProperty] private bool hasHeadDot;
	[ObservableProperty] private string subline = string.Empty;
	[ObservableProperty] private bool isSublineBad;

	/// <summary>Starts following the Neurons and the events, on the Neurons page. Called as the big screen opens.</summary>
	public void Start()
	{
		if (this.running)
			return;

		this.running = true;
		this.seen = false;
		this.states.Clear();
		this.open.Clear();
		this.fresh.Clear();
		this.Held = false;
		this.Latest.Clear();
		foreach (NeuronEvent e in this.hub.Snapshot().Where(e => e.Severity >= EventSeverity.Warning).OrderByDescending(e => e.Timestamp).Take(MaxLatest).Reverse())
			this.arrived.Enqueue(e);

		foreach (IBigScreenPage page in this.added)
			page.Start();

		this.hub.EventAdded += this.Hub_EventAdded;
		this.fleet.Refreshed += this.Fleet_Refreshed;
		this.timer.Start();
		this.Show(this.Pages[0], 0);
		this.left = this.Dwell;
		this.Refresh();
	}

	/// <summary>Stops following. Called as the big screen closes.</summary>
	public void Stop()
	{
		if (!this.running)
			return;

		this.running = false;
		this.hub.EventAdded -= this.Hub_EventAdded;
		this.fleet.Refreshed -= this.Fleet_Refreshed;
		this.timer.Stop();
		foreach (IBigScreenPage page in this.added)
			page.Stop();
	}

	private void Hub_EventAdded(object? sender, NeuronEvent e)
	{
		if (e.Severity >= EventSeverity.Warning)
			this.arrived.Enqueue(e);
	}

	private void Fleet_Refreshed(object? sender, EventArgs e) => this.Refresh();

	private void Tick()
	{
		this.Refresh();
		this.Advance(TimeSpan.FromSeconds(1));
	}

	// ----- Turning -----

	/// <summary>Time goes by: the page turns when its time is up, unless it is held.</summary>
	public void Advance(TimeSpan elapsed)
	{
		if (this.Held)
			return;

		this.left -= elapsed;
		if (this.left <= TimeSpan.Zero)
			this.Turn(1);
	}

	/// <summary>To the next shown page (+1) or the one before (-1), round and round. With one page, nothing turns.</summary>
	public void Turn(int direction)
	{
		this.left = this.Dwell;
		int count = this.Pages.Count;
		int at = Math.Max(0, this.Pages.IndexOf(this.Current));
		for (int step = 1; step < count; step++)
		{
			BigScreenPage page = this.Pages[((at + direction * step) % count + count) % count];
			if (page.IsShown)
			{
				this.Show(page, direction);
				return;
			}
		}

		// Nothing else to show. If this page has nothing either, back to the Neurons.
		if (!this.Current.IsShown)
			this.Show(this.Pages[0], direction);
	}

	/// <summary>A key on the big screen. Left and Right turn, Space holds or lets go. True when the key did something.</summary>
	public bool Press(Key key)
	{
		switch (key)
		{
			case Key.Left:
				this.Turn(-1);
				return true;
			case Key.Right:
				this.Turn(1);
				return true;
			case Key.Space:
				this.Held = !this.Held;
				this.left = this.Dwell;
				return true;
			default:
				return false;
		}
	}

	/// <summary>Something happened that a page shows: that page now, and for longer.</summary>
	private void News(string key, int part = 0)
	{
		BigScreenPage? page = this.Pages.FirstOrDefault(p => p.Key == key && p.Part == part && p.IsShown);
		if (page is null)
			return;

		if (page != this.Current)
			this.Show(page, this.Pages.IndexOf(page) > this.Pages.IndexOf(this.Current) ? 1 : -1);

		this.left = this.NewsDwell;
	}

	private void Show(BigScreenPage page, int direction)
	{
		if (page == this.Current)
			return;

		// What was new on the Problems pages has been seen once it turns away from them.
		if (this.Current.Key == BigScreenPages.Problems && page.Key != BigScreenPages.Problems)
		{
			this.fresh.Clear();
			foreach (BigScreenProblemViewModel p in this.Problems)
				p.IsNew = false;
		}

		this.Current.IsCurrent = false;
		page.IsCurrent = true;
		this.Current = page;
		this.FillPage();
		this.Head();
		if (direction != 0)
			this.Turned?.Invoke(this, direction);
	}

	// ----- Keeping up -----

	/// <summary>Brings the pages up to date. The timer does it every second while the big screen is open.</summary>
	public void Refresh()
	{
		DateTime now = this.Now?.Invoke() ?? DateTime.Now;
		this.Clock = now.ToString("HH:mm", CultureInfo.CurrentCulture);
		this.Date = now.ToString("dddd d MMMM", CultureInfo.CurrentCulture);

		// The tiles keep their order by name, whatever the list is sorted by: a big screen should not shuffle.
		List<FleetRowViewModel> rows = [.. this.fleet.Rows.OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.AccountLabel, StringComparer.CurrentCultureIgnoreCase)];
		this.RefreshTiles(rows);
		this.RefreshLatest(rows, now);
		bool problemOpened = this.RefreshProblems(rows, now);

		// A Neuron that answered before and now does not: the first of them by name.
		int down = -1;
		for (int i = 0; i < rows.Count; i++)
		{
			FleetRowViewModel row = rows[i];
			(Guid, string) id = (row.AccountId, row.BareJid);
			if (down < 0 && this.states.TryGetValue(id, out WatchState was) && !IsDown(was) && IsDown(row.State))
				down = i;
			this.states[id] = row.State;
		}

		this.RefreshPages();

		if (this.seen)
		{
			if (down >= 0)
				this.News(BigScreenPages.Neurons, down / Math.Max(1, this.layout.PerPage));
			else if (problemOpened)
				this.News(BigScreenPages.Problems);
		}

		this.seen = true;

		// The page on the screen has nothing to show any more: on to the next.
		if (!this.Current.IsShown)
			this.Turn(1);

		this.Head();
	}

	private static bool IsDown(WatchState state) => state is WatchState.Offline or WatchState.Failed;

	private void RefreshTiles(List<FleetRowViewModel> rows)
	{
		for (int i = this.Tiles.Count - 1; i >= 0; i--)
		{
			if (!rows.Any(r => r.AccountId == this.Tiles[i].AccountId && r.BareJid == this.Tiles[i].BareJid))
				this.Tiles.RemoveAt(i);
		}

		for (int i = 0; i < rows.Count; i++)
		{
			FleetRowViewModel row = rows[i];
			BigScreenTileViewModel? tile = this.Tiles.FirstOrDefault(t => t.AccountId == row.AccountId && t.BareJid == row.BareJid);
			if (tile is null)
			{
				tile = new BigScreenTileViewModel(row.AccountId, row.BareJid);
				this.Tiles.Insert(Math.Min(i, this.Tiles.Count), tile);
			}
			else if (this.Tiles.IndexOf(tile) is int at && at != i && i < this.Tiles.Count)
				this.Tiles.Move(at, i);

			tile.Apply(row);
		}

		this.HasTiles = this.Tiles.Count > 0;
		this.Lay();

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
	}

	private void RefreshLatest(List<FleetRowViewModel> rows, DateTime now)
	{
		while (this.arrived.TryDequeue(out NeuronEvent? e))
		{
			string neuron = rows.FirstOrDefault(r => string.Equals(r.BareJid, e.Source.NeuronJid, StringComparison.OrdinalIgnoreCase))?.Label ?? Converters.NeuronNameConverter.Name(e.Source.NeuronJid);
			this.Latest.Insert(0, new BigScreenEventViewModel(e, neuron));
		}

		while (this.Latest.Count > MaxLatest)
			this.Latest.RemoveAt(this.Latest.Count - 1);

		foreach (BigScreenEventViewModel shown in this.Latest)
			shown.Time = RelativeTime.Format(shown.Event.Timestamp, now);

		this.HasLatest = this.Latest.Count > 0;
	}

	/// <summary>Brings the open problems up to date. True when one opened since the last time.</summary>
	private bool RefreshProblems(List<FleetRowViewModel> rows, DateTime now)
	{
		List<Issue> issues = [.. this.hub.Issues().Where(i => i.State is IssueState.Open or IssueState.Acknowledged)];
		bool opened = false;
		foreach (Issue i in issues)
		{
			if (!this.open.Contains(i.Fingerprint) && this.seen)
			{
				this.fresh.Add(i.Fingerprint);
				opened = true;
			}
		}

		this.open.Clear();
		this.open.UnionWith(issues.Select(i => i.Fingerprint));
		this.fresh.IntersectWith(this.open);

		Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
		foreach (FleetRowViewModel row in rows)
			names.TryAdd(row.BareJid, row.Label);

		List<Issue> shown = [.. issues
			.OrderByDescending(i => this.fresh.Contains(i.Fingerprint))
			.ThenByDescending(i => i.LastSeen)];

		for (int i = this.Problems.Count - 1; i >= 0; i--)
		{
			if (!shown.Any(s => s.Fingerprint == this.Problems[i].Fingerprint))
				this.Problems.RemoveAt(i);
		}

		for (int i = 0; i < shown.Count; i++)
		{
			Issue issue = shown[i];
			BigScreenProblemViewModel? item = this.Problems.FirstOrDefault(p => p.Fingerprint == issue.Fingerprint);
			if (item is null)
			{
				item = new BigScreenProblemViewModel(issue.Fingerprint);
				this.Problems.Insert(Math.Min(i, this.Problems.Count), item);
			}
			else if (this.Problems.IndexOf(item) is int at && at != i)
				this.Problems.Move(at, i);

			item.Apply(issue, names, now, this.fresh.Contains(issue.Fingerprint));
		}

		this.OpenProblems = issues.Count;
		return opened;
	}

	/// <summary>
	/// The space for the tiles on the Neurons page, in px as the big screen is drawn. The view
	/// tells it as it is laid out; until then it is that of a 1280 by 720 screen.
	/// </summary>
	public void Fit(Size tileArea)
	{
		if (tileArea.Width <= 0 || tileArea.Height <= 0 || tileArea == this.area)
			return;

		this.area = tileArea;
		this.Lay();
		this.RefreshPages();
		if (!this.Current.IsShown)
			this.Turn(1);
	}

	/// <summary>How the tiles are laid out, for the Neurons there are and the space there is.</summary>
	private void Lay()
	{
		this.layout = BigScreenTileLayout.For(this.Tiles.Count, this.area);
		this.Columns = this.layout.Columns;
		this.SmallTiles = this.layout.Small;
		this.TileScale = this.layout.Scale;
		this.TileListWidth = Math.Floor(this.area.Width / this.layout.Scale);
	}

	/// <summary>Which pages there are, which are on, and which have something to show.</summary>
	private void RefreshPages()
	{
		foreach (IBigScreenPage page in this.added)
		{
			if (!this.Pages.Any(p => p.Added == page))
				this.Pages.Add(new BigScreenPage(page.Key, page.Title, page));
		}

		for (int i = this.Pages.Count - 1; i >= 0; i--)
		{
			if (this.Pages[i].Added is IBigScreenPage gone && !this.added.Contains(gone))
				this.Pages.RemoveAt(i);
		}

		this.Parts(BigScreenPages.Neurons, this.layout.Pages);
		this.Parts(BigScreenPages.Problems, Math.Max(1, (this.OpenProblems + ProblemsPerPage - 1) / ProblemsPerPage));

		foreach (BigScreenPage page in this.Pages)
		{
			bool on = this.settings?.IsBigScreenPageOn(page.Key) ?? true;
			page.IsShown = page.Key switch
			{
				BigScreenPages.Neurons => true,
				BigScreenPages.Problems => on && this.OpenProblems > 0,
				_ => on && page.Added!.HasContent,
			};
		}

		List<BigScreenPage> dots = [.. this.Pages.Where(p => p.IsShown)];
		if (!dots.SequenceEqual(this.Dots))
		{
			this.Dots.Clear();
			foreach (BigScreenPage page in dots)
				this.Dots.Add(page);
		}

		this.HasDots = dots.Count > 1;
		this.FillPage();
	}

	/// <summary>As many pages with <paramref name="key"/> as it takes, side by side. The one on the screen, if it goes, gives way to the last one left.</summary>
	private void Parts(string key, int count)
	{
		List<BigScreenPage> have = [.. this.Pages.Where(p => p.Key == key)];
		int first = this.Pages.IndexOf(have[0]);
		while (have.Count < count)
		{
			BigScreenPage page = new(key, have[0].Title, part: have.Count);
			this.Pages.Insert(first + have.Count, page);
			have.Add(page);
		}

		while (have.Count > count)
		{
			BigScreenPage gone = have[^1];
			have.RemoveAt(have.Count - 1);
			if (gone == this.Current)
				this.Show(have[^1], 0);
			this.Pages.Remove(gone);
		}
	}

	/// <summary>The Neurons or the problems on the page showing now.</summary>
	private void FillPage()
	{
		if (this.Current.Key == BigScreenPages.Neurons)
		{
			int per = Math.Max(1, this.layout.PerPage);
			Fill(this.PageTiles, this.Tiles.Skip(this.Current.Part * per).Take(per));
		}
		else if (this.Current.Key == BigScreenPages.Problems)
			Fill(this.PageProblems, this.Problems.Skip(this.Current.Part * ProblemsPerPage).Take(ProblemsPerPage));
	}

	private static void Fill<T>(ObservableCollection<T> shown, IEnumerable<T> items)
	{
		List<T> wanted = [.. items];
		if (wanted.SequenceEqual(shown))
			return;

		shown.Clear();
		foreach (T item in wanted)
			shown.Add(item);
	}

	/// <summary>The top of the page: what it is about, in a few words.</summary>
	private void Head()
	{
		switch (this.Current.Key)
		{
			case BigScreenPages.Neurons:
				this.Headline = this.Summary;
				this.HeadTone = this.SummaryTone;
				this.HasHeadDot = true;
				this.Subline = this.ErrorsText;
				this.IsSublineBad = this.HasErrors;
				break;

			case BigScreenPages.Problems:
				this.Headline = this.OpenProblems == 1 ? "1 open problem" : $"{this.OpenProblems:N0} open problems";
				this.HeadTone = this.Problems.Any(p => p.Tone == "bad") ? "bad" : "busy";
				this.HasHeadDot = true;
				int neurons = this.hub.Issues().Where(i => i.State is IssueState.Open or IssueState.Acknowledged).SelectMany(i => i.Neurons).Distinct(StringComparer.OrdinalIgnoreCase).Count();
				this.Subline = neurons == 1 ? "On 1 Neuron" : $"On {neurons} Neurons";
				this.IsSublineBad = false;
				break;

			default:
				this.Headline = this.Current.Title;
				this.HeadTone = "idle";
				this.HasHeadDot = false;
				this.Subline = string.Empty;
				this.IsSublineBad = false;
				break;
		}
	}
}
