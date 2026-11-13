using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Neuro.Nat.App.Features;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Storage;
using Neuro.Nat.Features.Telemetry.Data;

namespace Neuro.Nat.Features.Telemetry.Tests;

/// <summary>
/// App telemetry on a Neuron the account administers but has no account on: asked over XMPP, at
/// the Neuron's own client, with nothing to log in with. Stand-ins answer for the web and for the
/// Neuron's client. No live Neuron.
/// </summary>
public class TelemetryOverXmppTests
{
	/// <summary>The account is on this Neuron. It has no telemetry.</summary>
	private const string Lab = "lab.example.com";

	/// <summary>Another Neuron's own client, an account on that Neuron's parent broker, and what the Neuron calls itself.</summary>
	private const string AuthClient = "auth-neuron@example.net";
	private const string Auth = "auth.example.net";

	private static readonly DateTime Now = new(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc);
	private static readonly DateTime Live = new(2026, 11, 12, 9, 0, 0);

	private static string StatusOf(string neuron) => $$"""
		{"neurons":[{"neuron":"{{neuron}}","role":"self","metricsVersion":5,"lastComputedDay":"2026-11-11","lastCompute":"2026-11-12T02:10:00.0000000Z",
		"lastComputeError":null,"computing":false,"lastCollect":null,"lastCollectSuccess":null,"lastCollectError":null,"lastCollectFacts":null}]}
		""";

	private static HttpResponseMessage Json(HttpStatusCode code, string body = "")
		=> new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	/// <summary>Answers in place of a Neuron's web server, and counts what was asked.</summary>
	private sealed class StandInWeb(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
	{
		public List<Uri> Asked { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			lock (this.Asked)
				this.Asked.Add(request.RequestUri!);
			return Task.FromResult(answer(request));
		}
	}

	/// <summary>Answers in place of a Neuron's own client, and remembers what was sent to it.</summary>
	private sealed class StandInClient(Func<XmppRoute, HttpRequestMessage, HttpResponseMessage> answer)
	{
		public List<(XmppRoute Route, string Path, string? Authorization, string? IfNoneMatch)> Asked { get; } = [];

		public Task<HttpResponseMessage> SendAsync(XmppRoute route, HttpRequestMessage request, CancellationToken cancel)
		{
			lock (this.Asked)
				this.Asked.Add((route, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), request.Headers.IfNoneMatch.FirstOrDefault()?.ToString()));
			return Task.FromResult(answer(route, request));
		}
	}

	private sealed class StandInHost(AccountManager accounts, MonitorService monitors) : IFeatureHost
	{
		public AccountManager Accounts => accounts;
		public MonitorService Monitors => monitors;
		public event EventHandler<Guid>? SignedIn;
		public event EventHandler<FeatureNeuron>? SoftwareChanged;
		public void SignIn(Guid account) => this.SignedIn?.Invoke(this, account);
		public void Software(Guid account, string jid) => this.SoftwareChanged?.Invoke(this, new FeatureNeuron(account, jid));
	}

	private sealed record Sample(TelemetryFeature Feature, StandInHost Host, StandInWeb Web, StandInClient Client, Guid Account, List<string> Tokens) : IAsyncDisposable
	{
		public NeuronMonitor Monitor => this.Host.Monitors.For(this.Account)!;

		/// <summary>The Neuron's events stream through the account: it administers the Neuron.</summary>
		public async Task GoesLiveAsync(string jid, string? ownName, DateTime? since = null)
		{
			this.Monitor.Preview(new WatchedNeuron { AccountId = this.Account, BareJid = jid, OwnName = ownName, State = WatchState.Live, LiveSince = since ?? Live });
			await this.Feature.SettledAsync();
		}

		public ValueTask DisposeAsync() => this.Feature.DisposeAsync();
	}

	private static async Task<Sample> SampleAsync(Func<XmppRoute, HttpRequestMessage, HttpResponseMessage> client, Func<HttpRequestMessage, HttpResponseMessage>? web = null)
	{
		EngineRuntime.Initialize();
		AccountManager accounts = new(new InMemoryAccountStore(), new InMemoryCredentialVault());
		AccountSession ops = await accounts.AddAsync(new AccountProfile { Host = Lab, UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		StandInHost host = new(accounts, new MonitorService(accounts, new EventHub()));
		StandInWeb standInWeb = new(web ?? (_ => Json(HttpStatusCode.NotFound)));
		StandInClient standInClient = new(client);

		List<string> tokens = [];
		TelemetryFeature feature = new(standInWeb,
			(_, _) =>
			{
				lock (tokens)
					tokens.Add("token");
				return Task.FromResult<string?>("token");
			},
			(_, domain) => [new Uri("https://" + domain + "/")],
			() => Now)
		{
			FetchesNumbers = false,
			OverXmppGiven = standInClient.SendAsync,
		};
		feature.Start(host);
		return new Sample(feature, host, standInWeb, standInClient, ops.Profile.Id, tokens);
	}

	[Fact]
	public async Task AnotherNeuronTheAccountAdministersIsAskedOverXmpp()
	{
		await using Sample s = await SampleAsync((_, _) => Json(HttpStatusCode.OK, StatusOf(Auth)));
		int changes = 0;
		s.Feature.Changed += (_, _) => Interlocked.Increment(ref changes);

		// Before its events stream, nothing is asked: the account may not administer it.
		s.Monitor.Preview(new WatchedNeuron { AccountId = s.Account, BareJid = AuthClient, OwnName = Auth, State = WatchState.Subscribing });
		await s.Feature.SettledAsync();
		Assert.Empty(s.Client.Asked);
		Assert.Equal(FeaturePresence.Unknown, s.Feature.PresenceOn(s.Account, AuthClient));

		await s.GoesLiveAsync(AuthClient, Auth);

		// Asked at the Neuron's own client, by this account, with nothing to log in with. Not over the web, and no token fetched.
		(XmppRoute route, string path, string? authorization, _) = Assert.Single(s.Client.Asked);
		Assert.Equal(s.Account, route.AccountId);
		Assert.Equal(AuthClient, route.NeuronJid, ignoreCase: true);
		Assert.Equal("/NeuroAppTelemetry/Api/v1/Status", path);
		Assert.Null(authorization);
		Assert.Empty(s.Web.Asked);
		Assert.Empty(s.Tokens);

		Assert.Equal(FeaturePresence.Present, s.Feature.PresenceOn(s.Account, AuthClient));
		Assert.Equal(["App telemetry: installed, numbers up to yesterday"], s.Feature.SoftwareLines(s.Account, AuthClient));
		Assert.Equal(1, changes);

		// Its dashboard: reached over XMPP, a tab on that Neuron's page, and a place in the rail.
		TelemetryCollector c = Assert.Single(s.Feature.Collectors());
		Assert.Equal(AuthClient, c.NeuronJid, ignoreCase: true);
		Assert.True(XmppRoute.Is(c.WebAddress));
		Assert.Single(s.Feature.NeuronTabs(s.Account, AuthClient));
		Assert.Single(s.Feature.Places());

		// The account's own Neuron is another matter: nothing was found there.
		Assert.Empty(s.Feature.NeuronTabs(s.Account, Lab));
	}

	[Fact]
	public async Task AskedOncePerStreamAndAgainWhenItsSoftwareChanges()
	{
		await using Sample s = await SampleAsync((_, _) => Json(HttpStatusCode.OK, StatusOf(Auth)));

		await s.GoesLiveAsync(AuthClient, Auth);
		Assert.Single(s.Client.Asked);

		// More news of the same stream is not a reason to ask again.
		await s.GoesLiveAsync(AuthClient, Auth);
		Assert.Single(s.Client.Asked);

		// It came back with a new stream: asked again.
		await s.GoesLiveAsync(AuthClient, Auth, Live.AddHours(2));
		Assert.Equal(2, s.Client.Asked.Count);

		// Its software changed (the package may just have been installed): asked again.
		s.Host.Software(s.Account, AuthClient);
		await s.Feature.SettledAsync();
		Assert.Equal(3, s.Client.Asked.Count);
		Assert.Empty(s.Web.Asked);
	}

	[Fact]
	public async Task TheAccountsOwnNeuronIsAskedOverTheWebOnly()
	{
		// The account's own Neuron, watched through its client on the parent broker: it calls itself by the account's domain.
		await using Sample s = await SampleAsync((_, _) => Json(HttpStatusCode.OK, StatusOf(Lab)), _ => Json(HttpStatusCode.OK, StatusOf(Lab)));

		await s.GoesLiveAsync("lab-neuron@example.com", Lab);
		Assert.Empty(s.Client.Asked);

		s.Host.SignIn(s.Account);
		await s.Feature.SettledAsync();
		Assert.Single(s.Web.Asked);
		Assert.Empty(s.Client.Asked);

		TelemetryCollector c = Assert.Single(s.Feature.Collectors());
		Assert.Null(c.NeuronJid);
		Assert.Single(s.Feature.NeuronTabs(s.Account, "lab-neuron@example.com"));
	}

	[Fact]
	public async Task ANeuronReachedBothWaysIsOneDashboard()
	{
		// The account's own Neuron goes live before it has said what it calls itself: by its address it looks like another Neuron.
		await using Sample s = await SampleAsync((_, _) => Json(HttpStatusCode.OK, StatusOf(Lab)), _ => Json(HttpStatusCode.OK, StatusOf(Lab)));
		s.Host.SignIn(s.Account);
		await s.GoesLiveAsync("lab-neuron@example.com", null);
		Assert.Single(s.Client.Asked);
		Assert.Single(s.Web.Asked);

		// Both answered for lab.example.com: one dashboard, the web's, and the tab on its page shows that one.
		TelemetryCollector c = Assert.Single(s.Feature.Collectors());
		Assert.Null(c.NeuronJid);
		Assert.Single(s.Feature.NeuronTabs(s.Account, "lab-neuron@example.com"));

		// It says what it calls itself: the account's own. Still one dashboard.
		await s.GoesLiveAsync("lab-neuron@example.com", Lab);
		Assert.Null(Assert.Single(s.Feature.Collectors()).NeuronJid);
		Assert.Single(s.Client.Asked);
	}

	[Theory]
	[InlineData(HttpStatusCode.NotFound)]       // No telemetry there.
	[InlineData(HttpStatusCode.Unauthorized)]   // An earlier package: it knows only logins to the Neuron's web.
	[InlineData(HttpStatusCode.BadGateway)]
	public async Task NothingThereShowsNothing(HttpStatusCode code)
	{
		await using Sample s = await SampleAsync((_, _) => Json(code));
		int changes = 0;
		s.Feature.Changed += (_, _) => Interlocked.Increment(ref changes);

		await s.GoesLiveAsync(AuthClient, Auth);

		Assert.Single(s.Client.Asked);
		Assert.NotEqual(FeaturePresence.Present, s.Feature.PresenceOn(s.Account, AuthClient));
		Assert.Empty(s.Feature.SoftwareLines(s.Account, AuthClient));
		Assert.Empty(s.Feature.Collectors());
		Assert.Empty(s.Feature.NeuronTabs(s.Account, AuthClient));
		Assert.Empty(s.Feature.Places());
		Assert.Equal(0, changes);
	}

	[Fact]
	public async Task ANeuronThatDoesNotAnswerShowsNothing()
	{
		await using Sample s = await SampleAsync((_, _) => throw new HttpRequestException("The Neuron did not answer over XMPP."));
		await s.GoesLiveAsync(AuthClient, Auth);

		Assert.Single(s.Client.Asked);
		Assert.Equal(FeaturePresence.Unknown, s.Feature.PresenceOn(s.Account, AuthClient));
		Assert.Empty(s.Feature.Collectors());
	}

	[Fact]
	public async Task NumbersAreFetchedOverXmppWithoutAToken()
	{
		DateTime now = Now;
		int tokens = 0;
		StandInWeb web = new(_ => Json(HttpStatusCode.InternalServerError));
		StandInClient client = new((_, request) =>
		{
			if (request.Headers.IfNoneMatch.Count > 0)
				return new HttpResponseMessage(HttpStatusCode.NotModified);

			HttpResponseMessage answer = Json(HttpStatusCode.OK, "{\"metrics\":[]}");
			answer.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
			return answer;
		});

		using HttpClient http = new(new XmppRoutingHandler(web, client.SendAsync));
		using TelemetryFetcher fetcher = new(http, (_, _) => { tokens++; return Task.FromResult<string?>("token"); }, new InMemoryFeatureAnswerStore(), () => now);
		TelemetryCollector c = new(Guid.NewGuid(), AuthClient, XmppRoute.Address) { NeuronJid = AuthClient };

		Fetched first = await fetcher.GetAsync(c, "/Catalog", TestContext.Current.CancellationToken);
		Assert.Equal("{\"metrics\":[]}", first.Body);
		Assert.Null(first.Problem);

		// An hour later: asked again with the ETag, and "nothing changed" keeps the answer.
		now = now.AddHours(1).AddMinutes(1);
		Fetched second = await fetcher.GetAsync(c, "/Catalog", TestContext.Current.CancellationToken);
		Assert.Equal("{\"metrics\":[]}", second.Body);
		Assert.Equal(1, fetcher.NotModified);

		Assert.Equal(2, client.Asked.Count);
		Assert.All(client.Asked, a =>
		{
			Assert.Equal(c.AccountId, a.Route.AccountId);
			Assert.Equal(AuthClient, a.Route.NeuronJid);
			Assert.Equal("/NeuroAppTelemetry/Api/v1/Catalog", a.Path);
			Assert.Null(a.Authorization);
		});
		Assert.Null(client.Asked[0].IfNoneMatch);
		Assert.Equal("\"v1\"", client.Asked[1].IfNoneMatch);

		// Nothing of it touched the web, and no token was asked for.
		Assert.Empty(web.Asked);
		Assert.Equal(0, tokens);
	}

	[Fact]
	public async Task ARequestForXmppWithoutANeuronNeverReachesTheWeb()
	{
		StandInWeb web = new(_ => Json(HttpStatusCode.OK));
		using HttpClient http = new(new XmppRoutingHandler(web, (_, _, _) => Task.FromResult(Json(HttpStatusCode.OK))));
		using HttpRequestMessage request = new(HttpMethod.Get, new Uri(XmppRoute.Address, "/NeuroAppTelemetry/Api/v1/Status"));

		await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(request, TestContext.Current.CancellationToken));
		Assert.Empty(web.Asked);
	}
}
