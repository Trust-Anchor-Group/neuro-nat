using System.Net;
using System.Text;
using Neuro.Nat.App.Features;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Storage;

namespace Neuro.Nat.Features.Telemetry.Tests;

/// <summary>
/// Finding app telemetry on the Neurons, with a stand-in for the web API and for the token the
/// Neuron gives over XMPP. No live Neuron.
/// </summary>
public class TelemetryFeatureTests
{
	private const string Lab = "lab.tagroot.io";
	private static readonly DateTime Now = new(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc);

	// The /Status answers as the package gives them (Neuro.App.Telemetry, Web/ApiResource.cs GetStatus).
	private const string SelfOnly = """
		{"neurons":[{"neuron":"lab.tagroot.io","role":"self","metricsVersion":5,"lastComputedDay":"2026-11-11","lastCompute":"2026-11-12T02:10:00.0000000Z",
		"lastComputeError":null,"computing":false,"lastCollect":null,"lastCollectSuccess":null,"lastCollectError":null,"lastCollectFacts":null}]}
		""";

	private const string Collector = """
		{"neurons":[
		{"neuron":"root.tagroot.io","role":"self","metricsVersion":5,"lastComputedDay":"2026-11-11","lastCompute":"2026-11-12T02:10:00.0000000Z","lastComputeError":null,"computing":false,"lastCollect":null,"lastCollectSuccess":null,"lastCollectError":null,"lastCollectFacts":null},
		{"neuron":"a.tagroot.io","role":"source","metricsVersion":5,"lastComputedDay":"2026-11-11","lastCompute":null,"lastComputeError":null,"computing":null,"lastCollect":"2026-11-12T02:00:00Z","lastCollectSuccess":"2026-11-12T02:00:00Z","lastCollectError":null,"lastCollectFacts":120},
		{"neuron":"b.tagroot.io","role":"source","metricsVersion":5,"lastComputedDay":"2026-11-11","lastCompute":null,"lastComputeError":null,"computing":null,"lastCollect":null,"lastCollectSuccess":null,"lastCollectError":null,"lastCollectFacts":0},
		{"neuron":"c.tagroot.io","role":"source","metricsVersion":null,"lastComputedDay":null,"lastCompute":null,"lastComputeError":null,"computing":null,"lastCollect":null,"lastCollectSuccess":null,"lastCollectError":null,"lastCollectFacts":0},
		{"neuron":"d.tagroot.io","role":"source","metricsVersion":4,"lastComputedDay":"2026-11-10","lastCompute":null,"lastComputeError":null,"computing":null,"lastCollect":null,"lastCollectSuccess":null,"lastCollectError":null,"lastCollectFacts":3}]}
		""";

	private const string NotAllowed = """{"error":"notAllowed","message":"The account needs the privilege Custom.Telemetry.External or Custom.Telemetry.Internal."}""";

	/// <summary>Answers in place of the Neuron's web server, and remembers what was asked.</summary>
	private sealed class StandInWeb(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
	{
		public List<(Uri Uri, string? Authorization)> Asked { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			lock (this.Asked)
				this.Asked.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
			return Task.FromResult(answer(request));
		}

		public static StandInWeb Says(HttpStatusCode code, string body = "")
			=> new(_ => new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
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

	private sealed record Sample(TelemetryFeature Feature, StandInHost Host, StandInWeb Web, Guid Account, List<string> Tokens) : IAsyncDisposable
	{
		public async Task SignInAsync()
		{
			this.Host.SignIn(this.Account);
			await this.Feature.SettledAsync();
		}

		public IReadOnlyList<string> Lines(string jid = Lab) => this.Feature.SoftwareLines(this.Account, jid);

		public ValueTask DisposeAsync() => this.Feature.DisposeAsync();
	}

	private static async Task<Sample> SampleAsync(StandInWeb web, bool hasAddress = true, Func<string>? token = null)
	{
		EngineRuntime.Initialize();
		AccountManager accounts = new(new InMemoryAccountStore(), new InMemoryCredentialVault());
		AccountSession ops = await accounts.AddAsync(new AccountProfile { Host = Lab, UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		StandInHost host = new(accounts, new MonitorService(accounts, new EventHub()));

		List<string> tokens = [];
		int n = 0;
		TelemetryFeature feature = new(web,
			(_, _) =>
			{
				string t = token?.Invoke() ?? "token-" + Interlocked.Increment(ref n);
				lock (tokens)
					tokens.Add(t);
				return Task.FromResult<string?>(t);
			},
			(_, domain) => hasAddress ? [new Uri("https://" + domain + "/")] : [],
			() => Now)
		{
			FetchesNumbers = false,
		};
		feature.Start(host);
		return new Sample(feature, host, web, ops.Profile.Id, tokens);
	}

	[Fact]
	public async Task ANeuronWithOnlyItsOwnNumbersSaysInstalled()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.OK, SelfOnly));
		await s.SignInAsync();

		Assert.Equal(FeaturePresence.Present, s.Feature.PresenceOn(s.Account, Lab));
		Assert.Equal(["App telemetry: installed, numbers up to yesterday"], s.Lines());

		(Uri uri, string? authorization) = Assert.Single(s.Web.Asked);
		Assert.Equal("https://lab.tagroot.io/NeuroAppTelemetry/Api/v1/Status", uri.ToString());
		Assert.Equal("Bearer token-1", authorization);
	}

	[Fact]
	public async Task ACollectorSaysHowManyNeuronsItCollectsFrom()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.OK, Collector));
		await s.SignInAsync();

		Assert.Equal(["App telemetry: installed, numbers up to yesterday, collects from 4 Neurons"], s.Lines());
		TelemetryStatus status = s.Feature.StatusOf(s.Account, Lab)!;
		Assert.True(status.IsCollector);
		Assert.Equal("root.tagroot.io", status.Neuron);
	}

	[Fact]
	public async Task OlderNumbersAndNoNumbersAreSaidAsTheyAre()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.OK, SelfOnly.Replace("2026-11-11", "2026-11-03", StringComparison.Ordinal)));
		await s.SignInAsync();
		Assert.Equal(["App telemetry: installed, numbers up to 3 November"], s.Lines());

		await using Sample none = await SampleAsync(StandInWeb.Says(HttpStatusCode.OK, SelfOnly.Replace("\"2026-11-11\"", "null", StringComparison.Ordinal)));
		await none.SignInAsync();
		Assert.Equal(["App telemetry: installed, no numbers yet"], none.Lines());
	}

	[Fact]
	public async Task NotAllowedNamesThePrivilege()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.Forbidden, NotAllowed));
		await s.SignInAsync();

		Assert.Equal(FeaturePresence.NotAllowed, s.Feature.PresenceOn(s.Account, Lab));
		Assert.Equal(["App telemetry: your account needs Custom.Telemetry.Internal"], s.Lines());
	}

	[Fact]
	public async Task NotInstalledShowsNothing()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.NotFound, "Not Found"));
		await s.SignInAsync();

		Assert.Equal(FeaturePresence.Absent, s.Feature.PresenceOn(s.Account, Lab));
		Assert.Empty(s.Lines());
		INatFeature f = s.Feature;
		Assert.Empty(f.NeuronTabs(s.Account, Lab));
		Assert.Empty(f.Places());
		Assert.Empty(f.InboxItems());
		Assert.Empty(f.SearchHits());
		Assert.Empty(f.BigScreenPages());
	}

	[Fact]
	public async Task SomethingElseAnsweringIsNotTelemetry()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.OK, "<html>Welcome</html>"));
		await s.SignInAsync();

		Assert.Equal(FeaturePresence.Absent, s.Feature.PresenceOn(s.Account, Lab));
		Assert.Empty(s.Lines());
	}

	[Fact]
	public async Task NoWebAddressShowsNothingAndAsksNothing()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.OK, SelfOnly), hasAddress: false);
		await s.SignInAsync();

		Assert.Equal(FeaturePresence.Absent, s.Feature.PresenceOn(s.Account, Lab));
		Assert.Empty(s.Lines());
		Assert.Empty(s.Web.Asked);
		Assert.Empty(s.Tokens);
	}

	[Fact]
	public async Task NotSignedInShowsNothing()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.Unauthorized));
		await s.SignInAsync();

		Assert.Equal(FeaturePresence.Unknown, s.Feature.PresenceOn(s.Account, Lab));
		Assert.Empty(s.Lines());
	}

	[Fact]
	public async Task ANetworkFailureShowsNothing()
	{
		await using Sample s = await SampleAsync(new StandInWeb(_ => throw new HttpRequestException("No route", null, HttpStatusCode.ServiceUnavailable)));
		await s.SignInAsync();

		Assert.Equal(FeaturePresence.Unknown, s.Feature.PresenceOn(s.Account, Lab));
		Assert.Empty(s.Lines());
	}

	[Fact]
	public async Task NoTokenAsksNothing()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.OK, SelfOnly), token: () => string.Empty);
		await s.SignInAsync();

		Assert.Empty(s.Web.Asked);
		Assert.Empty(s.Lines());
	}

	[Fact]
	public async Task AFailedRoundKeepsWhatWasKnown()
	{
		bool down = false;
		await using Sample s = await SampleAsync(new StandInWeb(_ => down
			? throw new HttpRequestException("Down")
			: new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SelfOnly) }));
		await s.SignInAsync();
		down = true;
		await s.SignInAsync();

		Assert.Equal(2, s.Web.Asked.Count);
		Assert.Equal(["App telemetry: installed, numbers up to yesterday"], s.Lines());
	}

	[Fact]
	public async Task AskedOncePerSignInAndAgainWhenTheSoftwareChanges()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.OK, SelfOnly));
		int changes = 0;
		s.Feature.Changed += (_, _) => Interlocked.Increment(ref changes);

		Assert.Empty(s.Web.Asked);
		Assert.Empty(s.Lines());

		await s.SignInAsync();
		Assert.Single(s.Web.Asked);
		Assert.Equal(1, changes);

		// Nothing else asks: reading the lines, or another Neuron's software changing.
		_ = s.Lines();
		_ = s.Feature.PresenceOn(s.Account, Lab);
		s.Host.Software(s.Account, "edge.example.org");
		await s.Feature.SettledAsync();
		Assert.Single(s.Web.Asked);

		// The account's own Neuron got new software: asked again, with a fresh token.
		s.Host.Software(s.Account, Lab);
		await s.Feature.SettledAsync();
		Assert.Equal(2, s.Web.Asked.Count);

		// Signed in again (a new connection): asked again.
		await s.SignInAsync();
		Assert.Equal(3, s.Web.Asked.Count);
		Assert.Equal(["token-1", "token-2", "token-3"], s.Tokens);
		Assert.Equal(["Bearer token-1", "Bearer token-2", "Bearer token-3"], s.Web.Asked.Select(a => a.Authorization));

		// The same answer each time: what NAT shows did not change after the first.
		Assert.Equal(1, changes);
	}

	[Fact]
	public async Task OnlyTheAccountsOwnNeuronIsAnswered()
	{
		await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.OK, SelfOnly));
		await s.SignInAsync();

		Assert.NotEmpty(s.Lines(Lab));
		Assert.Empty(s.Lines("edge.example.org"));
		Assert.Equal(FeaturePresence.Unknown, s.Feature.PresenceOn(s.Account, "edge.example.org"));
		Assert.Empty(s.Feature.SoftwareLines(Guid.NewGuid(), Lab));
	}

	[Fact]
	public async Task TheWordsFollowTheRules()
	{
		foreach (string body in new[] { SelfOnly, Collector })
		{
			await using Sample s = await SampleAsync(StandInWeb.Says(HttpStatusCode.OK, body));
			await s.SignInAsync();
			foreach (string line in s.Lines())
				Assert.DoesNotContain(line, c => c is '·' or '—');
		}
	}

	[Fact]
	public void WebAddressesComeFromTheNeuronsLinksThenItsDomain()
	{
		Guid account = Guid.NewGuid();
		Engine.Attention.AttentionInbox inbox = new();
		inbox.Preview(new Engine.Attention.NeuronNotification
		{
			Id = "n1",
			AccountId = account,
			NeuronJid = Lab,
			Received = DateTime.Now,
			// The Neuron's notifications carry links to its admin pages.
			Markdown = "New software\n\n[Admin](https://web.tagroot.io:8443/Settings.md) [Plain](http://web.tagroot.io/x) [Local](http://localhost:8081/x)",
		});

		// Plain http only on this computer, so a token never crosses the network unencrypted.
		Assert.Equal(["https://web.tagroot.io:8443/", "http://localhost:8081/", "https://lab.tagroot.io/"], NeuronWeb.Candidates(null, inbox, account, Lab).Select(u => u.ToString()));
		Assert.Equal(["https://localhost/"], NeuronWeb.Candidates(null, inbox, account, "localhost").Select(u => u.ToString()));
		Assert.Empty(NeuronWeb.Candidates(null, null, account, string.Empty));
	}

	[Fact]
	public void TheAccountsOwnWebAddressComesFirst()
	{
		Guid account = Guid.NewGuid();
		Engine.Attention.AttentionInbox inbox = new();
		inbox.Preview(new Engine.Attention.NeuronNotification
		{
			Id = "n1",
			AccountId = account,
			NeuronJid = Lab,
			Received = DateTime.Now,
			Markdown = "New software\n\n[Admin](https://web.tagroot.io:8443/Settings.md)",
		});

		// Set on the account: first. Then the Neuron's own links, then https on its domain.
		Assert.Equal(["http://localhost:8081/", "https://web.tagroot.io:8443/", "https://lab.tagroot.io/"],
			NeuronWeb.Candidates("http://localhost:8081", inbox, account, Lab).Select(u => u.ToString()));
		Assert.Equal(["https://web.tagroot.io:8443/", "https://lab.tagroot.io/"], NeuronWeb.Candidates(null, inbox, account, Lab).Select(u => u.ToString()));
		Assert.Equal(["https://lab.tagroot.io/"], NeuronWeb.Candidates(" ", null, account, Lab).Select(u => u.ToString()));

		// Plain http to another computer is refused, set or not; a bare host gets https.
		Assert.Equal(["https://lab.tagroot.io/"], NeuronWeb.Candidates("http://web.tagroot.io", null, account, Lab).Select(u => u.ToString()));
		Assert.Null(Engine.Accounts.AccountWeb.Check("http://web.tagroot.io"));
		Assert.Null(Engine.Accounts.AccountWeb.Check("ftp://web.tagroot.io"));
		Assert.Equal("https://web.tagroot.io/", Engine.Accounts.AccountWeb.Check("web.tagroot.io"));
		Assert.Equal("http://127.0.0.1:8081/", Engine.Accounts.AccountWeb.Check("http://127.0.0.1:8081/x"));
	}
}
