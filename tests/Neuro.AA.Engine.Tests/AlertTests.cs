using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Alerts;
using Neuro.AA.Engine.Attention;
using Neuro.AA.Engine.Events;
using Neuro.AA.Engine.Sniffing;

namespace Neuro.AA.Engine.Tests;

/// <summary>What the watch agent alerts for, and what it keeps quiet about.</summary>
public class AlertTests
{
	private static async Task<(MonitorService Monitors, Guid Account)> SetupAsync()
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "NeuroAA.Alerts." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		return (new MonitorService(manager, new EventHub()), s.Profile.Id);
	}

	private static void Event(MonitorService m, DateTime at, string text, SnifferDirection d = SnifferDirection.Error)
	{
		if (SniffedEventParser.Parse(new SnifferLine(0, at, d, text), new EventSource(Guid.Empty, "alpha@lab.local")) is NeuronEvent e)
			m.Hub.Add(e);
	}

	[Fact]
	public async Task AlertsNewErrorsOnceAndNotHistory()
	{
		var (m, _) = await SetupAsync();
		DateTime now = DateTime.Now;
		Event(m, now.AddHours(-3), "Old problem from the history\n\nModule: A");

		using AlertService alerts = new(m, new AlertSettings(), () => now, startTimer: false);
		List<Alert> got = [];
		alerts.Raised += (_, a) => got.Add(a);

		Event(m, now.AddHours(-2), "Another old problem, backfilled after start\n\nModule: B");
		Event(m, now, "Disk full on the backup volume\n\nModule: C");
		Event(m, now, "Disk full on the backup volume\n\nModule: C");
		Event(m, now, "Endpoint blocked\n\nModule: D", SnifferDirection.Warning);
		Event(m, now, "Old problem from the history\n\nModule: A");

		Alert a = Assert.Single(got);
		Assert.Equal(AlertKind.Error, a.Kind);
		Assert.Contains("Disk full", a.Title);
	}

	[Fact]
	public async Task AlertsNotificationsThatNeedSomeone()
	{
		var (m, account) = await SetupAsync();
		DateTime now = DateTime.Now;
		m.Attention.Preview(new NeuronNotification { Id = "old", AccountId = account, NeuronJid = "alpha@lab.local", Received = now.AddDays(-1), Markdown = "Account requested.\n\n| Name: | Kim |" });

		using AlertService alerts = new(m, new AlertSettings(), () => now, startTimer: false);
		List<Alert> got = [];
		alerts.Raised += (_, a) => got.Add(a);

		m.Attention.Preview(new NeuronNotification { Id = "n1", AccountId = account, NeuronJid = "alpha@lab.local", Received = now, Markdown = "Account requested.\n\n| Name: | Kim |" });
		m.Attention.Preview(new NeuronNotification { Id = "n2", AccountId = account, NeuronJid = "alpha@lab.local", Received = now, Markdown = "XMPP server at [`alpha`](http://alpha/) restarted at 2026-11-10, 12:00." });

		Alert a = Assert.Single(got);
		Assert.Equal("n1", a.Target);
		Assert.Contains("needs you", a.Detail);
	}

	[Fact]
	public async Task NeuronDownAfterAWhileAndBackAgain()
	{
		var (m, account) = await SetupAsync();
		DateTime now = DateTime.Now;
		NeuronMonitor monitor = m.For(account)!;
		monitor.Preview(new WatchedNeuron { AccountId = account, BareJid = "alpha@lab.local", Name = "Alpha", State = WatchState.Live });

		using AlertService alerts = new(m, new AlertSettings(), () => now, startTimer: false);
		List<Alert> got = [];
		alerts.Raised += (_, a) => got.Add(a);

		monitor.Preview(new WatchedNeuron { AccountId = account, BareJid = "alpha@lab.local", Name = "Alpha", State = WatchState.Offline });
		now = now.AddSeconds(30);
		alerts.CheckNeurons();
		Assert.Empty(got);   // A short reconnect is not news.

		now = now.AddSeconds(90);
		alerts.CheckNeurons();
		alerts.CheckNeurons();
		Alert down = Assert.Single(got);
		Assert.Equal(AlertKind.NeuronDown, down.Kind);
		Assert.Contains("Alpha", down.Title);

		monitor.Preview(new WatchedNeuron { AccountId = account, BareJid = "alpha@lab.local", Name = "Alpha", State = WatchState.Live });
		Assert.Equal(AlertKind.NeuronBack, got.Last().Kind);
		Assert.Equal(2, got.Count);
	}

	[Fact]
	public async Task SnoozeHoldsAlertsBack()
	{
		var (m, _) = await SetupAsync();
		DateTime now = DateTime.Now;
		using AlertService alerts = new(m, new AlertSettings(), () => now, startTimer: false);
		List<Alert> got = [];
		alerts.Raised += (_, a) => got.Add(a);

		alerts.Snooze(TimeSpan.FromHours(1));
		Event(m, now, "Certificate expired\n\nModule: E");
		Assert.Empty(got);
		Assert.Single(alerts.Recent);

		now = now.AddHours(2);
		Assert.False(alerts.IsSnoozed);
		Event(m, now, "Database corrupt\n\nModule: F");
		Assert.Single(got);
	}
}
