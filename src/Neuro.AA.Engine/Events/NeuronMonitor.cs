using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Attention;
using Neuro.AA.Engine.Sniffing;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Concentrator;
using Waher.Networking.XMPP.Events;
using Waher.Networking.XMPP.ServiceDiscovery;
using Waher.Networking.XMPP.StanzaErrors;

namespace Neuro.AA.Engine.Events;

/// <summary>
/// Watches every Neuron one account can reach and streams their events into the
/// <see cref="EventHub"/>.
///
/// How it works, using only what a Neuron already exposes:
/// a Neuron is a roster contact whose client hosts a concentrator. Administrators of the
/// Neuron (notification addresses or users with the Admin privilege) may register a sniffer
/// on its Reports/Events node; every event the Neuron logs is then forwarded live. No event
/// sink or configuration is added on the Neuron.
/// </summary>
public sealed class NeuronMonitor : IAsyncDisposable
{
	/// <summary>Data source and node holding the Neuron's events.</summary>
	public const string EventsSourceId = "Reports";
	public const string EventsNodeId = "Events";

	private static readonly TimeSpan sniffDuration = TimeSpan.FromHours(1);
	private static readonly TimeSpan renewBefore = TimeSpan.FromMinutes(10);

	/// <summary>How far back to read a Neuron's event log the first time its stream starts.</summary>
	public static TimeSpan HistoryWindow { get; set; } = TimeSpan.FromHours(24);

	private readonly AccountSession session;
	private readonly EventHub hub;
	private readonly ConcurrentDictionary<string, Watch> watches = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, bool> isNeuron = new(StringComparer.OrdinalIgnoreCase);
	private readonly Timer renewTimer;
	private XmppClient? client;
	private ConcentratorClient? concentrator;
	private Waher.Networking.XMPP.Sensor.SensorClient? sensor;
	private Waher.Networking.XMPP.Control.ControlClient? control;

	public NeuronMonitor(AccountSession session, EventHub hub)
	{
		this.session = session;
		this.hub = hub;
		session.ClientCreated += this.Session_ClientCreated;
		session.StatusChanged += this.Session_StatusChanged;
		this.renewTimer = new Timer(_ => _ = this.RenewAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

		if (session.Client is XmppClient c)
			this.Attach(c);
	}

	/// <summary>Raised when the list of Neurons or the state of one changes. Any thread.</summary>
	public event EventHandler? NeuronsChanged;

	/// <summary>Raised when someone who is not a Neuron asks to subscribe to this account's presence.</summary>
	public event EventHandler<PresenceEventArgs>? SubscriptionRequested;

	/// <summary>Raised when a Neuron sends this account a notification (a chat message). Any thread.</summary>
	public event EventHandler<IncomingNotification>? NotificationReceived;

	public AccountSession Session => this.session;

	/// <summary>Neurons known for this account.</summary>
	public IReadOnlyList<WatchedNeuron> Neurons => [.. this.watches.Values.Select(w => w.Info).Where(n => n.State != WatchState.NotANeuron).OrderBy(n => n.Label)];

	/// <summary>Adds a Neuron without network traffic, for previews and UI tests.</summary>
	internal void Preview(WatchedNeuron n)
	{
		this.watches[n.BareJid] = new Watch(n);
		this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Reads the health of a live Neuron. Returns null if it is not live through this account.</summary>
	public async Task<NeuronHealth?> ReadHealthAsync(string bareJid, CancellationToken cancel = default)
	{
		XmppClient? c = this.client;
		ConcentratorClient? cc = this.concentrator;
		if (c is null || cc is null || !this.watches.TryGetValue(bareJid, out Watch? w) || w.Info.State != WatchState.Live || w.FullJid is not string full)
			return null;

		return await HealthReader.ReadAsync(c, cc, this.session.Profile.Id, w.Info.BareJid, full, cancel);
	}

	/// <summary>
	/// The sources and nodes of a Neuron, through this account. Null when the Neuron is not
	/// online, or when this account is not an administrator there.
	/// </summary>
	public Concentrator.NeuronConcentrator? Concentrator(string bareJid)
	{
		ConcentratorClient? cc = this.concentrator;
		if (cc is null || !this.watches.TryGetValue(bareJid, out Watch? w) || w.Info.State != WatchState.Live || w.FullJid is not string full)
			return null;

		return new Concentrator.NeuronConcentrator(cc, this.session.Profile.Id, w.Info.BareJid, full, this.sensor, this.control);
	}

	/// <summary>Asks to follow a Neuron by its bare JID. It shows up once the Neuron accepts.</summary>
	public async Task AddNeuronAsync(string bareJid)
	{
		XmppClient c = this.client ?? throw new InvalidOperationException("The account is not connected.");
		await c.RequestPresenceSubscription(bareJid);
		this.GetWatch(bareJid);
		this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
	}

	private void Session_ClientCreated(object? sender, XmppClient c) => this.Attach(c);

	private void Attach(XmppClient c)
	{
		this.Detach();

		this.client = c;
		this.concentrator = new ConcentratorClient(c);
		this.sensor = new Waher.Networking.XMPP.Sensor.SensorClient(c);
		this.control = new Waher.Networking.XMPP.Control.ControlClient(c);
		c.OnPresence += this.Client_OnPresence;
		c.OnPresenceSubscribe += this.Client_OnPresenceSubscribe;
		c.OnRosterItemAdded += this.Client_OnRosterItemChanged;
		c.OnRosterItemUpdated += this.Client_OnRosterItemChanged;
		c.OnRosterItemRemoved += this.Client_OnRosterItemRemoved;
		c.OnChatMessage += this.Client_OnChatMessage;
	}

	private void Detach()
	{
		XmppClient? c = this.client;
		if (c is null)
			return;

		c.OnPresence -= this.Client_OnPresence;
		c.OnPresenceSubscribe -= this.Client_OnPresenceSubscribe;
		c.OnRosterItemAdded -= this.Client_OnRosterItemChanged;
		c.OnRosterItemUpdated -= this.Client_OnRosterItemChanged;
		c.OnRosterItemRemoved -= this.Client_OnRosterItemRemoved;
		c.OnChatMessage -= this.Client_OnChatMessage;
		this.concentrator?.Dispose();
		this.concentrator = null;
		this.sensor?.Dispose();
		this.sensor = null;
		this.control?.Dispose();
		this.control = null;
		this.client = null;
	}

	private void Session_StatusChanged(object? sender, ConnectionStatus status)
	{
		if (status == ConnectionStatus.Connected && this.client is XmppClient c)
		{
			// Presence may have arrived before the roster; look at what the client already knows.
			foreach (RosterItem item in c.Roster)
			{
				this.Client_OnRosterItemChanged(c, item);
				if (item.HasLastPresence && item.LastPresence.IsOnline)
					_ = this.CheckAsync(item.BareJid, item.LastPresenceFullJid);
			}
		}
		else if (status != ConnectionStatus.Connected)
		{
			foreach (Watch w in this.watches.Values)
				w.Update(i => i with { State = WatchState.Offline, FullJid = null, LiveSince = null }, clearSniffer: true);
			this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	private Task Client_OnRosterItemChanged(object sender, RosterItem item)
	{
		if (item.State is SubscriptionState.To or SubscriptionState.Both || this.watches.ContainsKey(item.BareJid))
		{
			Watch w = this.GetWatch(item.BareJid);
			w.Update(i => i with { Name = string.IsNullOrWhiteSpace(item.Name) ? null : item.Name });
			this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
		}

		return Task.CompletedTask;
	}

	private Task Client_OnRosterItemRemoved(object sender, RosterItem item)
	{
		if (this.watches.TryRemove(item.BareJid, out Watch? w))
		{
			_ = this.UnregisterAsync(w);
			this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
		}

		return Task.CompletedTask;
	}

	private Task Client_OnPresence(object sender, PresenceEventArgs e)
	{
		if (string.IsNullOrEmpty(e.FromBareJID) || string.Equals(e.FromBareJID, this.client?.BareJID, StringComparison.OrdinalIgnoreCase))
			return Task.CompletedTask;

		if (e.IsOnline)
			_ = this.CheckAsync(e.FromBareJID, e.From);
		else if (this.watches.TryGetValue(e.FromBareJID, out Watch? w) && string.Equals(w.Info.FullJid, e.From, StringComparison.OrdinalIgnoreCase))
		{
			w.Update(i => i with { State = WatchState.Offline, FullJid = null, LiveSince = null }, clearSniffer: true);
			this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
		}

		return Task.CompletedTask;
	}

	private async Task Client_OnPresenceSubscribe(object sender, PresenceEventArgs e)
	{
		// A Neuron asks to subscribe to its administrators before it sends them notifications.
		// Accept it automatically when the sender really is a Neuron, and subscribe back so
		// its events can be followed. Anyone else is left for the operator to decide.
		try
		{
			if (await this.IsNeuronAsync(e.From))
			{
				await e.Accept();

				RosterItem? item = this.client?.GetRosterItem(e.FromBareJID);
				if (item is null || item.State is SubscriptionState.None or SubscriptionState.From)
					await (this.client?.RequestPresenceSubscription(e.FromBareJID) ?? Task.CompletedTask);

				this.GetWatch(e.FromBareJID);
				this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
			}
			else
				this.SubscriptionRequested?.Invoke(this, e);
		}
		catch (Exception ex)
		{
			this.session.Sniffer.Exception(ex);
		}
	}

	/// <summary>
	/// True when a chat sender is a Neuron (its notifications go to the Attention list). Uses
	/// what is known already and asks the sender only when nothing is.
	/// </summary>
	public async Task<bool> IsNeuronSenderAsync(string fullJid)
	{
		string bare = XmppClient.GetBareJID(fullJid);
		if (this.watches.TryGetValue(bare, out Watch? w) && w.Info.State is WatchState.Live or WatchState.Subscribing or WatchState.Forbidden)
			return true;
		if (w?.Info.State == WatchState.NotANeuron)
			return false;
		if (this.isNeuron.TryGetValue(bare, out bool known))
			return known;

		return await this.IsNeuronAsync(fullJid);
	}

	private async Task<bool> IsNeuronAsync(string jid)
	{
		XmppClient? c = this.client;
		if (c is null || string.IsNullOrEmpty(jid))
			return false;

		ServiceDiscoveryEventArgs disco = await c.ServiceDiscoveryAsync(jid);
		bool result = disco.Ok && disco.HasAnyFeature(ConcentratorServer.NamespacesConcentrator);
		if (disco.Ok)
			this.isNeuron[XmppClient.GetBareJID(jid)] = result;

		return result;
	}

	private async Task Client_OnChatMessage(object sender, MessageEventArgs e)
	{
		// A Neuron sends its notifications as chat messages to its notification addresses.
		// Chat from anyone else is not ours to handle here.
		try
		{
			string bare = e.FromBareJID;
			if (string.IsNullOrEmpty(bare) || string.Equals(bare, this.client?.BareJID, StringComparison.OrdinalIgnoreCase))
				return;

			// Notifications go to the bare JID. A message to this client's full JID is a reply,
			// for example to a chat admin command, and belongs to that conversation.
			if (!string.IsNullOrEmpty(e.To) && e.To.Contains('/'))
				return;

			IncomingNotification? n = ReadNotification(this.session.Profile.Id, e);
			if (n is null)
				return;

			bool neuron;
			if (this.watches.TryGetValue(bare, out Watch? w) && w.Info.State is WatchState.Live or WatchState.Subscribing or WatchState.Forbidden)
				neuron = true;
			else if (w?.Info.State == WatchState.NotANeuron)
				neuron = false;
			else if (this.isNeuron.TryGetValue(bare, out bool known))
				neuron = known;
			else
				neuron = await this.IsNeuronAsync(e.From);

			if (neuron)
				this.NotificationReceived?.Invoke(this, n);
		}
		catch (Exception ex)
		{
			this.session.Sniffer.Exception(ex);
		}
	}

	/// <summary>Reads the Markdown, plain text, correction and delivery delay of a chat message.</summary>
	internal static IncomingNotification? ReadNotification(Guid accountId, MessageEventArgs e)
	{
		string? markdown = null;
		string? replaces = null;
		string? retracts = ReadRetraction(e.Message);
		DateTime timestamp = DateTime.Now;

		if (e.Message is XmlElement message)
		{
			foreach (XmlNode node in message.ChildNodes)
			{
				if (node is not XmlElement x)
					continue;

				switch (x.LocalName)
				{
					case "content" when x.NamespaceURI == "urn:xmpp:content" && x.GetAttribute("type") == "text/markdown":
						markdown = x.InnerText;
						break;

					case "replace" when x.NamespaceURI == "urn:xmpp:message-correct:0":
						replaces = x.GetAttribute("id");
						break;

					case "delay" when x.NamespaceURI == "urn:xmpp:delay":
						if (DateTime.TryParse(x.GetAttribute("stamp"), CultureInfo.InvariantCulture,
							DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime stamp))
						{
							timestamp = DateTime.SpecifyKind(stamp, DateTimeKind.Utc).ToLocalTime();
						}
						break;
				}
			}
		}

		string text = e.Body ?? string.Empty;
		markdown ??= text;
		if (string.IsNullOrWhiteSpace(markdown) && retracts is null)
			return null;

		return new IncomingNotification(accountId, e.FromBareJID, string.IsNullOrEmpty(e.Id) ? null : e.Id,
			string.IsNullOrEmpty(replaces) ? null : replaces, timestamp, markdown, text)
		{
			Retracts = retracts,
		};
	}

	/// <summary>
	/// The id of the message a stanza takes back, when it does (XEP-0424). Both spellings are
	/// read: the current one, with the id on the retract element, and the earlier one, where
	/// the retract element sits inside an "apply-to" element that carries the id.
	/// </summary>
	internal static string? ReadRetraction(XmlElement? message)
	{
		if (message is null)
			return null;

		foreach (XmlNode node in message.ChildNodes)
		{
			if (node is not XmlElement x)
				continue;

			if (x.LocalName == "retract" && x.NamespaceURI.StartsWith("urn:xmpp:message-retract:", StringComparison.Ordinal) && x.GetAttribute("id") is { Length: > 0 } id)
				return id;

			if (x.LocalName == "apply-to" && x.NamespaceURI == "urn:xmpp:fasten:0" && x.GetAttribute("id") is { Length: > 0 } target &&
				x.ChildNodes.OfType<XmlElement>().Any(c => c.LocalName == "retract" && c.NamespaceURI.StartsWith("urn:xmpp:message-retract:", StringComparison.Ordinal)))
			{
				return target;
			}
		}

		return null;
	}

	private Watch GetWatch(string bareJid)
		=> this.watches.GetOrAdd(bareJid, j => new Watch(new WatchedNeuron { AccountId = this.session.Profile.Id, BareJid = j, State = WatchState.Waiting }));

	private async Task CheckAsync(string bareJid, string fullJid)
	{
		Watch w = this.GetWatch(bareJid);
		if (!w.TryBegin(fullJid))
			return;

		try
		{
			w.Update(i => i with { FullJid = fullJid, State = WatchState.Discovering, Error = null });
			this.NeuronsChanged?.Invoke(this, EventArgs.Empty);

			if (!await this.IsNeuronAsync(fullJid))
			{
				w.Update(i => i with { State = WatchState.NotANeuron });
				this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
				return;
			}

			await this.RegisterAsync(w, fullJid);
		}
		finally
		{
			w.End();
		}
	}

	private async Task RegisterAsync(Watch w, string fullJid)
	{
		ConcentratorClient? cc = this.concentrator;
		if (cc is null)
			return;

		// A renewal keeps the stream live; only a first registration shows as subscribing.
		bool renewal = w.Info.State == WatchState.Live;
		if (!renewal)
		{
			w.Update(i => i with { State = WatchState.Subscribing });
			this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
		}

		EventSource source = new(this.session.Profile.Id, w.Info.BareJid);
		StreamSniffer sniffer = new("Events of " + w.Info.BareJid, 100);
		sniffer.LineAdded += (_, line) =>
		{
			if (SniffedEventParser.Parse(line, source) is NeuronEvent e)
				this.hub.Add(e);
		};

		DateTime expires = DateTime.Now + sniffDuration;
		TaskCompletionSource<SnifferRegistrationEventArgs> done = new();

		await cc.RegisterSniffer(fullJid, EventsNodeId, EventsSourceId, string.Empty, expires, sniffer,
			string.Empty, string.Empty, string.Empty, (_, e) =>
			{
				done.TrySetResult(e);
				return Task.CompletedTask;
			}, null);

		Task finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30)));
		if (finished != done.Task)
		{
			// A renewal that times out leaves the current registration running until it expires.
			if (!renewal)
				w.Update(i => i with { State = WatchState.Failed, Error = "The Neuron did not answer the sniffer request." });
		}
		else
		{
			SnifferRegistrationEventArgs r = done.Task.Result;
			if (r.Ok)
			{
				string? previous = w.SnifferId;
				DateTime streamStart = DateTime.Now;
				w.SetSniffer(r.SnifferId, r.Expires, fullJid);
				w.Update(i => i with { State = WatchState.Live, LiveSince = i.LiveSince ?? streamStart, Error = null });

				if (!string.IsNullOrEmpty(previous))
					_ = cc.UnregisterSniffer(fullJid, EventsNodeId, EventsSourceId, string.Empty, previous, string.Empty, string.Empty, string.Empty, null, null);
				else
					_ = this.BackfillAsync(w, fullJid, streamStart);
			}
			else
			{
				bool forbidden = r.StanzaError is ForbiddenException or NotAllowedException or NotAuthorizedException;
				w.Update(i => i with
				{
					State = forbidden ? WatchState.Forbidden : WatchState.Failed,
					Error = forbidden
						? "This account is not an administrator on the Neuron. Add it as a notification address, or give its user the Admin privilege."
						: r.ErrorText,
				});
			}
		}

		this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// Reads what the Neuron logged while nobody was listening: from where the last stream or
	/// backfill ended (at most <see cref="HistoryWindow"/> back) up to when this stream started.
	/// </summary>
	private async Task BackfillAsync(Watch w, string fullJid, DateTime until)
	{
		ConcentratorClient? cc = this.concentrator;
		if (cc is null)
			return;

		DateTime from = w.HistoryUntil is DateTime last && last > until - HistoryWindow ? last : until - HistoryWindow;
		w.Update(i => i with { HistoryState = HistoryState.Loading, HistoryError = null });
		this.NeuronsChanged?.Invoke(this, EventArgs.Empty);

		try
		{
			EventSource source = new(this.session.Profile.Id, w.Info.BareJid);
			int added = 0;
			EventHistory.SearchResult r = await EventHistory.SearchAsync(cc, fullJid, source, from, until, includeInformational: false,
				e =>
				{
					this.hub.Add(e);
					if (Interlocked.Increment(ref added) % 200 == 0)
					{
						w.Update(i => i with { HistoryCount = added });
						this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
					}
				});

			w.HistoryUntil = until;
			w.Update(i => i with
			{
				HistoryState = r.Complete ? HistoryState.Loaded : HistoryState.Failed,
				HistoryCount = r.Count,
				HistoryFrom = from,
				HistoryError = r.Complete ? null : "The Neuron stopped sending its event history before the search finished.",
			});
		}
		catch (Exception ex)
		{
			w.Update(i => i with { HistoryState = HistoryState.Failed, HistoryError = ex.Message });
			this.session.Sniffer.Exception(ex);
		}

		this.NeuronsChanged?.Invoke(this, EventArgs.Empty);
	}

	private async Task RenewAsync()
	{
		foreach (Watch w in this.watches.Values)
		{
			if (w.Info.State == WatchState.Live && w.Expires is DateTime exp && exp - DateTime.Now < renewBefore && w.FullJid is string full)
			{
				try
				{
					await this.RegisterAsync(w, full);
				}
				catch (Exception ex)
				{
					this.session.Sniffer.Exception(ex);
				}
			}
		}
	}

	private async Task UnregisterAsync(Watch w)
	{
		ConcentratorClient? cc = this.concentrator;
		if (cc is null || w.SnifferId is null || w.FullJid is null)
			return;

		try
		{
			await cc.UnregisterSniffer(w.FullJid, EventsNodeId, EventsSourceId, string.Empty, w.SnifferId, string.Empty, string.Empty, string.Empty, null, null);
		}
		catch
		{
			// Best effort; the registration expires on its own.
		}
	}

	public async ValueTask DisposeAsync()
	{
		this.renewTimer.Dispose();
		this.session.ClientCreated -= this.Session_ClientCreated;
		this.session.StatusChanged -= this.Session_StatusChanged;

		foreach (Watch w in this.watches.Values)
			await this.UnregisterAsync(w);

		this.Detach();
	}

	/// <summary>Mutable per-Neuron state behind the immutable <see cref="WatchedNeuron"/> snapshot.</summary>
	private sealed class Watch(WatchedNeuron info)
	{
		private readonly object sync = new();
		private bool busy;

		public WatchedNeuron Info { get; private set; } = info;
		public string? SnifferId { get; private set; }
		public DateTime? Expires { get; private set; }
		public string? FullJid { get; private set; }

		/// <summary>Up to when history has been read (local time).</summary>
		public DateTime? HistoryUntil { get; set; }

		public void Update(Func<WatchedNeuron, WatchedNeuron> change, bool clearSniffer = false)
		{
			lock (this.sync)
			{
				this.Info = change(this.Info);
				if (clearSniffer)
				{
					this.SnifferId = null;
					this.Expires = null;
					this.FullJid = null;
				}
			}
		}

		public void SetSniffer(string id, DateTime expires, string fullJid)
		{
			lock (this.sync)
			{
				this.SnifferId = id;
				this.Expires = expires;
				this.FullJid = fullJid;
			}
		}

		/// <summary>Starts a check unless one is running or the stream is already live on this resource.</summary>
		public bool TryBegin(string fullJid)
		{
			lock (this.sync)
			{
				if (this.busy)
					return false;
				if (this.Info.State is WatchState.Live or WatchState.NotANeuron && string.Equals(this.FullJid ?? this.Info.FullJid, fullJid, StringComparison.OrdinalIgnoreCase))
					return false;

				this.busy = true;
				return true;
			}
		}

		public void End()
		{
			lock (this.sync)
				this.busy = false;
		}
	}
}
