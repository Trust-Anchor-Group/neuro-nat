using System.Text.Json;
using Neuro.AA.Engine.Attention;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.Engine.Alerts;

/// <summary>What an alert is about.</summary>
public enum AlertKind
{
	/// <summary>A Neuron notification that needs someone, or a warning or worse.</summary>
	Notification,

	/// <summary>An error the fleet has not had before, or one that came back after being resolved.</summary>
	Error,

	/// <summary>A Neuron stopped answering.</summary>
	NeuronDown,

	/// <summary>A Neuron that was down answers again.</summary>
	NeuronBack,
}

/// <summary>Something worth interrupting someone for.</summary>
public sealed record Alert
{
	public required string Id { get; init; }
	public required AlertKind Kind { get; init; }
	public required string Title { get; init; }
	public string? Detail { get; init; }
	public EventSeverity Severity { get; init; }
	public DateTime At { get; init; }
	public Guid? AccountId { get; init; }
	public string? NeuronJid { get; init; }

	/// <summary>The notification id, issue fingerprint, or Neuron address the alert points at.</summary>
	public string? Target { get; init; }
}

/// <summary>Which alerts to show.</summary>
public sealed record AlertSettings
{
	/// <summary>Keep Neuro.AA running in the tray when its window closes.</summary>
	public bool KeepRunningInTray { get; init; } = true;

	public bool Notifications { get; init; } = true;
	public bool Errors { get; init; } = true;
	public bool NeuronDown { get; init; } = true;

	/// <summary>Also alert for notifications that are only information (updates, restarts).</summary>
	public bool InformationalNotifications { get; init; }
}

/// <summary>Keeps alert settings between runs.</summary>
public sealed class JsonAlertSettingsStore(string path)
{
	private static readonly JsonSerializerOptions options = new() { WriteIndented = true };

	public AlertSettings Load()
	{
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<AlertSettings>(File.ReadAllText(path)) ?? new() : new();
		}
		catch (Exception)
		{
			return new();
		}
	}

	public void Save(AlertSettings settings)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(settings, options));
		File.Move(tmp, path, true);
	}
}

/// <summary>
/// Decides what is worth an alert while Neuro.AA watches, in the window or from the tray:
/// notifications that need someone, errors the fleet has not had before or that came back,
/// and Neurons that stop or start answering. Things already there when Neuro.AA started (history,
/// saved notifications) never alert. A Neuron has to be gone for a while before it counts as
/// down, so reconnects do not alert. Alerts can be snoozed.
/// </summary>
public sealed class AlertService : IDisposable
{
	private readonly MonitorService monitors;
	private readonly Func<DateTime> clock;
	private readonly DateTime started;
	private readonly HashSet<string> seenNotifications = [];
	private readonly Dictionary<string, (IssueState State, int Count)> issues = [];
	private readonly Dictionary<(Guid, string), (WatchState State, DateTime Since, bool Alerted)> neurons = [];
	private readonly List<Alert> recent = [];
	private readonly object sync = new();
	private readonly Timer? timer;

	public AlertService(MonitorService monitors, AlertSettings settings, Func<DateTime>? clock = null, bool startTimer = true)
	{
		this.monitors = monitors;
		this.Settings = settings;
		this.clock = clock ?? (() => DateTime.Now);
		this.started = this.clock();

		foreach (NeuronNotification n in monitors.Attention.Items)
			this.seenNotifications.Add(n.Id);
		foreach (Issue i in monitors.Hub.Issues())
			this.issues[i.Fingerprint] = (i.State, i.Count);
		foreach (WatchedNeuron n in monitors.Neurons)
			this.neurons[(n.AccountId, n.BareJid.ToLowerInvariant())] = (n.State, this.started, false);

		monitors.Attention.Changed += this.Attention_Changed;
		monitors.Hub.IssueChanged += this.Hub_IssueChanged;
		monitors.NeuronsChanged += this.Monitors_NeuronsChanged;

		if (startTimer)
			this.timer = new Timer(_ => this.CheckNeurons(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
	}

	/// <summary>How long a Neuron must be gone before it alerts.</summary>
	public static TimeSpan DownAfter { get; } = TimeSpan.FromSeconds(90);

	public AlertSettings Settings { get; set; }

	/// <summary>Alerts are held back until then.</summary>
	public DateTime? SnoozedUntil { get; private set; }

	public bool IsSnoozed => this.SnoozedUntil is DateTime t && t > this.clock();

	/// <summary>Raised for every alert, unless snoozed. Any thread.</summary>
	public event EventHandler<Alert>? Raised;

	/// <summary>Raised when snoozing starts or ends. Any thread.</summary>
	public event EventHandler? SnoozeChanged;

	/// <summary>Alerts of the last hour, newest first, including those held back while snoozed.</summary>
	public IReadOnlyList<Alert> Recent
	{
		get
		{
			lock (this.sync)
				return [.. this.recent];
		}
	}

	public void Snooze(TimeSpan span)
	{
		this.SnoozedUntil = this.clock() + span;
		this.SnoozeChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Resume()
	{
		this.SnoozedUntil = null;
		this.SnoozeChanged?.Invoke(this, EventArgs.Empty);
	}

	private void Raise(Alert a)
	{
		lock (this.sync)
		{
			this.recent.Insert(0, a);
			DateTime cutoff = this.clock().AddHours(-1);
			this.recent.RemoveAll(x => x.At < cutoff);
		}

		if (!this.IsSnoozed)
			this.Raised?.Invoke(this, a);
	}

	// ---- Notifications ---------------------------------------------------------------------

	private void Attention_Changed(object? sender, NeuronNotification n)
	{
		lock (this.sync)
		{
			if (!this.seenNotifications.Add(n.Id))
				return;   // Already known: a correction, or a state change.
		}

		if (!this.Settings.Notifications || n.Received < this.started.AddMinutes(-1))
			return;

		bool worth = n.IsOpen || n.Severity >= EventSeverity.Warning || this.Settings.InformationalNotifications;
		if (!worth)
			return;

		this.Raise(new Alert
		{
			Id = "n:" + n.Id,
			Kind = AlertKind.Notification,
			Title = n.Title,
			Detail = this.NeuronLabel(n.AccountId, n.NeuronJid) + (n.IsOpen ? " · needs you" : string.Empty),
			Severity = n.Severity,
			At = this.clock(),
			AccountId = n.AccountId,
			NeuronJid = n.NeuronJid,
			Target = n.Id,
		});
	}

	// ---- Errors ----------------------------------------------------------------------------

	private void Hub_IssueChanged(object? sender, Issue i)
	{
		bool isNew, cameBack;
		lock (this.sync)
		{
			bool known = this.issues.TryGetValue(i.Fingerprint, out var before);
			this.issues[i.Fingerprint] = (i.State, i.Count);
			isNew = !known;
			cameBack = known && before.State == IssueState.Resolved && i.State == IssueState.Open && i.Count > before.Count;
		}

		if (!this.Settings.Errors || i.Severity < EventSeverity.Error || i.State == IssueState.Muted)
			return;

		// History that arrives after start (backfill) is not news: only events from now on count.
		if (i.Latest.Timestamp < this.started.AddMinutes(-1))
			return;

		if (!isNew && !cameBack)
			return;

		IReadOnlyList<string> where = i.Neurons;
		this.Raise(new Alert
		{
			Id = "i:" + i.Fingerprint + ":" + i.Count,
			Kind = AlertKind.Error,
			Title = (cameBack ? "Back again: " : "New: ") + i.Title,
			Detail = where.Count == 1 ? where[0] : $"{where.Count} Neurons",
			Severity = i.Severity,
			At = this.clock(),
			NeuronJid = where.Count == 1 ? where[0] : null,
			Target = i.Fingerprint,
		});
	}

	// ---- Neurons ---------------------------------------------------------------------------

	private void Monitors_NeuronsChanged(object? sender, EventArgs e) => this.CheckNeurons();

	/// <summary>Looks for Neurons that have been down long enough, or came back. Also runs on a timer.</summary>
	public void CheckNeurons()
	{
		DateTime now = this.clock();
		List<Alert> raise = [];

		lock (this.sync)
		{
			foreach (WatchedNeuron n in this.monitors.Neurons)
			{
				(Guid, string) key = (n.AccountId, n.BareJid.ToLowerInvariant());
				if (!this.neurons.TryGetValue(key, out var was))
				{
					this.neurons[key] = (n.State, now, false);
					continue;
				}

				bool isUp = n.State is WatchState.Live;
				bool isDown = n.State is WatchState.Offline or WatchState.Failed;
				bool wasDown = was.State is WatchState.Offline or WatchState.Failed;

				if (was.State != n.State)
				{
					// A Neuron that alerted as down and is up again says so.
					if (isUp && was.Alerted && this.Settings.NeuronDown)
						raise.Add(this.NeuronAlert(n, AlertKind.NeuronBack, now));

					// The clock counts from when it went down; moving between down states keeps it.
					this.neurons[key] = (n.State, isDown && wasDown ? was.Since : now, isDown && was.Alerted);
					continue;
				}

				if (isDown && !was.Alerted && now - was.Since >= DownAfter)
				{
					this.neurons[key] = was with { Alerted = true };
					if (this.Settings.NeuronDown)
						raise.Add(this.NeuronAlert(n, AlertKind.NeuronDown, now));
				}
			}
		}

		foreach (Alert a in raise)
			this.Raise(a);
	}

	private Alert NeuronAlert(WatchedNeuron n, AlertKind kind, DateTime now) => new()
	{
		Id = (kind == AlertKind.NeuronDown ? "down:" : "up:") + n.AccountId + ":" + n.BareJid + ":" + now.Ticks,
		Kind = kind,
		Title = kind == AlertKind.NeuronDown ? $"{n.Label} is not answering" : $"{n.Label} answers again",
		Detail = kind == AlertKind.NeuronDown ? n.Error ?? "Its event stream stopped." : null,
		Severity = kind == AlertKind.NeuronDown ? EventSeverity.Error : EventSeverity.Info,
		At = now,
		AccountId = n.AccountId,
		NeuronJid = n.BareJid,
		Target = n.BareJid,
	};

	private string NeuronLabel(Guid accountId, string jid)
		=> this.monitors.Neurons.FirstOrDefault(n => n.AccountId == accountId && string.Equals(n.BareJid, jid, StringComparison.OrdinalIgnoreCase))?.Label ?? jid;

	public void Dispose()
	{
		this.timer?.Dispose();
		this.monitors.Attention.Changed -= this.Attention_Changed;
		this.monitors.Hub.IssueChanged -= this.Hub_IssueChanged;
		this.monitors.NeuronsChanged -= this.Monitors_NeuronsChanged;
	}
}
