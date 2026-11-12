using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Alerts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Devices;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// Noticing things: pop-ups, the one Inbox list, the "New" line, muting, your status and the
/// Settings screen. Renders frames for review and checks the behaviour behind them.
/// </summary>
public class NoticeTests
{
	private sealed record Sample(MainWindowViewModel Vm, ChatService Chat, DevicesService Devices, MonitorService Monitors, AlertService Alerts, Guid Ops, Guid Admin) : IDisposable
	{
		public List<Alert> Raised { get; } = [];

		public void Dispose() => this.Alerts.Dispose();
	}

	private static string ReviewFolder()
	{
		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;

		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		return folder;
	}

	private static void Capture(MainWindow window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		frame!.Save(Path.Combine(ReviewFolder(), name + ".png"));
	}

	private static async Task<Sample> SampleAsync()
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Notice." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		AccountSession admin = await manager.AddAsync(new AccountProfile { Host = "prod.example", UserName = "admin", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);

		MonitorService monitors = new(manager, new EventHub());
		ChatService chat = new(manager, monitors);
		DevicesService devices = new(manager);
		MainWindowViewModel vm = new(manager, new FleetService(monitors), chat, null, devices);
		await vm.StartAsync();

		AlertService alerts = new(monitors, new AlertSettings(), startTimer: false);
		alerts.Watch(chat);
		alerts.Watch(devices);
		vm.Settings.Attach(alerts, new JsonAlertSettingsStore(Path.Combine(dir, "alerts.json")));

		Sample s = new(vm, chat, devices, monitors, alerts, ops.Profile.Id, admin.Profile.Id);
		alerts.Raised += (_, a) => s.Raised.Add(a);
		return s;
	}

	/// <summary>What the tray agent does with an alert, without the tray.</summary>
	private static void Show(Sample s, Alert a)
	{
		if (s.Vm.IsLookingAt(a) || s.Vm.ShowsItself(a))
			return;

		NoticeViewModel n = s.Vm.Notice(a, out bool isNew);
		if (isNew)
			s.Vm.ShowInApp(n);
	}

	private static void AddNotifications(Sample s)
	{
		DateTime now = DateTime.Now;
		s.Monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n1", AccountId = s.Admin, NeuronJid = "beta@prod.example", Received = now.AddMinutes(-3),
			Markdown = "Legal identity application received: [`2f9a1c04-57e1-4c1e@legal.prod.example`](https://prod.example/ValidateLegalId.md?ID=2f9a1c04-57e1-4c1e%40legal.prod.example)\n",
		});
		s.Monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n2", AccountId = s.Ops, NeuronJid = "alpha@lab.local", Received = now.AddMinutes(-34),
			Markdown = "Account requested.\n\n| Account request ||\n|:------|:-------|\n| Name: | Kim Lindqvist |\n| Company: | Example Logistics AB |\n",
		});
		s.Monitors.Attention.Preview(new NeuronNotification
		{
			Id = "n3", AccountId = s.Admin, NeuronJid = "gamma@prod.example", Received = now.AddMinutes(-300), State = AttentionState.Read,
			Markdown = "XMPP server at [`gamma.prod.example`](http://gamma.prod.example/) updated and restarted at 2026-11-09, 16:40:12.",
		});
	}

	private static void AddPeople(Sample s)
	{
		s.Chat.Preview(s.Ops, "kim@lab.local", "Morning! Did the backup on gamma finish?", "Kim Lindqvist");
		s.Chat.Preview(s.Ops, "kim@lab.local", "I see the 02:00 one failed and nothing after it.", "Kim Lindqvist");
		s.Chat.Preview(s.Admin, "ops-team@muc.prod.example", "@admin can you approve the identity for the warehouse gateway?", "sam", room: true, mention: true);
		s.Chat.Preview(new SubscriptionRequest(s.Ops, "sensor-hub-7@lab.local", DateTime.Now.AddMinutes(-9)) { AccountLabel = "ops@lab.local" });
		s.Chat.Preview(new RoomInvitation(s.Admin, "incident-4471@muc.prod.example", "robin@prod.example", "We are looking at the ledger repair. Join us.", DateTime.Now.AddMinutes(-2)));
		s.Devices.Preview(new DeviceQuestion
		{
			Id = "q1", AccountId = s.Ops, Kind = DeviceQuestionKind.Readout, DeviceJid = "thermo-12@lab.local", RemoteJid = "dashboard@partner.example",
			Names = ["Temperature", "Humidity"], Received = DateTime.Now.AddMinutes(-15),
		});
	}

	[AvaloniaTheory]
	[InlineData("Light", 1440, 900)]
	[InlineData("Dark", 1440, 900)]
	[InlineData("Light", 900, 700)]
	public async Task RendersTheInbox(string theme, int width, int height)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		using Sample s = await SampleAsync();
		AddNotifications(s);
		AddPeople(s);

		s.Vm.ShowAttentionCommand.Execute(null);
		s.Vm.Attention.Refresh();
		s.Vm.Inbox.Refresh();

		MainWindow window = new() { DataContext = s.Vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(200);
		string tag = $"{theme.ToLowerInvariant()}-{width}";
		Capture(window, "inbox-" + tag);

		// Two unread conversations, three requests and two notifications that need someone.
		Assert.Equal(2, s.Vm.Inbox.MessageCount);
		Assert.Equal(3, s.Vm.Inbox.RequestCount);
		Assert.Equal(2, s.Vm.Inbox.NeuronCount);
		Assert.Equal(7, s.Vm.Inbox.WaitingCount);
		Assert.Equal(7, s.Vm.Inbox.Rows.Count);
		Assert.Equal("(7) NAT", s.Vm.WindowTitle);

		// A message row opens its conversation beside the list, and stays while it is open.
		s.Vm.Inbox.SelectedRow = s.Vm.Inbox.Rows.First(r => r.IsMessage && !r.IsRoom);
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(100);
		Capture(window, "inbox-message-" + tag);
		Assert.True(s.Vm.Inbox.ShowsConversation);
		Assert.Equal(2, s.Vm.Chat.Messages.Count);
		Assert.True(s.Vm.Chat.Messages[0].IsFirstNew);
		s.Vm.Inbox.Refresh();
		Assert.Equal(1, s.Vm.Inbox.MessageCount);
		Assert.Contains(s.Vm.Inbox.Rows, r => r.IsMessage && !r.IsRoom);

		s.Vm.Inbox.SelectedRow = s.Vm.Inbox.Rows.First(r => r.Kind == InboxRowKind.DeviceQuestion);
		Dispatcher.UIThread.RunJobs();
		Capture(window, "inbox-request-" + tag);
		Assert.True(s.Vm.Inbox.ShowsRequest);

		s.Vm.Inbox.SelectedRow = s.Vm.Inbox.Rows.First(r => r.IsNotification);
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(100);
		Capture(window, "inbox-notification-" + tag);
		Assert.True(s.Vm.Inbox.ShowsNotification);

		s.Vm.Inbox.ShowDone = true;
		s.Vm.Attention.Refresh();
		s.Vm.Inbox.Refresh();
		Capture(window, "inbox-done-" + tag);
		Assert.Contains(s.Vm.Inbox.Rows, r => r.IsDone);
		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task RendersPopUpsInTheWindow(string theme)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		using Sample s = await SampleAsync();
		s.Vm.ShowFleetCommand.Execute(null);

		MainWindow window = new() { DataContext = s.Vm, Width = 1280, Height = 800 };
		window.Show();

		DateTime now = DateTime.Now;
		Show(s, new Alert { Id = "i:1", Kind = AlertKind.Error, Severity = EventSeverity.Error, Title = "New: Unable to upload backup to backup@vault.prod.example", Detail = "gamma@prod.example", At = now });
		AddPeople(s);
		s.Alerts.ContactRequested(s.Ops, "sensor-hub-7@lab.local");
		foreach (Alert a in s.Raised.ToList())
			Show(s, a);

		Dispatcher.UIThread.RunJobs();
		await Task.Delay(150);
		Capture(window, "popups-" + theme.ToLowerInvariant());

		// At most three show; the two messages from Kim are one pop-up.
		Assert.Equal(3, s.Vm.Notices.Count);
		Assert.Equal(1, s.Raised.Count(a => a.Kind == AlertKind.Message && !a.IsRoom) - 1);
		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light", 1280)]
	[InlineData("Dark", 1280)]
	[InlineData("Light", 760)]
	public async Task RendersSettings(string theme, int width)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		using Sample s = await SampleAsync();
		s.Vm.ShowSettingsCommand.Execute(null);

		MainWindow window = new() { DataContext = s.Vm, Width = width, Height = 900 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"settings-{theme.ToLowerInvariant()}-{width}");

		Assert.True(s.Vm.IsSettings);
		Assert.Equal(PlaceKind.Settings, s.Vm.Place);
		window.Close();
	}

	[AvaloniaFact]
	public async Task MessagesFromOneConversationShareAPopUp()
	{
		using Sample s = await SampleAsync();
		s.Chat.Preview(s.Ops, "kim@lab.local", "One", "Kim");
		s.Chat.Preview(s.Ops, "kim@lab.local", "Two", "Kim");
		s.Chat.Preview(s.Ops, "robin@lab.local", "Hello", "Robin");
		foreach (Alert a in s.Raised)
			Show(s, a);

		Assert.Equal(2, s.Vm.Notices.Count);
		NoticeViewModel kim = s.Vm.Notices[0];
		Assert.Equal(1, kim.More);
		Assert.Equal("Two", kim.Detail);
		Assert.True(kim.CanReply);
		Assert.True(kim.CanMute);
		Assert.Contains("2 new", kim.Caption);

		// Reading the conversation anywhere closes its pop-up.
		s.Vm.ShowChatCommand.Execute(null);
		Assert.True(s.Vm.Chat.Open(s.Ops, "kim@lab.local"));
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(1300);
		Dispatcher.UIThread.RunJobs();
		Assert.True(kim.IsClosed);
		Assert.Single(s.Vm.Notices);

		// Muting from the pop-up: nothing more pops up from Robin, and the count lets go.
		s.Vm.Notices[0].MuteForGoodCommand.Execute(null);
		Assert.Empty(s.Vm.Notices);
		int before = s.Raised.Count;
		s.Chat.Preview(s.Ops, "robin@lab.local", "Still there?", "Robin");
		Assert.Equal(before, s.Raised.Count);
		Assert.Equal(0, s.Chat.UnreadTotal);
	}

	[AvaloniaFact]
	public async Task AnOpenConversationIsOnlyReadWhileYouCanSeeIt()
	{
		using Sample s = await SampleAsync();
		s.Chat.Preview(s.Ops, "kim@lab.local", "First", "Kim");
		s.Vm.ShowChatCommand.Execute(null);
		Assert.True(s.Vm.Chat.Open(s.Ops, "kim@lab.local"));
		Conversation conv = s.Chat.Get(s.Ops, "kim@lab.local");
		Assert.Equal(0, conv.Unread);

		// In front of you: the message appears, and nothing pops up.
		ChatMessage seen = s.Chat.Preview(s.Ops, "kim@lab.local", "Second", "Kim");
		Assert.True(s.Vm.IsLookingAt(s.Raised[^1]));

		// The window goes behind another: what arrives now waits for you.
		s.Vm.IsWindowActive = false;
		ChatMessage missed = s.Chat.Preview(s.Ops, "kim@lab.local", "Third", "Kim");
		Assert.False(s.Vm.IsLookingAt(s.Raised[^1]));
		await Task.Delay(700);
		Dispatcher.UIThread.RunJobs();
		Assert.True(conv.Unread > 0);

		// Back in front: it counts as read, with the "New" line where you stopped.
		s.Vm.IsWindowActive = true;
		Assert.Equal(0, conv.Unread);
		MessageViewModel line = Assert.Single(s.Vm.Chat.Messages, m => m.IsFirstNew);
		Assert.NotEqual(missed.Id, seen.Id);
		Assert.True(line.Id == missed.Id || line.Id == seen.Id);

		// Mark as unread keeps it waiting until you come back to it.
		s.Vm.Chat.MarkUnreadCommand.Execute(s.Vm.Chat.Messages[^1]);
		Assert.Equal(1, conv.Unread);
		Assert.True(s.Vm.Chat.Messages[^1].IsFirstNew);
	}

	[AvaloniaFact]
	public async Task InboxAnswersCanBeTakenBack()
	{
		using Sample s = await SampleAsync();
		AddNotifications(s);
		AddPeople(s);
		s.Vm.Attention.Refresh();
		InboxViewModel inbox = s.Vm.Inbox;
		inbox.Refresh();
		Assert.Equal(7, inbox.WaitingCount);

		// Done on a notification: it leaves, and Undo brings it back.
		InboxRowViewModel note = inbox.Rows.First(r => r.IsNotification);
		await inbox.PrimaryCommand.ExecuteAsync(note);
		Assert.DoesNotContain(inbox.Rows, r => r.Key == note.Key);
		Assert.Equal(6, inbox.WaitingCount);
		Assert.True(inbox.HasUndo);
		inbox.UndoCommand.Execute(null);
		Assert.Contains(inbox.Rows, r => r.Key == note.Key);
		Assert.Equal(7, inbox.WaitingCount);

		// Accept on a request: the row leaves at once, the answer is sent when Undo's time is up.
		InboxRowViewModel request = inbox.Rows.First(r => r.Kind == InboxRowKind.ContactRequest);
		await inbox.PrimaryCommand.ExecuteAsync(request);
		Assert.DoesNotContain(inbox.Rows, r => r.Key == request.Key);
		Assert.Single(s.Chat.Requests);
		inbox.UndoCommand.Execute(null);
		Assert.Contains(inbox.Rows, r => r.Key == request.Key);

		await inbox.SecondaryCommand.ExecuteAsync(inbox.Rows.First(r => r.Kind == InboxRowKind.ContactRequest));
		await inbox.CommitAsync();
		inbox.Refresh();
		Assert.Empty(s.Chat.Requests);
		Assert.Equal(6, inbox.WaitingCount);

		// Mark read on a conversation, and take that back too.
		InboxRowViewModel message = inbox.Rows.First(r => r.IsMessage && !r.IsRoom);
		await inbox.PrimaryCommand.ExecuteAsync(message);
		Assert.Equal(0, s.Chat.Get(s.Ops, "kim@lab.local").Unread);
		inbox.UndoCommand.Execute(null);
		Assert.Equal(2, s.Chat.Get(s.Ops, "kim@lab.local").Unread);

		// Several rows at once.
		inbox.SelectedRows = [.. inbox.Rows.Where(r => r.IsNotification || r.IsMessage)];
		Assert.True(inbox.HasMany);
		await inbox.DoneManyCommand.ExecuteAsync(null);
		Assert.Equal(0, inbox.MessageCount);
		Assert.Equal(0, inbox.NeuronCount);
		Assert.Equal(2, inbox.WaitingCount);
	}

	[AvaloniaFact]
	public async Task DoNotDisturbHoldsPopUpsBackAndCountsOn()
	{
		using Sample s = await SampleAsync();
		s.Vm.Settings.SetStatus(OwnStatus.DoNotDisturb);
		Assert.True(s.Alerts.IsQuiet);
		Assert.Equal(Waher.Networking.XMPP.Availability.DoNotDisturb, s.Chat.OwnAvailability);

		s.Chat.Preview(s.Ops, "kim@lab.local", "Are you there?", "Kim");
		Assert.Empty(s.Raised);
		s.Vm.Inbox.Refresh();
		Assert.Equal(1, s.Vm.Inbox.WaitingCount);

		s.Vm.Settings.SetStatus(OwnStatus.Away);
		Assert.False(s.Alerts.IsQuiet);
		Assert.Equal(Waher.Networking.XMPP.Availability.Away, s.Chat.OwnAvailability);
		Assert.Equal("Away", s.Vm.Settings.StatusText);

		// Switches in Settings reach the alert service.
		s.Vm.Settings.PopMessages = false;
		s.Chat.Preview(s.Ops, "kim@lab.local", "Hello?", "Kim");
		Assert.Empty(s.Raised);
		s.Vm.Settings.PopMessages = true;
		s.Chat.Preview(s.Ops, "kim@lab.local", "Hello??", "Kim");
		Assert.Single(s.Raised);

		// The test button shows a pop-up even with do not disturb on.
		s.Vm.Settings.SetStatus(OwnStatus.DoNotDisturb);
		s.Vm.Settings.TestMessageCommand.Execute(null);
		Assert.Equal(2, s.Raised.Count);
	}
}
