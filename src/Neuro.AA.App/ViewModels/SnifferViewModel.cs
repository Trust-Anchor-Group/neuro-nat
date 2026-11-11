using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Sniffing;

namespace Neuro.AA.App.ViewModels;

/// <summary>What a traffic line is, for the four filter buttons.</summary>
public enum TrafficKind
{
	Sent,
	Received,
	Information,
	Problem,
}

/// <summary>One line of traffic as the view shows it: one row, or tidy XML when opened.</summary>
public sealed partial class TrafficLineViewModel(SnifferLine line) : ObservableObject
{
	private string? tidy;
	private bool tidied;

	public SnifferLine Line { get; } = line;
	public DateTime Timestamp => this.Line.Timestamp;
	public SnifferDirection Direction => this.Line.Direction;
	public string Text => this.Line.Text;

	public TrafficKind Kind => this.Line.Direction switch
	{
		SnifferDirection.Transmitted => TrafficKind.Sent,
		SnifferDirection.Received => TrafficKind.Received,
		SnifferDirection.Information => TrafficKind.Information,
		_ => TrafficKind.Problem,
	};

	/// <summary>The line as XML with one element per row, or null when it is not XML or already is one short element.</summary>
	public string? Tidy
	{
		get
		{
			if (!this.tidied)
			{
				this.tidied = true;
				this.tidy = XmlTidy.Tidy(this.Line.Text);
			}

			return this.tidy;
		}
	}

	/// <summary>True when the line has more to show than fits on one row.</summary>
	public bool CanOpen => this.Line.Text.Length > 60 && (XmlTidy.LooksLikeXml(this.Line.Text) || this.Line.Text.Contains('\n'));

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Shown))]
	private bool isOpen;

	/// <summary>Marks a line the find box matches.</summary>
	[ObservableProperty] private bool isMatch;

	/// <summary>Marks the match the find box is on.</summary>
	[ObservableProperty] private bool isCurrentMatch;

	/// <summary>What the row shows: the whole line on one row, or tidied over several when opened.</summary>
	public string Shown => this.IsOpen ? this.Tidy ?? this.Line.Text : Export.OneLine(this.Line.Text);

	public string Tag => this.Line.Direction switch
	{
		SnifferDirection.Transmitted => "SENT",
		SnifferDirection.Received => "GOT",
		SnifferDirection.Information => "INFO",
		SnifferDirection.Warning => "WARN",
		SnifferDirection.Error => "ERR",
		SnifferDirection.Exception => "EXC",
		_ => string.Empty,
	};

	public string TagTip => this.Line.Direction switch
	{
		SnifferDirection.Transmitted => "Sent by this app",
		SnifferDirection.Received => "Received from the server",
		SnifferDirection.Information => "A note from the connection, not traffic",
		SnifferDirection.Warning => "A warning from the connection",
		SnifferDirection.Error => "An error from the connection",
		SnifferDirection.Exception => "An exception in the connection",
		_ => string.Empty,
	};
}

/// <summary>
/// Live traffic of one connection or one node: everything sent and received, as lines that
/// can be selected, searched, filtered, copied and saved. The same view model is behind the
/// traffic on Accounts and on Sources. Lines arrive on network threads and reach the UI in
/// batches a few times per second, so a busy connection cannot flood it.
/// </summary>
public sealed partial class SnifferViewModel : ObservableObject, IDisposable, IActionSource
{
	/// <summary>How many lines can be kept. The first is the default.</summary>
	public static IReadOnlyList<int> Limits { get; } = [2000, 10000, 50000];

	private readonly StreamSniffer sniffer;
	private readonly ConcurrentQueue<SnifferLine> pending = new();
	private readonly List<TrafficLineViewModel> all = [];
	private readonly List<TrafficLineViewModel> matches = [];
	private readonly DispatcherTimer flush;
	private long lastSequence;

	public SnifferViewModel(StreamSniffer sniffer, string title = "Traffic")
	{
		this.sniffer = sniffer;
		this.Title = title;

		foreach (SnifferLine l in sniffer.Snapshot())
			this.pending.Enqueue(l);

		this.IsEmpty = this.pending.IsEmpty;

		sniffer.LineAdded += this.Sniffer_LineAdded;
		this.flush = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => this.Flush());
		this.flush.Start();
	}

	/// <summary>Whose traffic this is: an account or a node. Used for the window title and file names.</summary>
	public string Title { get; set; }

	/// <summary>Set by the owner: "Go to" entries for the account or node the traffic belongs to.</summary>
	public Func<IReadOnlyList<ItemAction>>? OwnerPlaces { get; set; }

	/// <summary>The lines shown, oldest first: everything kept, less what the filter buttons hide.</summary>
	public Avalonia.Collections.AvaloniaList<TrafficLineViewModel> Lines { get; } = [];

	/// <summary>Set by the view: the lines selected.</summary>
	public IReadOnlyList<TrafficLineViewModel> Selected { get; set; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PauseText))]
	private bool isPaused;

	public string PauseText => this.IsPaused ? "Resume" : "Pause";

	/// <summary>True when nothing has been captured (or the view was cleared).</summary>
	[ObservableProperty]
	private bool isEmpty = true;

	/// <summary>True when lines were captured but the filter buttons hide all of them.</summary>
	[ObservableProperty]
	private bool isAllHidden;

	/// <summary>What the empty view says. The owner sets it to fit: connect the account, start the node.</summary>
	[ObservableProperty]
	private string emptyText = "Everything sent and received shows here, live.";

	// ----- What to show -----

	[ObservableProperty] private bool showSent = true;
	[ObservableProperty] private bool showReceived = true;
	[ObservableProperty] private bool showInformation = true;
	[ObservableProperty] private bool showProblems = true;

	partial void OnShowSentChanged(bool value) => this.Rebuild();
	partial void OnShowReceivedChanged(bool value) => this.Rebuild();
	partial void OnShowInformationChanged(bool value) => this.Rebuild();
	partial void OnShowProblemsChanged(bool value) => this.Rebuild();

	public bool IsFiltered => !(this.ShowSent && this.ShowReceived && this.ShowInformation && this.ShowProblems);

	private bool Shows(TrafficLineViewModel l) => l.Kind switch
	{
		TrafficKind.Sent => this.ShowSent,
		TrafficKind.Received => this.ShowReceived,
		TrafficKind.Information => this.ShowInformation,
		_ => this.ShowProblems,
	};

	/// <summary>Shows only one kind of line, or everything when null.</summary>
	public void ShowOnly(TrafficKind? kind)
	{
		this.ShowSent = kind is null or TrafficKind.Sent;
		this.ShowReceived = kind is null or TrafficKind.Received;
		this.ShowInformation = kind is null or TrafficKind.Information;
		this.ShowProblems = kind is null or TrafficKind.Problem;
	}

	[RelayCommand]
	private void ShowEverything() => this.ShowOnly(null);

	private void Rebuild()
	{
		this.Lines.Clear();
		this.Lines.AddRange(this.all.Where(this.Shows));
		this.OnPropertyChanged(nameof(this.IsFiltered));
		this.RefreshState();
		this.RefreshMatches();
	}

	private void RefreshState()
	{
		this.IsEmpty = this.all.Count == 0;
		this.IsAllHidden = this.all.Count > 0 && this.Lines.Count == 0;
		this.OnPropertyChanged(nameof(this.LimitNote));
	}

	// ----- How much to keep -----

	/// <summary>How many lines are kept. Older lines are dropped, and the view says so.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(LimitNote))]
	private int limit = Limits[0];

	/// <summary>Lines dropped because the limit was reached, since the view was cleared.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(LimitNote), nameof(HasDropped))]
	private long dropped;

	public bool HasDropped => this.Dropped > 0;

	public string LimitNote => this.Dropped > 0
		? $"Keeping the last {this.Limit:N0} lines. {this.Dropped:N0} older {(this.Dropped == 1 ? "line was" : "lines were")} dropped."
		: $"{this.all.Count:N0} of up to {this.Limit:N0} lines";

	[RelayCommand]
	private void SetLimit(int value)
	{
		this.Limit = value;
		this.Trim();
	}

	private void Trim()
	{
		int over = this.all.Count - this.Limit;
		if (over <= 0)
			return;

		HashSet<TrafficLineViewModel> gone = [.. this.all.Take(over)];
		this.all.RemoveRange(0, over);
		this.Dropped += over;

		int shown = 0;
		while (shown < this.Lines.Count && gone.Contains(this.Lines[shown]))
			shown++;

		if (shown > 0)
			this.Lines.RemoveRange(0, shown);

		if (this.matches.Count > 0)
		{
			TrafficLineViewModel? current = this.matchIndex >= 0 && this.matchIndex < this.matches.Count ? this.matches[this.matchIndex] : null;
			this.matches.RemoveAll(gone.Contains);
			this.matchIndex = current is null ? -1 : this.matches.IndexOf(current);
		}
	}

	// ----- Live: new lines, pause, and following the newest -----

	/// <summary>Set by the view: true while the newest line is in sight, so new lines keep it there.</summary>
	public bool IsFollowing { get; set; } = true;

	/// <summary>Lines that arrived while the view was scrolled up.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNew), nameof(NewText))]
	private int newCount;

	public bool HasNew => this.NewCount > 0;
	public string NewText => this.NewCount == 1 ? "Jump to latest · 1 new line" : $"Jump to latest · {this.NewCount:N0} new lines";

	/// <summary>Raised when the view should scroll to its newest line.</summary>
	public event EventHandler? ScrollToLatest;

	/// <summary>Raised when the view should bring a line into sight.</summary>
	public event EventHandler<TrafficLineViewModel>? ScrollTo;

	[RelayCommand]
	private void JumpToLatest()
	{
		this.NewCount = 0;
		this.IsFollowing = true;
		this.ScrollToLatest?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Called by the view when the newest line comes into sight again.</summary>
	public void SeenLatest() => this.NewCount = 0;

	private void Sniffer_LineAdded(object? sender, SnifferLine e) => this.pending.Enqueue(e);

	/// <summary>Moves pending lines into <see cref="Lines"/> now instead of on the next tick.</summary>
	public void Flush()
	{
		if (this.IsPaused || this.pending.IsEmpty)
			return;

		List<TrafficLineViewModel> batch = [];
		while (this.pending.TryDequeue(out SnifferLine? line))
		{
			if (line.Sequence <= this.lastSequence)
				continue;  // Already shown (snapshot and live event can overlap).

			this.lastSequence = line.Sequence;
			batch.Add(new TrafficLineViewModel(line) { IsOpen = this.OpenAll });
		}

		if (batch.Count == 0)
			return;

		if (batch.Count > this.Limit)
		{
			this.Dropped += batch.Count - this.Limit;
			batch.RemoveRange(0, batch.Count - this.Limit);
		}

		this.all.AddRange(batch);
		List<TrafficLineViewModel> shown = [.. batch.Where(this.Shows)];
		this.Lines.AddRange(shown);
		this.Trim();

		if (this.HasFind)
		{
			foreach (TrafficLineViewModel l in shown)
			{
				if (this.IsFound(l))
				{
					l.IsMatch = true;
					this.matches.Add(l);
				}
			}

			this.OnPropertyChanged(nameof(this.FindStatus));
		}

		if (!this.IsFollowing)
			this.NewCount += shown.Count;

		this.RefreshState();
	}

	[RelayCommand]
	private void Clear()
	{
		this.all.Clear();
		this.Lines.Clear();
		this.matches.Clear();
		this.Dropped = 0;
		this.NewCount = 0;
		this.RefreshState();
		this.OnPropertyChanged(nameof(this.FindStatus));
	}

	[RelayCommand]
	private void TogglePause() => this.IsPaused = !this.IsPaused;

	// ----- Opening lines: tidy XML -----

	/// <summary>Shows every line opened, as tidy XML, instead of one row each.</summary>
	[ObservableProperty] private bool openAll;

	partial void OnOpenAllChanged(bool value)
	{
		foreach (TrafficLineViewModel l in this.all)
			l.IsOpen = value;
	}

	[RelayCommand]
	private void Toggle(TrafficLineViewModel? line)
	{
		if (line is not null)
			line.IsOpen = !line.IsOpen;
	}

	// ----- Find -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasFind), nameof(FindStatus))]
	private string findText = string.Empty;

	public bool HasFind => this.FindText.Trim().Length > 0;

	private int matchIndex = -1;

	public string FindStatus => !this.HasFind ? string.Empty
		: this.matches.Count == 0 ? "No match"
		: this.matchIndex >= 0 ? $"{this.matchIndex + 1} of {this.matches.Count:N0}"
		: Ui.Count(this.matches.Count, "match", "matches");

	partial void OnFindTextChanged(string value) => this.RefreshMatches();

	private bool IsFound(TrafficLineViewModel l) => l.Text.Contains(this.FindText.Trim(), StringComparison.OrdinalIgnoreCase);

	private void RefreshMatches()
	{
		foreach (TrafficLineViewModel m in this.matches)
		{
			m.IsMatch = false;
			m.IsCurrentMatch = false;
		}

		this.matches.Clear();
		this.matchIndex = -1;

		if (this.HasFind)
		{
			foreach (TrafficLineViewModel l in this.Lines)
			{
				if (this.IsFound(l))
				{
					l.IsMatch = true;
					this.matches.Add(l);
				}
			}
		}

		this.OnPropertyChanged(nameof(this.FindStatus));
	}

	/// <summary>Goes to the next (or previous) line the find box matches, wrapping around.</summary>
	[RelayCommand]
	private void FindNext(string? direction)
	{
		this.matches.RemoveAll(m => !this.Lines.Contains(m));
		if (this.matches.Count == 0)
		{
			this.matchIndex = -1;
			this.OnPropertyChanged(nameof(this.FindStatus));
			return;
		}

		if (this.matchIndex >= 0 && this.matchIndex < this.matches.Count)
			this.matches[this.matchIndex].IsCurrentMatch = false;

		int step = direction == "back" ? -1 : 1;
		this.matchIndex = this.matchIndex < 0
			? (step > 0 ? 0 : this.matches.Count - 1)
			: (this.matchIndex + step + this.matches.Count) % this.matches.Count;

		TrafficLineViewModel line = this.matches[this.matchIndex];
		line.IsCurrentMatch = true;
		this.IsFollowing = false;
		this.OnPropertyChanged(nameof(this.FindStatus));
		this.ScrollTo?.Invoke(this, line);
	}

	// ----- Copy and save -----

	/// <summary>Lines as text: one per row, with the time and direction in front when asked for.</summary>
	public static string ToText(IEnumerable<TrafficLineViewModel> lines, bool withTimes, bool tidy = false)
	{
		StringBuilder sb = new();
		foreach (TrafficLineViewModel l in lines)
		{
			string text = tidy ? l.Tidy ?? l.Text : l.Text;
			if (withTimes)
				sb.Append(l.Timestamp.ToString("HH:mm:ss.fff")).Append('\t').Append(l.Tag).Append('\t');

			sb.AppendLine(text);
		}

		return sb.ToString();
	}

	/// <summary>
	/// Lines in the sniffer file format the IoT Gateway tools write and read: one element per
	/// line, named by direction, with the text in rows.
	/// </summary>
	public static string ToXml(IEnumerable<TrafficLineViewModel> lines)
	{
		StringBuilder sb = new();
		XmlWriterSettings settings = new() { Indent = true, IndentChars = "\t", OmitXmlDeclaration = false, Encoding = Encoding.UTF8 };
		using (XmlWriter w = XmlWriter.Create(sb, settings))
		{
			w.WriteStartElement("SnifferOutput", "http://waher.se/Schema/SnifferOutput.xsd");
			foreach (TrafficLineViewModel l in lines)
			{
				w.WriteStartElement(l.Direction switch
				{
					SnifferDirection.Transmitted => "Tx",
					SnifferDirection.Received => "Rx",
					SnifferDirection.Information => "Info",
					SnifferDirection.Warning => "Warning",
					SnifferDirection.Error => "Error",
					_ => "Exception",
				});
				w.WriteAttributeString("timestamp", XmlConvert.ToString(l.Timestamp, XmlDateTimeSerializationMode.RoundtripKind));
				foreach (string row in l.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
					w.WriteElementString("Row", row);
				w.WriteEndElement();
			}

			w.WriteEndElement();
		}

		return sb.ToString().Replace("utf-16", "utf-8", StringComparison.Ordinal);
	}

	private SaveFormat[] Formats(IReadOnlyList<TrafficLineViewModel> lines) =>
	[
		new SaveFormat("Text, with times", "txt", () => ToText(lines, true)),
		new SaveFormat("Sniffer file (XML)", "xml", () => ToXml(lines)),
	];

	private IReadOnlyList<TrafficLineViewModel> SelectedOrShown => this.Selected.Count > 0 ? this.Selected : [.. this.Lines];

	/// <summary>Toolbar Copy: the selected lines, or every line shown when none is selected.</summary>
	[RelayCommand]
	private Task Copy()
	{
		IReadOnlyList<TrafficLineViewModel> lines = this.SelectedOrShown;
		return Ui.Copy(ToText(lines, lines.Count > 1), Ui.Count(lines.Count, "line"));
	}

	/// <summary>Toolbar Save: the selected lines, or every line shown when one or none is selected.</summary>
	[RelayCommand]
	private Task Save()
	{
		IReadOnlyList<TrafficLineViewModel> lines = this.Selected.Count > 1 ? this.Selected : [.. this.Lines];
		return Ui.Save(Ui.FileName("Traffic " + this.Title), this.Formats(lines));
	}

	/// <summary>Opens this traffic in a window of its own, next to the main one.</summary>
	[RelayCommand]
	private void OpenWindow() => Ui.OpenWindow?.Invoke(this);

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		List<TrafficLineViewModel> lines = [.. items.OfType<TrafficLineViewModel>().OrderBy(l => l.Line.Sequence)];
		if (lines.Count == 0)
			return [];

		ActionList list = [];
		string what = Ui.Count(lines.Count, "line");
		bool anyXml = lines.Any(l => l.Tidy is not null);

		if (lines.Count == 1)
		{
			TrafficLineViewModel line = lines[0];
			list.Copy("Copy line", () => line.Text, "the line", "Ctrl+C", inBar: true);
			list.Copy("Copy with time", () => ToText(lines, true).TrimEnd(), "the line with its time", "Ctrl+Shift+C");
			if (anyXml)
				list.Copy("Copy as tidy XML", () => line.Tidy, "the line as tidy XML");
		}
		else
		{
			list.Copy("Copy " + what, () => ToText(lines, false), what, "Ctrl+C", inBar: true);
			list.Copy("Copy with times", () => ToText(lines, true), what + " with times", "Ctrl+Shift+C");
			if (anyXml)
				list.Copy("Copy as tidy XML", () => ToText(lines, false, true), what + " as tidy XML");
		}

		list.Copy("Copy everything shown", () => ToText(this.Lines, true), Ui.Count(this.Lines.Count, "line"));
		list.Save(lines.Count > 1 ? "Save " + what + "…" : "Save everything shown…", Ui.FileName("Traffic " + this.Title),
			this.Formats(lines.Count > 1 ? lines : [.. this.Lines]));

		if (lines.Count == 1)
		{
			TrafficKind kind = lines[0].Kind;
			string name = kind switch
			{
				TrafficKind.Sent => "sent",
				TrafficKind.Received => "received",
				TrafficKind.Information => "notes",
				_ => "problems",
			};
			list.Filter("Show only " + name, () => this.ShowOnly(kind));
		}

		if (this.IsFiltered)
			list.Filter("Show everything", () => this.ShowOnly(null));

		if (this.OwnerPlaces is not null)
			list.AddRange(this.OwnerPlaces());

		if (lines.Any(l => l.CanOpen))
		{
			bool open = lines.Where(l => l.CanOpen).All(l => l.IsOpen);
			list.Do(open ? "Close up" : "Open as tidy XML", () =>
			{
				foreach (TrafficLineViewModel l in lines)
					l.IsOpen = !open;
			}, key: "Enter");
		}

		list.Do(this.IsPaused ? "Resume" : "Pause", () => this.IsPaused = !this.IsPaused, icon: "Pause");
		list.Do("Clear", () => this.ClearCommand.Execute(null), icon: "Eraser");

		if (Ui.OpenWindow is not null)
			list.Window("Open in its own window", () => Ui.OpenWindow?.Invoke(this));

		return list;
	}

	public void Dispose()
	{
		this.sniffer.LineAdded -= this.Sniffer_LineAdded;
		this.flush.Stop();
	}
}
