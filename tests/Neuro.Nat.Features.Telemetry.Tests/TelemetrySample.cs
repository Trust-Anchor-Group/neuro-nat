using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Neuro.Nat.App.Features;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Software;
using Neuro.Nat.Engine.Storage;
using Neuro.Nat.Features.Telemetry.Tests.StandIn;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Neuro.Nat.Features.Telemetry.Tests.TestAppBuilder))]

namespace Neuro.Nat.Features.Telemetry.Tests;

public static class TestAppBuilder
{
	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder.Configure<App.App>()
			.UseSkia()
			.WithInterFont()
			.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// NAT with app telemetry registered, its accounts on stand-in Neurons that answer the telemetry
/// API with made-up numbers. No live Neuron. The clock can be moved.
/// </summary>
public sealed class TelemetrySample : IAsyncDisposable
{
	public static readonly DateTime Start = new(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc);

	private DateTime now = Start;

	private TelemetrySample()
	{
	}

	public DateTime Now
	{
		get => this.now;
		set => this.now = value;
	}

	public AccountManager Accounts { get; private set; } = null!;
	public MonitorService Monitors { get; private set; } = null!;
	public FeatureSet Features { get; private set; } = null!;
	public TelemetryFeature Feature { get; private set; } = null!;
	public IFeatureAnswerStore Store { get; private set; } = null!;

	/// <summary>The stand-in API of each Neuron, by domain. A domain without one answers 404 (no package).</summary>
	public Dictionary<string, StandInTelemetry> Apis { get; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Every request, by host and path.</summary>
	public List<string> Requests { get; } = [];

	/// <summary>The account on each domain.</summary>
	public Dictionary<string, Guid> AccountOf { get; } = new(StringComparer.OrdinalIgnoreCase);

	private sealed class Web(TelemetrySample s) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			lock (s.Requests)
				s.Requests.Add(request.RequestUri!.Host + request.RequestUri.PathAndQuery);
			return Task.FromResult(s.Apis.TryGetValue(request.RequestUri.Host, out StandInTelemetry? api)
				? api.Answer(request)
				: new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("Not Found") });
		}
	}

	/// <summary>Accounts on these Neurons, each with or without the package, and NAT around them.</summary>
	public static async Task<TelemetrySample> CreateAsync(IEnumerable<(string Domain, StandInTelemetry? Api)> neurons, IFeatureAnswerStore? store = null)
	{
		EngineRuntime.Initialize();
		AccountManager accounts = new(new InMemoryAccountStore(), new InMemoryCredentialVault());
		MonitorService monitors = new(accounts, new EventHub());
		TelemetrySample s = new() { Accounts = accounts, Monitors = monitors };
		s.Store = store ?? new InMemoryFeatureAnswerStore();
		s.Feature = new TelemetryFeature(new Web(s), (_, _) => Task.FromResult<string?>("token"), (_, domain) => [new Uri("https://" + domain + "/")], () => s.Now, s.Store);
		s.Features = new FeatureSet([s.Feature]);

		foreach ((string domain, StandInTelemetry? api) in neurons)
		{
			// The same account each run, so what one run kept is found by the next.
			Guid id = new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(domain)));
			AccountSession ops = await accounts.AddAsync(new AccountProfile { Id = id, Host = domain, UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
			monitors.For(ops.Profile.Id)!.Preview(new WatchedNeuron { AccountId = ops.Profile.Id, BareJid = domain, Name = domain.StartsWith("lab.", StringComparison.Ordinal) ? "Lab" : null, State = WatchState.Live });
			s.AccountOf[domain] = ops.Profile.Id;
			if (api is not null)
				s.Apis[domain] = api;
		}

		return s;
	}

	/// <summary>NAT's main window model with the features, as the app makes it.</summary>
	public async Task<MainWindowViewModel> WindowModelAsync()
	{
		ChatService chat = new(this.Accounts, this.Monitors);
		SoftwareService software = new(this.Monitors, chat)
		{
			ReadStandIn = (account, jid) => new NeuronSoftware { AccountId = account, NeuronJid = jid, BuildTime = "2026-11-10 08:12", ReadAt = DateTime.Now },
		};
		MainWindowViewModel vm = new(this.Accounts, new FleetService(this.Monitors), chat, null, null, null, software, features: this.Features);
		await vm.StartAsync();
		vm.Fleet.Refresh();
		Dispatcher.UIThread.RunJobs();
		return vm;
	}

	/// <summary>Starts the features without a window (NAT's window does this itself).</summary>
	public void StartFeatures() => this.Features.Start(this.Accounts, this.Monitors);

	/// <summary>Each account signs in: its Neuron is asked whether it has the package, and a collector's overview is fetched.</summary>
	public async Task FindAsync()
	{
		foreach (Guid account in this.AccountOf.Values)
			await this.Feature.FindAsync(account);
		Dispatcher.UIThread.RunJobs();
	}

	public int QueryRequests(string? host = null)
	{
		lock (this.Requests)
			return this.Requests.Count(r => r.Contains("/Query", StringComparison.Ordinal) && (host is null || r.StartsWith(host + "/", StringComparison.OrdinalIgnoreCase)));
	}

	public ValueTask DisposeAsync() => this.Features.DisposeAsync();
}
