using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Neuro.Nat.App;
using Neuro.Nat.App.Features;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Storage;
using Neuro.Nat.Features.Telemetry.Data;
using Neuro.Nat.Features.Telemetry.ViewModels;
using Neuro.Nat.Features.Telemetry.Views;

namespace Neuro.Nat.Features.Telemetry;

/// <summary>
/// App telemetry (the Neuro.App.Telemetry package) on the Neurons. It asks each account's own
/// Neuron once when the account signs in, and again when that Neuron's software changes, whether
/// the package is there and may be used. Where it is not, NAT shows nothing of it. Where it is,
/// the Software tab says so in one quiet line, the Neuron's page gets an App telemetry tab, the
/// rail an App telemetry place, the Inbox what needs attention, and the big screen App numbers.
/// </summary>
public sealed class TelemetryFeature : INatFeature
{
	public const string FeatureKey = "telemetry";

	/// <summary>The privilege that sees everything. Partners get <see cref="ExternalPrivilege"/>, which sees less.</summary>
	public const string InternalPrivilege = "Custom.Telemetry.Internal";

	public const string ExternalPrivilege = "Custom.Telemetry.External";

	/// <summary>Where the package's web API is, on the Neuron's web address.</summary>
	public const string ApiPath = "/NeuroAppTelemetry/Api/v1";

	/// <summary>The key of the tab on a collector's page.</summary>
	public const string TabKey = "telemetry.dashboard";

	/// <summary>The key of the place in the rail.</summary>
	public const string PlaceKey = "telemetry";

	/// <summary>The key of the big screen page.</summary>
	public const string ScreenKey = "app-numbers";

	public const string Title = "App telemetry";

	private readonly HttpClient http;
	private readonly Func<Guid, CancellationToken, Task<string?>>? tokensGiven;
	private readonly Func<Guid, string, IReadOnlyList<Uri>>? addressesGiven;
	private readonly Func<DateTime> utcNow;
	private readonly IFeatureAnswerStore store;
	private readonly CancellationTokenSource stopping = new();
	private readonly object sync = new();
	private readonly Dictionary<string, TelemetryStatus> found = [];
	private readonly Dictionary<string, long> latest = [];
	private readonly HashSet<Task> running = [];
	private readonly Dictionary<string, CollectorData> collectors = [];
	private readonly Dictionary<string, DashboardViewModel> dashboards = [];
	private readonly Dictionary<string, DateTime> firstSeen = [];
	private XmppTokens? xmppTokens;
	private TelemetryFetcher? fetcher;
	private IFeatureHost? host;
	private Timer? hourly;
	private TelemetryPlaceViewModel? place;
	private AppNumbersViewModel? appNumbers;
	private long rounds;

	/// <summary>The real thing: tokens over XMPP, web addresses from what NAT knows of the Neuron, numbers kept in the store.</summary>
	public TelemetryFeature(IFeatureAnswerStore? store = null)
		: this(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, null, null, null, store)
	{
	}

	/// <summary>With stand-ins, for tests: the HTTP handler, the token source, the web addresses, the clock and the store.</summary>
	internal TelemetryFeature(HttpMessageHandler handler, Func<Guid, CancellationToken, Task<string?>>? tokens,
		Func<Guid, string, IReadOnlyList<Uri>>? addresses, Func<DateTime>? utcNow, IFeatureAnswerStore? store = null)
	{
		// No redirects: a redirect would carry the token somewhere not chosen here.
		if (handler is SocketsHttpHandler sockets)
			sockets.AllowAutoRedirect = false;

		this.http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
		this.tokensGiven = tokens;
		this.addressesGiven = addresses;
		this.utcNow = utcNow ?? (() => DateTime.UtcNow);
		this.store = store ?? new InMemoryFeatureAnswerStore();
	}

	public string Key => FeatureKey;

	public event EventHandler<FeatureNeuron?>? Changed;

	/// <summary>False in tests that only look at finding the package: no numbers are fetched after it is found.</summary>
	internal bool FetchesNumbers { get; set; } = true;

	/// <summary>The fetcher, once started. For tests.</summary>
	internal TelemetryFetcher? Fetcher => this.fetcher;

	public void Start(IFeatureHost host)
	{
		this.host = host;
		if (this.tokensGiven is null)
			this.xmppTokens = new XmppTokens(host.Accounts);

		this.fetcher = new TelemetryFetcher(this.http, this.TokenAsync, this.store, this.utcNow);
		_ = this.fetcher.LoadAsync();

		host.SignedIn += (_, accountId) => this.Ask(accountId);
		host.SoftwareChanged += (_, n) =>
		{
			if (this.Hosts(n.AccountId, n.NeuronJid))
				this.Ask(n.AccountId);
		};

		// The Inbox and the big screen follow the numbers without the dashboard open: at most once an hour.
		this.hourly = new Timer(_ => this.RefreshAll(), null, TelemetryFetcher.Fresh, TelemetryFetcher.Fresh);
	}

	private Task<string?> TokenAsync(Guid accountId, CancellationToken cancel)
		=> this.tokensGiven is not null ? this.tokensGiven(accountId, cancel)
			: this.xmppTokens is not null ? this.xmppTokens.GetAsync(accountId, cancel)
			: Task.FromResult<string?>(null);

	// ----- What NAT shows -----

	public FeaturePresence PresenceOn(Guid accountId, string neuronJid)
		=> this.StatusOf(accountId, neuronJid)?.Presence ?? FeaturePresence.Unknown;

	public IReadOnlyList<string> SoftwareLines(Guid accountId, string neuronJid)
	{
		TelemetryStatus? s = this.StatusOf(accountId, neuronJid);
		return s?.Presence switch
		{
			FeaturePresence.Present => [this.Describe(s)],
			FeaturePresence.NotAllowed => ["App telemetry: your account needs " + (s.Privilege ?? InternalPrivilege)],
			_ => [],
		};
	}

	/// <summary>What was last found on a Neuron, or null when it was not asked (or is not the account's own Neuron).</summary>
	public TelemetryStatus? StatusOf(Guid accountId, string neuronJid)
	{
		if (!this.Hosts(accountId, neuronJid) || this.DomainOf(accountId) is not string domain)
			return null;

		lock (this.sync)
			return this.found.TryGetValue(KeyOf(accountId, domain), out TelemetryStatus? s) ? s : null;
	}

	/// <summary>"App telemetry: installed, numbers up to yesterday, collects from 4 Neurons"</summary>
	internal string Describe(TelemetryStatus s)
	{
		DateTime yesterday = this.utcNow().Date.AddDays(-1);
		string numbers = s.LastComputedDay is not DateTime day ? "no numbers yet"
			: day >= yesterday ? "numbers up to yesterday"
			: "numbers up to " + day.ToString(day.Year == yesterday.Year ? "d MMMM" : "d MMMM yyyy", CultureInfo.InvariantCulture);
		string collects = s.Sources switch
		{
			0 => string.Empty,
			1 => ", collects from 1 Neuron",
			int n => $", collects from {n} Neurons",
		};
		return "App telemetry: installed, " + numbers + collects;
	}

	// ----- The dashboards -----

	/// <summary>
	/// The Neurons whose numbers make a dashboard: where the API answers this account, unless
	/// another one found collects that Neuron's numbers already (they are in its dashboard).
	/// </summary>
	public IReadOnlyList<TelemetryCollector> Collectors()
	{
		List<(TelemetryCollector Collector, TelemetryStatus Status)> present = [];
		lock (this.sync)
		{
			foreach ((string key, TelemetryStatus s) in this.found)
			{
				if (s.Presence != FeaturePresence.Present || s.WebAddress is null)
					continue;

				int bar = key.IndexOf('|', StringComparison.Ordinal);
				if (Guid.TryParseExact(key[..bar], "N", out Guid account))
					present.Add((new TelemetryCollector(account, key[(bar + 1)..], s.WebAddress), s));
			}
		}

		HashSet<string> collected = new(present.Where(p => p.Status.Sources > 0).SelectMany(p => p.Status.SourceNames), StringComparer.OrdinalIgnoreCase);
		return [.. present
			.Where(p => p.Status.Sources > 0 || !collected.Contains(p.Status.Neuron ?? p.Collector.Domain) && !collected.Contains(p.Collector.Domain))
			.Select(p => p.Collector)
			.OrderBy(c => this.NameOf(c.Domain), StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>A collector's numbers. Never shared with another collector.</summary>
	internal CollectorData DataOf(TelemetryCollector c)
	{
		lock (this.sync)
		{
			if (!this.collectors.TryGetValue(c.Key, out CollectorData? d) || d.Collector != c)
			{
				d = new CollectorData(c, this.fetcher ?? throw new InvalidOperationException("Not started."), this.NameOf, this.utcNow);
				d.Changed += (_, _) => this.NumbersChanged();
				this.collectors[c.Key] = d;
			}

			return d;
		}
	}

	/// <summary>A collector's dashboard, made once and kept, so the tab and the rail show the same one.</summary>
	internal DashboardViewModel DashboardOf(TelemetryCollector c)
	{
		lock (this.sync)
		{
			if (this.dashboards.TryGetValue(c.Key, out DashboardViewModel? d) && d.Collector == c)
				return d;
		}

		DashboardViewModel made = new(this.DataOf(c), this.NameOf(c.Domain), new FeatureTarget(TabKey, c.AccountId, c.Domain), this.utcNow);
		lock (this.sync)
			this.dashboards[c.Key] = made;
		return made;
	}

	private string NameOf(string neuron)
	{
		IEnumerable<WatchedNeuron> known = this.host?.Monitors.Neurons ?? [];
		return NeuronNames.For(known, neuron);
	}

	public IReadOnlyList<FeatureTab> NeuronTabs(Guid accountId, string neuronJid)
	{
		if (!this.Hosts(accountId, neuronJid) || this.DomainOf(accountId) is not string domain)
			return [];

		TelemetryCollector? c = this.Collectors().FirstOrDefault(x => x.AccountId == accountId && string.Equals(x.Domain, domain, StringComparison.OrdinalIgnoreCase));
		return c is null ? [] : [new FeatureTab(TabKey, Title, () => this.DashboardOf(c))];
	}

	public IReadOnlyList<FeaturePlace> Places()
		=> this.Collectors().Count == 0 ? [] : [new FeaturePlace(PlaceKey, Title, "Chart", () => this.Place())];

	private TelemetryPlaceViewModel Place()
	{
		this.place ??= new TelemetryPlaceViewModel(() => [.. this.Collectors().Select(this.DashboardOf)]);
		this.place.Sync();
		return this.place;
	}

	// ----- The Inbox -----

	/// <summary>
	/// What needs attention, from each collector's latest overview: a Neuron that stopped sending
	/// numbers, a success rate that fell, an app version that reads worse. An item waits until the
	/// numbers no longer show it.
	/// </summary>
	public IReadOnlyList<FeatureInboxItem> InboxItems()
	{
		List<FeatureInboxItem> items = [];
		foreach (TelemetryCollector c in this.Collectors())
		{
			if (this.DataOf(c).Overview?.Model is not DashboardModel m)
				continue;

			foreach (Finding f in m.Findings().Where(IsForInbox))
			{
				string key = "telemetry|" + c.Key + "|" + f.Kind + "|" + f.Subject;
				DateTime when;
				lock (this.sync)
				{
					if (!this.firstSeen.TryGetValue(key, out when))
						this.firstSeen[key] = when = this.utcNow().ToLocalTime();
				}

				items.Add(new FeatureInboxItem(key, c.AccountId, c.Domain, Title, f.Title, when)
				{
					Detail = f.Detail,
					Tone = f.Level,
					Opens = new FeatureTarget(TabKey, c.AccountId, c.Domain) { Part = f.Page ?? Pages.Overview },
				});
			}
		}

		return items;
	}

	/// <summary>The three things worth an Inbox item: a Neuron that stopped sending, a rate that fell, a worse app version.</summary>
	public static bool IsForInbox(Finding f) => f.Kind switch
	{
		"neuron" => f.Level == "bad",
		"rate" or "version" => true,
		_ => false,
	};

	// ----- Search -----

	public IReadOnlyList<FeatureSearchHit> SearchHits()
	{
		List<FeatureSearchHit> hits = [];
		List<TelemetryCollector> all = [.. this.Collectors()];
		foreach (TelemetryCollector c in all)
		{
			string on = all.Count > 1 ? Title + " on " + this.NameOf(c.Domain) : Title;
			DashboardModel? m = this.DataOf(c).Overview?.Model;
			IEnumerable<(string Key, string Title)> pages = m?.VisiblePages() ?? [(Pages.Overview, "Overview")];
			foreach ((string key, string title) in pages)
			{
				hits.Add(new FeatureSearchHit(title, on, new FeatureTarget(TabKey, c.AccountId, c.Domain) { Part = key })
				{
					Words = "app telemetry numbers dashboard " + key,
				});
			}

			foreach (Api.CatalogMetric metric in m?.Catalog.Metrics.Where(x => !x.Deprecated) ?? [])
			{
				hits.Add(new FeatureSearchHit(metric.Name, on, new FeatureTarget(TabKey, c.AccountId, c.Domain) { Part = "metric:" + metric.Key })
				{
					Kind = "Metric",
					Words = metric.Key + " " + metric.Description,
				});
			}
		}

		return hits;
	}

	// ----- The big screen -----

	public IReadOnlyList<FeatureScreenPage> BigScreenPages()
		=> this.AppNumberRows().Count == 0 ? [] : [new FeatureScreenPage(ScreenKey, "App numbers", () => this.AppNumbers())];

	/// <summary>Each collector's key numbers, in a row of its own.</summary>
	internal IReadOnlyList<AppNumbersRow> AppNumberRows()
	{
		List<TelemetryCollector> all = [.. this.Collectors()];
		List<AppNumbersRow> rows = [];
		foreach (TelemetryCollector c in all)
		{
			if (this.DataOf(c).Overview?.Model is not DashboardModel m)
				continue;

			List<KeyNumber> numbers = [.. m.KeyNumbers()];
			if (numbers.Count == 0)
				continue;

			string fresh = m.Status?.Self?.LastDay is DateTime d ? "Last 30 days, through " + Words.FullDay(d) : string.Empty;
			rows.Add(new AppNumbersRow(this.NameOf(c.Domain), fresh, numbers, all.Count > 1));
		}

		return rows;
	}

	internal AppNumbersViewModel AppNumbers()
	{
		this.appNumbers ??= new AppNumbersViewModel();
		this.appNumbers.Set(this.AppNumberRows());
		return this.appNumbers;
	}

	private void NumbersChanged()
	{
		if (this.appNumbers is AppNumbersViewModel vm)
		{
			if (Dispatcher.UIThread.CheckAccess())
				vm.Set(this.AppNumberRows());
			else
				Dispatcher.UIThread.Post(() => vm.Set(this.AppNumberRows()));
		}

		this.Changed?.Invoke(this, null);
	}

	/// <summary>Which Neurons have the package changed: the rail's picker follows, then NAT hears of it.</summary>
	private void FoundChanged()
	{
		if (this.place is TelemetryPlaceViewModel p)
			Dispatcher.UIThread.Post(p.Sync);
		this.Changed?.Invoke(this, null);
	}

	public IReadOnlyList<IDataTemplate> Templates() =>
	[
		new FuncDataTemplate<DashboardViewModel>((_, _) => new DashboardView(), supportsRecycling: false),
		new FuncDataTemplate<TelemetryPlaceViewModel>((_, _) => new PlaceView(), supportsRecycling: false),
		new FuncDataTemplate<AppNumbersViewModel>((_, _) => new AppNumbersView(), supportsRecycling: false),
	];

	/// <summary>Asks every collector for its overview, if an hour has passed. In the background.</summary>
	internal void RefreshAll()
	{
		foreach (TelemetryCollector c in this.Collectors())
			this.Track(this.RefreshAsync(c));
	}

	private async Task RefreshAsync(TelemetryCollector c)
	{
		try
		{
			await this.DataOf(c).GetAsync([Pages.Overview], null, ask: true, this.stopping.Token);
		}
		catch (OperationCanceledException)
		{
			// Stopping.
		}
		catch (Exception ex)
		{
			AppLog.Error($"App telemetry on {c.Domain} could not be read.", ex);
		}
	}

	// ----- Asking -----

	/// <summary>
	/// The account's own Neuron is the one whose token opens its web API, so that is the one asked:
	/// the account's domain. Other Neurons the account watches are not asked.
	/// </summary>
	private string? DomainOf(Guid accountId)
	{
		AccountSession? s = this.host?.Accounts.Get(accountId);
		if (s is null)
			return null;

		string? connected = s.Client?.Domain;
		return !string.IsNullOrEmpty(connected) ? connected : s.Profile.EffectiveDomain;
	}

	/// <summary>
	/// True when the Neuron is the account's own: it calls itself by the account's domain. Until it
	/// has said what it calls itself, the domain of its address stands in, though that can be its
	/// parent broker's (see <see cref="WatchedNeuron.OwnName"/>).
	/// </summary>
	private bool Hosts(Guid accountId, string neuronJid)
	{
		if (this.DomainOf(accountId) is not string domain)
			return false;

		string? profileDomain = this.host?.Accounts.Get(accountId)?.Profile.EffectiveDomain;
		bool IsAccounts(string name) => string.Equals(name, domain, StringComparison.OrdinalIgnoreCase) || string.Equals(name, profileDomain, StringComparison.OrdinalIgnoreCase);

		string? own = this.host?.Monitors.For(accountId)?.Neurons
			.FirstOrDefault(n => string.Equals(n.BareJid, neuronJid, StringComparison.OrdinalIgnoreCase))?.OwnName;
		if (!string.IsNullOrWhiteSpace(own))
			return IsAccounts(own);

		string d = NeuronNames.Domain(neuronJid);
		return d.Length == 0 || IsAccounts(d);
	}

	private static string KeyOf(Guid accountId, string domain) => accountId.ToString("N") + "|" + domain.ToLowerInvariant();

	/// <summary>Asks the account's Neuron, in the background.</summary>
	private void Ask(Guid accountId)
	{
		if (this.DomainOf(accountId) is not string domain || this.stopping.IsCancellationRequested)
			return;

		this.Track(this.RoundAsync(accountId, domain));
	}

	private void Track(Task t)
	{
		lock (this.sync)
			this.running.Add(t);
		_ = t.ContinueWith(done =>
		{
			lock (this.sync)
				this.running.Remove(done);
		}, TaskScheduler.Default);
	}

	/// <summary>Asks the account's Neuron now, as a sign-in does, and waits for the answer. For tests.</summary>
	internal async Task FindAsync(Guid accountId)
	{
		this.Ask(accountId);
		await this.SettledAsync();
	}

	/// <summary>Waits until every question asked so far has its answer. For tests.</summary>
	internal async Task SettledAsync()
	{
		while (true)
		{
			Task[] now;
			lock (this.sync)
				now = [.. this.running.Where(t => !t.IsCompleted)];
			if (now.Length == 0)
				return;
			await Task.WhenAll(now);
		}
	}

	private async Task RoundAsync(Guid accountId, string domain)
	{
		string key = KeyOf(accountId, domain);
		long round = Interlocked.Increment(ref this.rounds);
		lock (this.sync)
			this.latest[key] = round;

		TelemetryStatus result;
		try
		{
			result = await this.AskStatusAsync(accountId, domain, this.stopping.Token);
		}
		catch (Exception ex)
		{
			result = TelemetryStatus.Unknown(ex.GetType().Name);
		}

		bool changed;
		lock (this.sync)
		{
			// A newer round for the same Neuron decides.
			if (this.latest[key] != round)
				return;

			this.found.TryGetValue(key, out TelemetryStatus? was);

			// No answer this time (not signed in, no connection): what was known stays.
			if (result.Presence == FeaturePresence.Unknown && was is not null)
				result = was;

			changed = was != result;
			this.found[key] = result;
		}

		AppLog.Info($"App telemetry on {domain}: {result.Presence}" + (result.Problem is null ? "." : $" ({result.Problem})."));
		if (changed)
			this.FoundChanged();

		// A dashboard: its overview, for the Inbox and the big screen (at most once an hour).
		TelemetryCollector? c = this.FetchesNumbers ? this.Collectors().FirstOrDefault(x => x.Key == key) : null;
		if (c is not null)
			await this.RefreshAsync(c);
	}

	/// <summary>One round: a fresh token, then GET /Status on each web address until one answers.</summary>
	private async Task<TelemetryStatus> AskStatusAsync(Guid accountId, string domain, CancellationToken cancel)
	{
		AccountProfile? profile = this.host?.Accounts.Get(accountId)?.Profile;
		IReadOnlyList<Uri> addresses = this.addressesGiven?.Invoke(accountId, domain)
			?? NeuronWeb.Candidates(profile?.WebAddress, this.host?.Monitors.Attention, accountId, domain);
		if (addresses.Count == 0)
			return TelemetryStatus.Absent("no web address");

		string? token;
		try
		{
			token = await this.TokenAsync(accountId, cancel);
		}
		catch (Exception ex)
		{
			return TelemetryStatus.Unknown("no token: " + ex.GetType().Name);
		}

		if (string.IsNullOrEmpty(token))
			return TelemetryStatus.Unknown("no token");

		string? lastProblem = null;
		foreach (Uri address in addresses)
		{
			try
			{
				using HttpRequestMessage request = new(HttpMethod.Get, new Uri(address, ApiPath + "/Status"));
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
				request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
				using HttpResponseMessage response = await this.http.SendAsync(request, cancel);
				string body = await response.Content.ReadAsStringAsync(cancel);

				TelemetryStatus status = response.StatusCode switch
				{
					HttpStatusCode.OK => TelemetryStatus.Parse(body, address) ?? TelemetryStatus.Absent("not the telemetry API"),
					HttpStatusCode.Forbidden => new TelemetryStatus { Presence = FeaturePresence.NotAllowed, WebAddress = address, Privilege = TelemetryStatus.PrivilegeIn(body) },
					HttpStatusCode.NotFound => TelemetryStatus.Absent("404"),
					HttpStatusCode.Unauthorized => TelemetryStatus.Unknown("401, not signed in"),
					HttpStatusCode code => TelemetryStatus.Unknown(((int)code).ToString(CultureInfo.InvariantCulture)),
				};

				if (status.Presence is FeaturePresence.Present or FeaturePresence.NotAllowed)
					await this.RememberWebAddressAsync(accountId, profile, address, domain);
				return status;
			}
			catch (HttpRequestException ex)
			{
				lastProblem = ex.HttpRequestError.ToString();
			}
			catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
			{
				lastProblem = "timeout";
			}
		}

		return TelemetryStatus.Unknown(lastProblem ?? "no answer");
	}

	/// <summary>
	/// The account had no web address and one from the Neuron's own links answered: the account
	/// keeps it, so the next round asks there first and the account's form shows it.
	/// </summary>
	private async Task RememberWebAddressAsync(Guid accountId, AccountProfile? profile, Uri address, string domain)
	{
		if (profile is null || this.host is null || !string.IsNullOrWhiteSpace(profile.WebAddress) ||
			string.Equals(address.ToString(), "https://" + domain + "/", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		try
		{
			await this.host.Accounts.UpdateAsync(profile with { WebAddress = address.ToString() });
		}
		catch (Exception ex)
		{
			AppLog.Error("The Neuron's web address could not be kept with the account.", ex);
		}
	}

	public async ValueTask DisposeAsync()
	{
		this.stopping.Cancel();
		if (this.hourly is not null)
			await this.hourly.DisposeAsync();
		try
		{
			await this.SettledAsync();
		}
		catch (Exception)
		{
			// Stopping: what was being asked no longer matters.
		}

		this.http.Dispose();
		this.xmppTokens?.Dispose();
		this.fetcher?.Dispose();
		this.stopping.Dispose();
	}
}
