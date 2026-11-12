using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Neuro.Nat.App;
using Neuro.Nat.App.Features;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.Features.Telemetry;

/// <summary>
/// App telemetry (the Neuro.App.Telemetry package) on the Neurons. It asks each account's own
/// Neuron once when the account signs in, and again when that Neuron's software changes, whether
/// the package is there and may be used. Where it is not, NAT shows nothing of it. Where it is,
/// the Software tab says so in one quiet line; the dashboard comes later, on what is found here.
/// </summary>
public sealed class TelemetryFeature : INatFeature
{
	public const string FeatureKey = "telemetry";

	/// <summary>The privilege that sees everything. Partners get <see cref="ExternalPrivilege"/>, which sees less.</summary>
	public const string InternalPrivilege = "Custom.Telemetry.Internal";

	public const string ExternalPrivilege = "Custom.Telemetry.External";

	/// <summary>Where the package's web API is, on the Neuron's web address.</summary>
	public const string ApiPath = "/NeuroAppTelemetry/Api/v1";

	private readonly HttpClient http;
	private readonly Func<Guid, CancellationToken, Task<string?>>? tokensGiven;
	private readonly Func<Guid, string, IReadOnlyList<Uri>>? addressesGiven;
	private readonly Func<DateTime> utcNow;
	private readonly CancellationTokenSource stopping = new();
	private readonly object sync = new();
	private readonly Dictionary<string, TelemetryStatus> found = [];
	private readonly Dictionary<string, long> latest = [];
	private readonly HashSet<Task> running = [];
	private XmppTokens? xmppTokens;
	private IFeatureHost? host;
	private long rounds;

	/// <summary>The real thing: tokens over XMPP, web addresses from what NAT knows of the Neuron.</summary>
	public TelemetryFeature()
		: this(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, null, null, null)
	{
	}

	/// <summary>With stand-ins, for tests: the HTTP handler, the token source and the web addresses.</summary>
	internal TelemetryFeature(HttpMessageHandler handler, Func<Guid, CancellationToken, Task<string?>>? tokens,
		Func<Guid, string, IReadOnlyList<Uri>>? addresses, Func<DateTime>? utcNow)
	{
		// No redirects: a redirect would carry the token somewhere not chosen here.
		if (handler is SocketsHttpHandler sockets)
			sockets.AllowAutoRedirect = false;

		this.http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
		this.tokensGiven = tokens;
		this.addressesGiven = addresses;
		this.utcNow = utcNow ?? (() => DateTime.UtcNow);
	}

	public string Key => FeatureKey;

	public event EventHandler<FeatureNeuron?>? Changed;

	public void Start(IFeatureHost host)
	{
		this.host = host;
		if (this.tokensGiven is null)
			this.xmppTokens = new XmppTokens(host.Accounts);

		host.SignedIn += (_, accountId) => this.Ask(accountId);
		host.SoftwareChanged += (_, n) =>
		{
			if (this.Hosts(n.AccountId, n.NeuronJid))
				this.Ask(n.AccountId);
		};
	}

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

	/// <summary>True when the Neuron is the account's own (its domain is the account's).</summary>
	private bool Hosts(Guid accountId, string neuronJid)
	{
		if (this.DomainOf(accountId) is not string domain)
			return false;

		string d = NeuronNames.Domain(neuronJid);
		return d.Length == 0 || string.Equals(d, domain, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(d, this.host?.Accounts.Get(accountId)?.Profile.EffectiveDomain, StringComparison.OrdinalIgnoreCase);
	}

	private static string KeyOf(Guid accountId, string domain) => accountId.ToString("N") + "|" + domain.ToLowerInvariant();

	/// <summary>Asks the account's Neuron, in the background.</summary>
	private void Ask(Guid accountId)
	{
		if (this.DomainOf(accountId) is not string domain || this.stopping.IsCancellationRequested)
			return;

		Task t = this.RoundAsync(accountId, domain);
		lock (this.sync)
			this.running.Add(t);
		_ = t.ContinueWith(done =>
		{
			lock (this.sync)
				this.running.Remove(done);
		}, TaskScheduler.Default);
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
			this.Changed?.Invoke(this, null);
	}

	/// <summary>One round: a fresh token, then GET /Status on each web address until one answers.</summary>
	private async Task<TelemetryStatus> AskStatusAsync(Guid accountId, string domain, CancellationToken cancel)
	{
		IReadOnlyList<Uri> addresses = this.addressesGiven?.Invoke(accountId, domain) ?? NeuronWeb.Candidates(this.host?.Monitors.Attention, accountId, domain);
		if (addresses.Count == 0)
			return TelemetryStatus.Absent("no web address");

		string? token;
		try
		{
			token = this.tokensGiven is not null ? await this.tokensGiven(accountId, cancel)
				: this.xmppTokens is not null ? await this.xmppTokens.GetAsync(accountId, cancel)
				: null;
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

				return response.StatusCode switch
				{
					HttpStatusCode.OK => TelemetryStatus.Parse(body, address) ?? TelemetryStatus.Absent("not the telemetry API"),
					HttpStatusCode.Forbidden => new TelemetryStatus { Presence = FeaturePresence.NotAllowed, WebAddress = address, Privilege = TelemetryStatus.PrivilegeIn(body) },
					HttpStatusCode.NotFound => TelemetryStatus.Absent("404"),
					HttpStatusCode.Unauthorized => TelemetryStatus.Unknown("401, not signed in"),
					HttpStatusCode code => TelemetryStatus.Unknown(((int)code).ToString(CultureInfo.InvariantCulture)),
				};
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

	public async ValueTask DisposeAsync()
	{
		this.stopping.Cancel();
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
		this.stopping.Dispose();
	}
}
