using System.Text.Json;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.Engine.Alerts;

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

	/// <summary>Someone asks to become one of the account's contacts (a presence subscription).</summary>
	ContactRequest,

	/// <summary>A chat message from a person, or one in a room that may interrupt.</summary>
	Message,

	/// <summary>An invitation to a room.</summary>
	RoomInvitation,

	/// <summary>A device asks its owner whether someone may read, control or befriend it.</summary>
	DeviceQuestion,

	/// <summary>Someone asks to see an identity, or for a signature.</summary>
	IdentityRequest,
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

	/// <summary>True when someone has to do something about it.</summary>
	public bool NeedsYou { get; init; }

	/// <summary>Alerts with the same group replace each other on screen: one pop-up per conversation.</summary>
	public string? Group { get; init; }

	/// <summary>For a message: it came from a room.</summary>
	public bool IsRoom { get; init; }

	/// <summary>True for the kinds that are answered rather than read: requests, invitations and questions.</summary>
	public bool IsRequest => this.Kind is AlertKind.ContactRequest or AlertKind.RoomInvitation or AlertKind.DeviceQuestion or AlertKind.IdentityRequest;
}

/// <summary>Which alerts to show.</summary>
public sealed record AlertSettings
{
	/// <summary>Keep NAT running in the tray when its window closes.</summary>
	public bool KeepRunningInTray { get; init; } = true;

	public bool Notifications { get; init; } = true;
	public bool Errors { get; init; } = true;
	public bool NeuronDown { get; init; } = true;

	/// <summary>Also alert for notifications that are only information (updates, restarts).</summary>
	public bool InformationalNotifications { get; init; }

	/// <summary>Contact requests, room invitations, questions from devices and requests for identities.</summary>
	public bool ContactRequests { get; init; } = true;

	/// <summary>Chat messages from people, and from rooms as each room is set.</summary>
	public bool Messages { get; init; } = true;

	/// <summary>Play the message sound for chat messages.</summary>
	public bool SoundForMessages { get; init; } = true;

	/// <summary>Hold back every pop-up and sound until it is turned off. Counts keep running.</summary>
	public bool DoNotDisturb { get; init; }

	/// <summary>Show contacts that you are away. Pop-ups and sounds still come.</summary>
	public bool Away { get; init; }

	/// <summary>Play a sound for errors and Neurons going down.</summary>
	public bool SoundForErrors { get; init; } = true;

	/// <summary>Play a sound for notifications that need someone.</summary>
	public bool SoundForNotifications { get; init; } = true;

	/// <summary>Play a sound for contact requests.</summary>
	public bool SoundForContactRequests { get; init; } = true;

	/// <summary>Play a sound for information too (Neurons back, informational notifications).</summary>
	public bool SoundForInformation { get; init; }

	/// <summary>Whether an alert should make a sound with these settings.</summary>
	public bool WantsSound(Alert a) => a.Kind switch
	{
		AlertKind.Error or AlertKind.NeuronDown => this.SoundForErrors,
		AlertKind.ContactRequest or AlertKind.RoomInvitation or AlertKind.DeviceQuestion or AlertKind.IdentityRequest => this.SoundForContactRequests,
		AlertKind.Message => this.SoundForMessages,
		AlertKind.NeuronBack => this.SoundForInformation,
		_ when a.Severity >= Events.EventSeverity.Warning || a.NeedsYou => this.SoundForNotifications,
		_ => this.SoundForInformation,
	};
}

/// <summary>Keeps alert settings between runs.</summary>
public interface IAlertSettingsStore
{
	AlertSettings Load();

	void Save(AlertSettings settings);
}

/// <summary>Alert settings in the database.</summary>
public sealed class DatabaseAlertSettingsStore(Storage.DatabaseSettings settings) : IAlertSettingsStore
{
	/// <summary>The settings group's name in the database.</summary>
	public const string Name = "alerts";

	public AlertSettings Load() => settings.Get<AlertSettings>(Name) ?? new();

	public void Save(AlertSettings value) => settings.Set(Name, value);
}

/// <summary>Alert settings for this run only, where nothing can be kept.</summary>
public sealed class MemoryAlertSettingsStore : IAlertSettingsStore
{
	private AlertSettings settings = new();

	public AlertSettings Load() => this.settings;

	public void Save(AlertSettings value) => this.settings = value;
}

/// <summary>Alert settings in a JSON file: how they were kept before the database. Tests use it.</summary>
public sealed class JsonAlertSettingsStore(string path) : IAlertSettingsStore
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
/// Decides what is worth an alert while NAT watches, in the window or from the tray:
/// notifications that need someone, errors the fleet has not had before or that came back,
/// and Neurons that stop or start answering. Things already there when NAT started (history,
/// saved notifications) never alert. A Neuron has to be gone for a while before it counts as
/// down, so reconnects do not alert. Alerts can be snoozed.
/// </summary>
public sealed class AlertService : IDisposable
{
	private readonly MonitorService monitors;
	private readonly Func<DateTime> clock;
	private readonly DateTime started;
	private readonly HashSet<string> seenNotifications = [];
	private readonly HashSet<string> neededYou = [];
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
		this.AccountConnected = id => monitors.For(id)?.Session.Status == Accounts.ConnectionStatus.Connected;

		foreach (NeuronNotification n in monitors.Attention.Items)
		{
			this.seenNotifications.Add(n.Id);
			if (n.NeedsAction)
				this.neededYou.Add(n.Id);
		}
		foreach (Issue i in monitors.Hub.Issues())
			this.issues[i.Fingerprint] = (i.State, i.Count);
		foreach (WatchedNeuron n in monitors.Neurons)
			this.neurons[(n.AccountId, n.BareJid.ToLowerInvariant())] = (n.State, this.started, false);

		monitors.Attention.Changed += this.Attention_Changed;
		monitors.Hub.IssueChanged += this.Hub_IssueChanged;
		monitors.NeuronsChanged += this.Monitors_NeuronsChanged;
		monitors.SubscriptionRequested += this.Monitors_SubscriptionRequested;

		if (startTimer)
			this.timer = new Timer(_ => this.CheckNeurons(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
	}

	/// <summary>How long a Neuron must be gone before it alerts.</summary>
	public static TimeSpan DownAfter { get; } = TimeSpan.FromSeconds(90);

	public AlertSettings Settings { get; set; }

	/// <summary>Alerts are held back until then.</summary>
	public DateTime? SnoozedUntil { get; private set; }

	public bool IsSnoozed => this.SnoozedUntil is DateTime t && t > this.clock();

	/// <summary>True while pop-ups and sounds are held back: do not disturb, or snoozed.</summary>
	public bool IsQuiet => this.Settings.DoNotDisturb || this.IsSnoozed;

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

		if (!this.IsQuiet)
			this.Raised?.Invoke(this, a);
	}

	/// <summary>Raises a made-up alert, to try the pop-up and the sound. Ignores do not disturb.</summary>
	public void Test(AlertKind kind)
	{
		DateTime now = this.clock();
		this.Raised?.Invoke(this, kind switch
		{
			AlertKind.Message => new Alert
			{
				Id = "test:" + now.Ticks,
				Kind = AlertKind.Message,
				Title = "Test message",
				Detail = "This is how a new chat message looks and sounds.",
				At = now,
			},
			_ => new Alert
			{
				Id = "test:" + now.Ticks,
				Kind = kind,
				Title = "Test alert",
				Detail = "This is how an alert from a Neuron looks and sounds.",
				Severity = kind is AlertKind.Error or AlertKind.NeuronDown ? EventSeverity.Error : EventSeverity.Warning,
				At = now,
			},
		});
	}

	// ---- Chat messages, invitations, questions and requests ----------------------------------

	private readonly HashSet<string> seenRequests = [];

	/// <summary>Alerts for new chat messages and room invitations.</summary>
	public void Watch(Chat.ChatService chat)
	{
		chat.MessageReceived += (_, m) => this.MessageReceived(m);
		chat.RoomsChanged += (_, _) =>
		{
			foreach (Chat.RoomInvitation i in chat.Invitations)
			{
				if (this.FirstTime("inv:" + i.AccountId + ":" + i.RoomJid + ":" + i.Received.Ticks) && this.Settings.ContactRequests)
				{
					this.Raise(new Alert
					{
						Id = "inv:" + i.AccountId + ":" + i.RoomJid + ":" + i.Received.Ticks,
						Kind = AlertKind.RoomInvitation,
						Title = $"{i.From} invites you to {i.RoomJid.Split('@')[0]}",
						Detail = i.Reason ?? i.RoomJid,
						Severity = EventSeverity.Warning,
						At = this.clock(),
						AccountId = i.AccountId,
						Target = i.RoomJid,
						NeedsYou = true,
					});
				}
			}
		};
	}

	/// <summary>A new message arrived. Also used by tests.</summary>
	internal void MessageReceived(Chat.IncomingMessage m)
	{
		Chat.Conversation c = m.Conversation;
		if (!this.Settings.Messages || c.IsMuted(this.clock()))
			return;

		// A room only interrupts when it is set to, or when the message names you.
		if (c.IsRoom && c.Notify != Chat.RoomNotify.All && !m.Message.Mention)
			return;

		string room = c.BareJid.Split('@')[0];
		string text = m.Message.Text.Length > 0 ? m.Message.Text : m.Message.Markdown;
		this.Raise(new Alert
		{
			Id = "m:" + m.Message.Id,
			Group = "m:" + c.AccountId.ToString("N") + "|" + c.BareJid.ToLowerInvariant(),
			Kind = AlertKind.Message,
			Title = c.IsRoom ? $"{m.SenderLabel} in {room}" : m.SenderLabel,
			Detail = text.ReplaceLineEndings(" "),
			At = this.clock(),
			AccountId = c.AccountId,
			Target = c.BareJid,
			IsRoom = c.IsRoom,
		});
	}

	/// <summary>Alerts for new questions from devices.</summary>
	public void Watch(Devices.DevicesService devices)
	{
		devices.QuestionsChanged += (_, _) =>
		{
			foreach (Devices.DeviceQuestion q in devices.Questions)
			{
				if (!this.FirstTime("q:" + q.Id) || !this.Settings.ContactRequests)
					continue;

				this.Raise(new Alert
				{
					Id = "q:" + q.Id,
					Kind = AlertKind.DeviceQuestion,
					Title = q.Kind switch
					{
						Devices.DeviceQuestionKind.Friendship => $"{q.RemoteJid} wants to be a contact of a device",
						Devices.DeviceQuestionKind.Readout => $"{q.RemoteJid} wants to read a device",
						_ => $"{q.RemoteJid} wants to control a device",
					},
					Detail = q.DeviceJid,
					Severity = EventSeverity.Warning,
					At = this.clock(),
					AccountId = q.AccountId,
					Target = q.Id,
					NeedsYou = true,
				});
			}
		};
	}

	/// <summary>Alerts for new requests to see an identity or to sign.</summary>
	public void Watch(Identity.IdentityService identities)
	{
		identities.PetitionsChanged += (_, _) =>
		{
			foreach (Identity.IdentityPetition p in identities.Petitions)
			{
				if (!this.FirstTime("p:" + p.PetitionId) || !this.Settings.ContactRequests)
					continue;

				this.Raise(new Alert
				{
					Id = "p:" + p.PetitionId,
					Kind = AlertKind.IdentityRequest,
					Title = (p.RequestorName ?? p.From) + (p.Kind == "identity" ? " asks to see your identity" : " asks for your signature"),
					Detail = string.IsNullOrWhiteSpace(p.Purpose) ? p.From : p.Purpose,
					Severity = EventSeverity.Warning,
					At = this.clock(),
					AccountId = p.AccountId,
					Target = p.PetitionId,
					NeedsYou = true,
				});
			}
		};
	}

	private bool FirstTime(string key)
	{
		lock (this.sync)
			return this.seenRequests.Add(key);
	}

	// ---- Notifications ---------------------------------------------------------------------

	private void Attention_Changed(object? sender, NeuronNotification n)
	{
		bool isNew, nowNeedsYou;
		lock (this.sync)
		{
			isNew = this.seenNotifications.Add(n.Id);

			// An item that did not need anyone and now does, such as an identity application its
			// automatic review left for a person, alerts once, as a new item would.
			nowNeedsYou = n.NeedsAction && this.neededYou.Add(n.Id) && n.State != AttentionState.Handled;
		}

		if (!isNew)
		{
			// Already known: a correction, or a state change. Only coming to need someone counts.
			if (nowNeedsYou && this.Settings.Notifications)
				this.RaiseNotification(n);
			return;
		}

		if (!this.Settings.Notifications || n.Received < this.started.AddMinutes(-1))
			return;

		// An identity application is quiet until its review says it needs someone.
		if (n.Kind == NotificationKind.LegalIdApplication)
		{
			if (n.IsOpen)
				this.RaiseNotification(n);
			return;
		}

		bool worth = n.IsOpen || n.Severity >= EventSeverity.Warning || this.Settings.InformationalNotifications;
		if (!worth)
			return;

		this.Raise(new Alert
		{
			Id = "n:" + n.Id,
			Kind = AlertKind.Notification,
			Title = n.Title,
			Detail = this.NeuronLabel(n.AccountId, n.NeuronJid) + (n.IsOpen ? " is waiting for you" : string.Empty),
			NeedsYou = n.IsOpen,
			Severity = n.Severity,
			At = this.clock(),
			AccountId = n.AccountId,
			NeuronJid = n.NeuronJid,
			Target = n.Id,
		});
	}

	/// <summary>The alert for a known item that has just come to need someone.</summary>
	private void RaiseNotification(NeuronNotification n)
	{
		string neuron = this.NeuronLabel(n.AccountId, n.NeuronJid);
		this.Raise(new Alert
		{
			Id = "n:" + n.Id,
			Kind = AlertKind.Notification,
			Title = n.Title,
			Detail = string.IsNullOrWhiteSpace(n.Subject) ? neuron : n.Subject + " on " + neuron,
			NeedsYou = true,
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
			Detail = where.Count == 1 ? NeuronNames.For(this.monitors.Neurons, where[0]) : $"{where.Count} Neurons",
			Severity = i.Severity,
			At = this.clock(),
			NeuronJid = where.Count == 1 ? where[0] : null,
			Target = i.Fingerprint,
		});
	}

	// ---- Contact requests ------------------------------------------------------------------

	private void Monitors_SubscriptionRequested(object? sender, (Guid AccountId, Waher.Networking.XMPP.Events.PresenceEventArgs Presence) e)
		=> this.ContactRequested(e.AccountId, e.Presence.FromBareJID);

	/// <summary>Someone asks to become a contact of an account. Also used by tests.</summary>
	internal void ContactRequested(Guid accountId, string from)
	{
		if (!this.Settings.ContactRequests)
			return;

		this.Raise(new Alert
		{
			Id = "c:" + accountId + ":" + from + ":" + this.clock().Ticks,
			Kind = AlertKind.ContactRequest,
			Title = $"{from} wants to be a contact",
			Detail = this.AccountLabel?.Invoke(accountId) is string a ? "Asks " + a : null,
			Severity = Events.EventSeverity.Warning,
			At = this.clock(),
			AccountId = accountId,
			Target = from,
			NeedsYou = true,
		});
	}

	/// <summary>Names an account in alerts. Set by the host.</summary>
	public Func<Guid, string?>? AccountLabel { get; set; }

	// ---- Neurons ---------------------------------------------------------------------------

	private void Monitors_NeuronsChanged(object? sender, EventArgs e) => this.CheckNeurons();

	/// <summary>Looks for Neurons that have been down long enough, or came back. Also runs on a timer.</summary>
	/// <summary>Whether an account is connected. Only then can a Neuron be known to be down. Tests replace it.</summary>
	internal Func<Guid, bool> AccountConnected { get; init; }

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

				// While the account itself is not connected, NAT cannot know whether the Neuron is down: no alert.
				bool accountConnected = this.AccountConnected(n.AccountId);
				bool isUp = n.State is WatchState.Live;
				bool isDown = accountConnected && n.State is WatchState.Offline or WatchState.Failed;
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
		Title = kind == AlertKind.NeuronDown ? $"{this.NeuronLabel(n.AccountId, n.BareJid)} is not answering" : $"{this.NeuronLabel(n.AccountId, n.BareJid)} answers again",
		Detail = kind == AlertKind.NeuronDown ? n.Error ?? "Its event stream stopped." : null,
		Severity = kind == AlertKind.NeuronDown ? EventSeverity.Error : EventSeverity.Info,
		At = now,
		AccountId = n.AccountId,
		NeuronJid = n.BareJid,
		Target = n.BareJid,
	};

	/// <summary>The Neuron's name on screen: its roster name or its domain, or its full address when two would look the same.</summary>
	private string NeuronLabel(Guid accountId, string jid)
		=> NeuronNames.For(this.monitors.Neurons.OrderByDescending(n => n.AccountId == accountId), jid);

	public void Dispose()
	{
		this.timer?.Dispose();
		this.monitors.Attention.Changed -= this.Attention_Changed;
		this.monitors.Hub.IssueChanged -= this.Hub_IssueChanged;
		this.monitors.NeuronsChanged -= this.Monitors_NeuronsChanged;
		this.monitors.SubscriptionRequested -= this.Monitors_SubscriptionRequested;
	}
}
