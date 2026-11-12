using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Alerts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Sniffing;

namespace Neuro.Nat.Engine.Tests;

/// <summary>What the watch agent alerts for, and what it keeps quiet about.</summary>
public class AlertTests
{
	private static async Task<(MonitorService Monitors, Guid Account)> SetupAsync()
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Alerts." + Guid.NewGuid().ToString("N"));
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
		Assert.Contains("waiting for you", a.Detail);
	}

	[Fact]
	public async Task NeuronDownAfterAWhileAndBackAgain()
	{
		var (m, account) = await SetupAsync();
		DateTime now = DateTime.Now;
		NeuronMonitor monitor = m.For(account)!;
		monitor.Preview(new WatchedNeuron { AccountId = account, BareJid = "alpha@lab.local", Name = "Alpha", State = WatchState.Live });

		using AlertService alerts = new(m, new AlertSettings(), () => now, startTimer: false) { AccountConnected = _ => true };
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
	public async Task NoNeuronIsDownWhileItsAccountIsNotConnected()
	{
		var (m, account) = await SetupAsync();
		DateTime now = DateTime.Now;
		NeuronMonitor monitor = m.For(account)!;
		monitor.Preview(new WatchedNeuron { AccountId = account, BareJid = "alpha@lab.local", Name = "Alpha", State = WatchState.Live });

		using AlertService alerts = new(m, new AlertSettings(), () => now, startTimer: false) { AccountConnected = _ => false };
		List<Alert> got = [];
		alerts.Raised += (_, a) => got.Add(a);

		// The account lost its connection, so every Neuron through it looks offline: that is not news about the Neurons.
		monitor.Preview(new WatchedNeuron { AccountId = account, BareJid = "alpha@lab.local", Name = "Alpha", State = WatchState.Offline });
		now = now.AddMinutes(10);
		alerts.CheckNeurons();
		alerts.CheckNeurons();
		Assert.Empty(got);
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

	[Fact]
	public async Task ContactRequestsAlertAndSoundsFollowSettings()
	{
		var (m, account) = await SetupAsync();
		DateTime now = DateTime.Now;
		using AlertService alerts = new(m, new AlertSettings(), () => now, startTimer: false) { AccountLabel = _ => "ops@lab.local" };
		List<Alert> got = [];
		alerts.Raised += (_, a) => got.Add(a);

		alerts.ContactRequested(account, "kim@example.com");
		Alert a = Assert.Single(got);
		Assert.Equal(AlertKind.ContactRequest, a.Kind);
		Assert.Contains("kim@example.com", a.Title);
		Assert.Equal("Asks ops@lab.local", a.Detail);

		AlertSettings s = new();
		Assert.True(s.WantsSound(a));
		Assert.True(s.WantsSound(new Alert { Id = "e", Kind = AlertKind.Error, Title = "x", Severity = EventSeverity.Error }));
		Assert.False(s.WantsSound(new Alert { Id = "b", Kind = AlertKind.NeuronBack, Title = "x" }));
		Assert.False((s with { SoundForErrors = false }).WantsSound(new Alert { Id = "d", Kind = AlertKind.NeuronDown, Title = "x" }));

		alerts.Settings = s with { ContactRequests = false };
		alerts.ContactRequested(account, "other@example.com");
		Assert.Single(got);
	}

	[Fact]
	public async Task MessagesAlertUnlessMutedAndRoomsOnlyWhenTheyMay()
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Alerts." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		Guid account = s.Profile.Id;
		MonitorService m = new(manager, new EventHub());
		Chat.ChatService chat = new(manager, m);

		DateTime now = DateTime.Now;
		using AlertService alerts = new(m, new AlertSettings(), () => now, startTimer: false);
		alerts.Watch(chat);
		List<Alert> got = [];
		alerts.Raised += (_, a) => got.Add(a);

		// A person: always, one group per conversation.
		chat.Preview(account, "kim@lab.local", "Hello", "Kim");
		chat.Preview(account, "kim@lab.local", "Are you there?", "Kim");
		Assert.Equal(2, got.Count);
		Assert.All(got, a => Assert.Equal(AlertKind.Message, a.Kind));
		Assert.Equal(got[0].Group, got[1].Group);
		Assert.Equal("Kim", got[0].Title);
		Assert.Equal("kim@lab.local", got[0].Target);
		Assert.Equal(2, chat.UnreadTotal);

		// Muted: nothing pops up, and it stays out of the count. Unmuted: it is back.
		Chat.Conversation kim = chat.Get(account, "kim@lab.local");
		chat.SetMuted(kim, now.AddHours(1));
		chat.Preview(account, "kim@lab.local", "Hello?", "Kim");
		Assert.Equal(2, got.Count);
		Assert.Equal(0, chat.UnreadTotal);
		chat.SetMuted(kim, null);
		Assert.Equal(3, chat.UnreadTotal);

		// A room: only what mentions you, until the room is set to tell you everything.
		chat.Preview(account, "ops@muc.lab.local", "Lunch?", "sam", room: true);
		Assert.Equal(2, got.Count);
		chat.Preview(account, "ops@muc.lab.local", "ops: can you look at gamma?", "sam", room: true, mention: true);
		Assert.Equal(3, got.Count);
		Assert.True(got[2].IsRoom);
		Assert.Equal("sam in ops", got[2].Title);

		Chat.Conversation room = chat.GetRoom(account, "ops@muc.lab.local");
		chat.SetRoomNotify(room, Chat.RoomNotify.All);
		chat.Preview(account, "ops@muc.lab.local", "Anyone?", "sam", room: true);
		Assert.Equal(4, got.Count);
		chat.SetRoomNotify(room, Chat.RoomNotify.Muted);
		chat.Preview(account, "ops@muc.lab.local", "ops: hello?", "sam", room: true, mention: true);
		Assert.Equal(4, got.Count);

		// Do not disturb holds everything back; the test button still shows.
		alerts.Settings = alerts.Settings with { DoNotDisturb = true };
		chat.Preview(account, "kim@lab.local", "Last one", "Kim");
		Assert.Equal(4, got.Count);
		alerts.Test(AlertKind.Message);
		Assert.Equal(5, got.Count);
	}

	[Fact]
	public void MentionsAreWholeWords()
	{
		Assert.True(Chat.ChatService.Mentions("hey Mille, can you look?", "mille"));
		Assert.True(Chat.ChatService.Mentions("@mille: ping", "mille"));
		Assert.True(Chat.ChatService.Mentions("mille", "Mille"));
		Assert.False(Chat.ChatService.Mentions("a millennium ago", "mille"));
		Assert.False(Chat.ChatService.Mentions("nothing here", "mille"));
		Assert.False(Chat.ChatService.Mentions("anything", ""));
	}

	[Fact]
	public async Task MarkingUnreadRemembersWhereReadingStopped()
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Alerts." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		Chat.ChatService chat = new(manager, new MonitorService(manager, new EventHub()));

		Chat.ChatMessage one = chat.Preview(s.Profile.Id, "kim@lab.local", "One", "Kim");
		Chat.ChatMessage two = chat.Preview(s.Profile.Id, "kim@lab.local", "Two", "Kim");
		Chat.ChatMessage three = chat.Preview(s.Profile.Id, "kim@lab.local", "Three", "Kim");
		Chat.Conversation c = chat.Get(s.Profile.Id, "kim@lab.local");
		Assert.Equal(3, c.Unread);
		Assert.Equal(one.Id, c.FirstUnreadId);

		chat.MarkRead(c);
		Assert.Equal(0, c.Unread);
		Assert.Null(c.FirstUnreadId);

		chat.MarkUnreadFrom(c, two.Id);
		Assert.Equal(2, c.Unread);
		Assert.Equal(two.Id, c.FirstUnreadId);
		Assert.NotEqual(three.Id, c.FirstUnreadId);
	}
}
