using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Devices;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Topology;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.DataForms;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// Chat catches up: answering, changing and deleting messages, emoji, older messages a page at
/// a time, names and groups for people, and running a room. Renders frames for review.
/// </summary>
public class ChatCatchUpTests
{
	private const string OpsRoom = "ops-team@muc.lab.example.com";
	private const string IncidentRoom = "incident-4471@muc.prod.example.com";

	private sealed record Sample(MainWindowViewModel Vm, ChatService Chat, Guid Ops, Guid Admin,
		List<ChatService.Delivery> Sent, List<ChatService.RoomOperation> Asked, List<string> Said) : IDisposable
	{
		public ChatViewModel View => this.Vm.Chat;

		public void Dispose() => Ui.Said -= this.Said.Add;
	}

	private static void Capture(TopLevel window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);

		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;
		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		frame!.Save(Path.Combine(folder, name + ".png"));
	}

	private static bool Shows<T>(Visual root, Func<T, bool>? where = null)
		where T : Visual
		=> root.GetVisualDescendants().OfType<T>().Any(v => v.IsEffectivelyVisible && (where is null || where(v)));

	private static MainWindow Window(Sample s, string theme, int width = 1440, int height = 900)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		MainWindow window = new() { DataContext = s.Vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		return window;
	}

	private static string[] Texts(IReadOnlyList<ItemAction> actions) => [.. actions.Select(a => a.Text)];

	private static ItemAction Action(IReadOnlyList<ItemAction> actions, string text) => actions.Single(a => a.Text == text);

	private static OccupantViewModel Occupant(ChatViewModel chat, string nick) => chat.Occupants.Single(o => o.Nick == nick);

	/// <summary>Two accounts, three contacts in groups, and two rooms: one this account owns, one it only takes part in.</summary>
	private static async Task<Sample> SampleAsync()
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Chat." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.example.com", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		AccountSession admin = await manager.AddAsync(new AccountProfile { Host = "prod.example.com", UserName = "admin", ConnectOnStartup = false, ColorIndex = 3 }, AccountSecret.FromPassword("x"), connect: false);

		MonitorService monitors = new(manager, new EventHub());
		FleetService fleet = new(monitors);
		ChatService chat = new(manager, monitors);
		DevicesService devices = new(manager);
		Guid o = ops.Profile.Id;
		Guid a = admin.Profile.Id;

		List<ChatService.Delivery> sent = [];
		chat.Deliver = d =>
		{
			sent.Add(d);
			return Task.FromResult(false);
		};

		List<ChatService.RoomOperation> asked = [];
		chat.RoomStandIn = op =>
		{
			asked.Add(op);
			return Task.FromResult<object?>(op.What switch
			{
				"banned" => (IReadOnlyList<string>)["spam@bad.example"],
				"settings" => RoomSettings(),
				_ => null,
			});
		};

		chat.Preview(new Contact(o, "kim@lab.example.com", "Kim Lindqvist", ["On call"], SubscriptionState.Both, true, Availability.Online, null, false));
		chat.Preview(new Contact(o, "robin@lab.example.com", "Robin Ek", ["On call", "Operations"], SubscriptionState.Both, true, Availability.Away, "In a meeting", false));
		chat.Preview(new Contact(o, "sam@partner.example", "Sam Öberg", [], SubscriptionState.Both, false, Availability.Offline, null, false));

		// A room this account owns and moderates.
		chat.PreviewRoom(o, OpsRoom, "ops");
		chat.Preview(o, OpsRoom, new RoomOccupant("ops", "Moderator", "Owner", "ops@lab.example.com/aa"));
		chat.Preview(o, OpsRoom, new RoomOccupant("robin", "Moderator", "Admin", "robin@lab.example.com/laptop"));
		chat.Preview(o, OpsRoom, new RoomOccupant("kim", "Participant", "Member", "kim@lab.example.com/phone"));
		chat.Preview(o, OpsRoom, new RoomOccupant("guest", "Participant", "None", null));
		chat.Preview(o, OpsRoom, new RoomOccupant("sam", "Visitor", "None", "sam@partner.example/web"));

		// A room it only takes part in.
		chat.PreviewRoom(a, IncidentRoom, "admin");
		chat.Preview(a, IncidentRoom, new RoomOccupant("admin", "Participant", "Member", "admin@prod.example.com/aa"));
		chat.Preview(a, IncidentRoom, new RoomOccupant("lead", "Moderator", "Owner", "lead@prod.example.com/desk"));
		chat.Preview(a, IncidentRoom, new RoomOccupant("robin", "Participant", "Member", null));

		chat.Preview(o, OpsRoom, "Deploy window is 14:00 to 15:00 today.", "robin", room: true);
		chat.Preview(o, OpsRoom, "@ops can you watch gbg while it restarts?", "kim", room: true, mention: true);
		chat.Preview(o, OpsRoom, "I will take sto.", "guest", room: true);
		chat.MarkRead(chat.GetRoom(o, OpsRoom));

		List<string> said = [];
		Ui.Said += said.Add;

		MainWindowViewModel vm = new(manager, fleet, chat, null, devices);
		await vm.StartAsync();
		vm.Chat.Refresh();
		return new Sample(vm, chat, o, a, sent, asked, said);
	}

	/// <summary>A room's settings as a service gives them: a form with the usual room fields.</summary>
	private static DataForm RoomSettings()
	{
		XmlDocument doc = new();
		doc.LoadXml("""
			<x xmlns='jabber:x:data' type='form'>
			  <title>Room configuration</title>
			  <instructions>Settings of the room ops-team.</instructions>
			  <field var='FORM_TYPE' type='hidden'><value>http://jabber.org/protocol/muc#roomconfig</value></field>
			  <field var='muc#roomconfig_roomname' type='text-single' label='Name of the room'><value>Operations team</value></field>
			  <field var='muc#roomconfig_roomdesc' type='text-single' label='Short description'><value>Who is on call and what is being deployed.</value></field>
			  <field var='muc#roomconfig_persistentroom' type='boolean' label='Keep the room when the last person leaves'><value>1</value></field>
			  <field var='muc#roomconfig_membersonly' type='boolean' label='Only members may come in'><value>0</value></field>
			  <field var='muc#roomconfig_moderatedroom' type='boolean' label='Only people let in to speak may write'><value>1</value></field>
			  <field var='muc#roomconfig_maxusers' type='list-single' label='Most people in the room at once'>
			    <value>50</value>
			    <option label='10'><value>10</value></option>
			    <option label='50'><value>50</value></option>
			    <option label='No limit'><value>none</value></option>
			  </field>
			</x>
			""");
		return new DataForm(null, doc.DocumentElement!, null, null, OpsRoom, "ops@lab.example.com/aa");
	}

	// ----- Messages -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task MessagesCanBeAnsweredChangedAndTakenBack(string theme)
	{
		using Sample s = await SampleAsync();
		ChatViewModel chat = s.View;
		string t = theme.ToLowerInvariant();

		s.Chat.Preview(s.Ops, "kim@lab.example.com", "Morning! Did the backup on gbg finish?", "Kim Lindqvist");
		s.Chat.Preview(s.Ops, "kim@lab.example.com", "I see the 02:00 one failed and nothing after it.", "Kim Lindqvist");
		s.Chat.Preview(s.Ops, "kim@lab.example.com", "The root password is hunter2 btw", "Kim Lindqvist");
		await s.Chat.SendAsync(s.Ops, "kim@lab.example.com", "Looking at it now.");
		await s.Chat.SendAsync(s.Ops, "kim@lab.example.com", "The disk was ful. Cleared 40 GB.");
		s.Sent.Clear();

		s.Vm.ShowChatCommand.Execute(null);
		MainWindow window = Window(s, theme);
		Assert.True(chat.Open(s.Ops, "kim@lab.example.com"));
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(5, chat.Messages.Count);

		MessageViewModel theirs = chat.Messages[1];
		MessageViewModel secret = chat.Messages[2];
		MessageViewModel mine = chat.Messages[4];
		Assert.True(mine.CanChange);
		Assert.False(theirs.CanChange);

		// What a right-click offers depends on whose message it is.
		string[] onTheirs = Texts(chat.ActionsFor([theirs]));
		Assert.Equal("Reply", onTheirs[0]);
		Assert.DoesNotContain("Edit", onTheirs);
		Assert.Contains("Remove from my history…", onTheirs);
		string[] onMine = Texts(chat.ActionsFor([mine]));
		Assert.Equal(["Reply", "Edit"], onMine[..2]);
		Assert.Contains("Delete for everyone…", onMine);

		// Reply: the message is quoted above what you write.
		chat.ReplyCommand.Execute(theirs);
		Assert.Equal(ComposerMode.Reply, chat.Mode);
		Assert.Equal("Replying to Kim Lindqvist", chat.BannerTitle);
		Assert.True(theirs.IsTarget);
		chat.Draft = "Yes, the disk was full.";
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"chat-reply-{t}");
		await chat.SendCommand.ExecuteAsync(null);
		Assert.Equal("> I see the 02:00 one failed and nothing after it.\n\nYes, the disk was full.", s.Sent[^1].Body);
		Assert.Equal(ComposerMode.Write, chat.Mode);
		Assert.False(theirs.IsTarget);
		Assert.Equal(6, chat.Messages.Count);

		// Up in an empty box changes your last message. Something else was being written: it comes back after.
		chat.Draft = string.Empty;
		Assert.True(chat.EditLast());
		Assert.Equal(ComposerMode.Edit, chat.Mode);
		Assert.EndsWith("Yes, the disk was full.", chat.Draft);
		Assert.True(chat.CancelComposer());
		Assert.Equal(string.Empty, chat.Draft);

		chat.Draft = "half a thought";
		chat.EditCommand.Execute(mine);
		Assert.Equal("The disk was ful. Cleared 40 GB.", chat.Draft);
		Assert.Equal("Save", chat.SendText);
		chat.Draft = "The disk was full. Cleared 40 GB.";
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"chat-edit-{t}");
		await chat.SendCommand.ExecuteAsync(null);
		Assert.Contains("<replace id=", s.Sent[^1].CustomXml);
		Assert.Equal("The disk was full. Cleared 40 GB.", mine.Markdown);
		Assert.True(mine.IsCorrected);
		Assert.True(mine.ShowStamp);
		Assert.EndsWith("edited", mine.StampText);
		Assert.Equal(6, chat.Messages.Count);
		Assert.Equal("half a thought", chat.Draft);
		Assert.Equal(ComposerMode.Write, chat.Mode);
		chat.Draft = string.Empty;

		// Deleting asks first. Somebody else's message only leaves your own history.
		int sentBefore = s.Sent.Count;
		chat.DeleteCommand.Execute(secret);
		Assert.Equal("Remove this message from your history?", chat.Ask?.Title);
		Assert.True(chat.Ask!.IsDanger);
		await chat.ConfirmAskCommand.ExecuteAsync(null);
		Assert.Null(chat.Ask);
		Assert.DoesNotContain(secret, chat.Messages);
		Assert.Equal(sentBefore, s.Sent.Count);

		// One of yours is taken back for everyone: its place stays, its text goes.
		MessageViewModel first = chat.Messages.First(m => m.IsMine);
		chat.DeleteCommand.Execute(first);
		Assert.Equal("Delete this message for everyone?", chat.Ask?.Title);
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"chat-delete-ask-{t}");
		chat.CancelAskCommand.Execute(null);
		Assert.False(first.IsDeleted);
		chat.DeleteCommand.Execute(first);
		await chat.ConfirmAskCommand.ExecuteAsync(null);
		Assert.True(first.IsDeleted);
		Assert.Equal("You deleted this message", first.DeletedText);
		Assert.False(first.CanChange);
		Assert.Contains("<retract id=", s.Sent[^1].CustomXml);
		Assert.Equal(["Remove from my history"], Texts(chat.ActionsFor([first])));

		// A message that could not be sent says so, and can be sent again.
		s.Chat.Deliver = null;
		chat.Draft = "Restarting gbg now.";
		await chat.SendCommand.ExecuteAsync(null);
		MessageViewModel failed = chat.Messages[^1];
		Assert.True(failed.IsFailed);
		Assert.Null(chat.SendError);
		Assert.Contains("Send again", Texts(chat.ActionsFor([failed])));
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"chat-messages-{t}");
		s.Chat.Deliver = d =>
		{
			s.Sent.Add(d);
			return Task.FromResult(false);
		};
		await chat.SendAgainCommand.ExecuteAsync(failed);
		Assert.False(chat.Messages[^1].IsFailed);
		Assert.Equal("Restarting gbg now.", s.Sent[^1].Body);

		// What you were writing stays with the conversation you were writing it in.
		chat.Draft = "For Kim only";
		Assert.True(chat.Open(s.Ops, "robin@lab.example.com"));
		Assert.Equal(string.Empty, chat.Draft);
		Assert.True(chat.Open(s.Ops, "kim@lab.example.com"));
		Assert.Equal("For Kim only", chat.Draft);
		window.Close();
	}

	[AvaloniaFact]
	public async Task EmojiComeFromThePickerOrAColon()
	{
		// The rules by themselves.
		Assert.Equal("🚀", Emoji.Find("rocket")?.Symbol);
		Assert.Equal("👍", Emoji.Find("+1")?.Symbol);
		Assert.Equal((12, "roc"), Emoji.WordAt("Deploy done :roc", 16));
		Assert.Null(Emoji.WordAt("Meet at 10:30", 13));
		Assert.Null(Emoji.WordAt("Deploy done :r", 14));
		Assert.Equal("rocket", Emoji.Suggest("roc")[0].Name);
		Assert.Equal("Done 🎉 see `:tada:` and urn:xmpp:ping at 10:30:15", Emoji.Replace("Done :tada: see `:tada:` and urn:xmpp:ping at 10:30:15"));

		using Sample s = await SampleAsync();
		ChatViewModel chat = s.View;
		s.Vm.ShowChatCommand.Execute(null);
		MainWindow window = Window(s, "Light");
		Assert.True(chat.Open(s.Ops, "kim@lab.example.com"));

		// Typing a colon and a name offers emoji; Tab or Enter takes the marked one.
		chat.Draft = "Deploy done :roc";
		chat.Caret = chat.Draft.Length;
		Assert.True(chat.HasSuggestions);
		Assert.Equal("rocket", chat.Suggestions[0].Choice.Name);
		Assert.True(chat.Suggestions[0].IsSelected);
		Dispatcher.UIThread.RunJobs();
		Assert.True(Shows<TextBlock>(window, x => x.Text == "rocket"));
		Capture(window, "chat-emoji-suggest-light");

		Assert.True(chat.AcceptSuggestion());
		Assert.Equal("Deploy done 🚀 ", chat.Draft);
		Assert.Equal(chat.Draft.Length, chat.Caret);
		Assert.False(chat.HasSuggestions);

		// Down and Up move among several.
		chat.Draft = "All :ch";
		chat.Caret = chat.Draft.Length;
		Assert.Equal(["check", "chart_up", "chart_down"], chat.Suggestions.Take(3).Select(x => x.Choice.Name));
		Assert.True(chat.MoveSuggestion(1));
		Assert.True(chat.Suggestions[1].IsSelected);
		Assert.True(chat.MoveSuggestion(-1));
		Assert.True(chat.AcceptSuggestion());
		Assert.Equal("All ✅ ", chat.Draft);

		// Esc closes the suggestions and leaves the text alone.
		chat.Draft = "see :war";
		chat.Caret = chat.Draft.Length;
		Assert.True(chat.HasSuggestions);
		Assert.True(chat.CancelComposer());
		Assert.False(chat.HasSuggestions);
		Assert.Equal("see :war", chat.Draft);

		// The picker puts an emoji in where the caret is.
		chat.Draft = "Good work";
		chat.Caret = 4;
		chat.InsertEmojiCommand.Execute(Emoji.Find("clap"));
		Assert.Equal("Good👏 work", chat.Draft);

		// A whole ":name:" becomes its emoji when the message is sent.
		chat.Draft = "Fixed :tada:";
		await chat.SendCommand.ExecuteAsync(null);
		Assert.Equal("Fixed 🎉", s.Sent[^1].Body);

		// The picker itself.
		Button picker = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "EmojiButton" && b.IsEffectivelyVisible);
		picker.Flyout!.ShowAt(picker);
		Dispatcher.UIThread.RunJobs();
		Capture(window, "chat-emoji-picker-light");
		picker.Flyout.Hide();
		window.Close();
	}

	[AvaloniaFact]
	public async Task OlderMessagesComeAPageAtATime()
	{
		using Sample s = await SampleAsync();
		ChatViewModel chat = s.View;
		for (int i = 1; i <= 130; i++)
			s.Chat.Preview(s.Ops, "kim@lab.example.com", "Message " + i, "Kim Lindqvist");
		s.Chat.MarkRead(s.Chat.Get(s.Ops, "kim@lab.example.com"));

		chat.Refresh();
		Assert.True(chat.Open(s.Ops, "kim@lab.example.com"));
		Assert.Equal(ChatViewModel.PageSize, chat.Messages.Count);
		Assert.Equal("Message 81", chat.Messages[0].Markdown);
		Assert.True(chat.HasOlder);

		chat.ShowOlderCommand.Execute(null);
		Assert.Equal(100, chat.Messages.Count);
		Assert.Equal("Message 31", chat.Messages[0].Markdown);
		Assert.Equal("Message 130", chat.Messages[^1].Markdown);

		// A new message does not push the old ones out again.
		await s.Chat.SendAsync(s.Ops, "kim@lab.example.com", "Got them all.");
		chat.Open(s.Ops, "kim@lab.example.com");
		Assert.Equal(101, chat.Messages.Count);

		chat.ShowOlderCommand.Execute(null);
		Assert.Equal(131, chat.Messages.Count);
		Assert.False(chat.HasOlder);

		// Everything unread is shown, however much it is: the "New" line is never out of reach.
		for (int i = 1; i <= 70; i++)
			s.Chat.Preview(s.Ops, "robin@lab.example.com", "Unread " + i, "Robin Ek");
		chat.Refresh();
		Assert.True(chat.Open(s.Ops, "robin@lab.example.com"));
		Assert.Equal(70, chat.Messages.Count);
		Assert.True(chat.Messages[0].IsFirstNew);
	}

	// ----- People -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task PeopleCanBeNamedAndGrouped(string theme)
	{
		using Sample s = await SampleAsync();
		ChatViewModel chat = s.View;
		string t = theme.ToLowerInvariant();
		s.Chat.Preview(s.Ops, "kim@lab.example.com", "Morning! Did the backup on gbg finish?", "Kim Lindqvist");
		s.Chat.MarkRead(s.Chat.Get(s.Ops, "kim@lab.example.com"));
		s.Vm.ShowChatCommand.Execute(null);
		MainWindow window = Window(s, theme);
		chat.Refresh();

		// The groups are above the list. One click narrows it to a group, another shows everyone.
		Assert.True(chat.HasGroups);
		Assert.Equal(["On call", "Operations"], chat.GroupChoices.Select(g => g.Name));
		chat.PickGroupCommand.Execute(chat.GroupChoices[1]);
		Assert.Equal(["Robin Ek"], chat.People.Select(p => p.Label));
		Assert.True(chat.GroupChoices[1].IsSelected);
		chat.PickGroupCommand.Execute(chat.GroupChoices[1]);
		Assert.Equal(3, chat.People.Count);

		// The filter finds people by their group too.
		chat.FilterText = "on call";
		chat.Refresh();
		Assert.Equal(["Kim Lindqvist", "Robin Ek"], chat.People.Select(p => p.Label).Order());
		chat.FilterText = string.Empty;

		// Details: the name you gave them, and the groups they are in.
		Assert.True(chat.Open(s.Ops, "kim@lab.example.com"));
		PersonViewModel kim = chat.SelectedPerson!;
		Assert.False(chat.IsPanelOpen);
		chat.TogglePanelCommand.Execute(null);
		Assert.True(chat.IsPersonPanel);
		Assert.Equal("Kim Lindqvist", chat.DetailName);
		Assert.False(chat.CanSaveName);
		Assert.Equal(["Operations"], chat.OtherGroups);
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"chat-details-{t}");

		chat.DetailName = "Kim L";
		Assert.True(chat.CanSaveName);
		await chat.SaveNameCommand.ExecuteAsync(null);
		Assert.Equal("Kim L", kim.Label);
		Assert.False(chat.CanSaveName);

		// A group that exists keeps its spelling; a new name makes a new group.
		chat.NewGroup = "operations";
		await chat.AddGroupCommand.ExecuteAsync(null);
		Assert.Equal(["On call", "Operations"], kim.Groups);
		Assert.Equal(string.Empty, chat.NewGroup);
		Assert.Empty(chat.OtherGroups);
		chat.NewGroup = "Night shift";
		await chat.AddGroupCommand.ExecuteAsync(null);
		Assert.Contains("Night shift", chat.GroupChoices.Select(g => g.Name));
		await chat.RemoveGroupCommand.ExecuteAsync("On call");
		Assert.Equal(["Night shift", "Operations"], kim.Groups);

		// The same from a right-click on the person.
		string[] actions = Texts(chat.ActionsFor([kim]));
		Assert.Contains("Rename…", actions);
		Assert.Contains("Details and groups", actions);
		Assert.Contains("Remove from contacts…", actions);
		Action(chat.ActionsFor([kim]), "Rename…").RunAsync().Wait();
		Assert.Equal("Kim L", chat.Ask?.Input);
		chat.Ask!.Input = "Kim Lindqvist";
		await chat.ConfirmAskCommand.ExecuteAsync(null);
		Assert.Equal("Kim Lindqvist", kim.Label);
		Assert.Equal("Kim Lindqvist", chat.DetailName);

		// Someone who wrote without being a contact can be made one.
		s.Chat.Preview(s.Ops, "pat@elsewhere.example", "Hi, is this the lab's operator?");
		chat.Refresh();
		Assert.True(chat.Open(s.Ops, "pat@elsewhere.example"));
		Assert.True(chat.CanAddOpen);
		Assert.False(chat.SelectedPerson!.InRoster);
		string[] onStranger = Texts(chat.ActionsFor([chat.SelectedPerson]));
		Assert.Contains("Add to contacts", onStranger);
		Assert.DoesNotContain("Rename…", onStranger);
		window.Close();
	}

	// ----- Rooms -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task ARoomShowsWhoIsInItAndLetsItsModeratorsRunIt(string theme)
	{
		using Sample s = await SampleAsync();
		ChatViewModel chat = s.View;
		string t = theme.ToLowerInvariant();
		s.Vm.ShowChatCommand.Execute(null);
		MainWindow window = Window(s, theme);
		Assert.True(chat.Open(s.Ops, OpsRoom));
		Dispatcher.UIThread.RunJobs();
		PersonViewModel room = chat.SelectedPerson!;

		// The panel beside the room: everyone in it, by what they may do.
		Assert.True(chat.IsRoomPanel);
		Assert.Equal(["ops", "robin", "guest", "kim", "sam"], chat.Occupants.Select(o => o.Nick));
		Assert.Equal(["Moderators", null, "Participants", null, "Visitors, who can only read"], chat.Occupants.Select(o => o.Header));
		Assert.True(Occupant(chat, "ops").IsMe);
		Assert.Equal("Owner", Occupant(chat, "ops").Standing);
		Capture(window, $"chat-room-{t}");

		// An owner may do everything to the ones below; nothing is offered for yourself but copying.
		Assert.Equal(["Copy name", "Copy address"], Texts(chat.ActionsFor([Occupant(chat, "ops")])));
		string[] onSam = Texts(chat.ActionsFor([Occupant(chat, "sam")]));
		Assert.Equal("Message privately", onSam[0]);
		Assert.Contains("Change what they may do", onSam);
		Assert.Contains("Remove from room…", onSam);
		Assert.Contains("Ban…", onSam);
		// Banning needs their address, and the room does not show the guest's.
		Assert.DoesNotContain("Ban…", Texts(chat.ActionsFor([Occupant(chat, "guest")])));

		// Letting a visitor write.
		ItemAction roles = Action(chat.ActionsFor([Occupant(chat, "sam")]), "Change what they may do");
		Assert.Equal(3, roles.Children!.Count);
		Assert.False(roles.Children[0].IsEnabled);
		await roles.Children[1].RunAsync();
		Assert.Equal(new ChatService.RoomOperation("role", s.Ops, OpsRoom, "sam", "Participant"), s.Asked[^1]);
		chat.Refresh();
		Assert.Null(Occupant(chat, "sam").Header);
		Assert.DoesNotContain(chat.Occupants, o => o.Header?.StartsWith("Visitors", StringComparison.Ordinal) == true);
		Assert.Contains("sam may now read and write", s.Said);

		// Removing someone asks for a reason first.
		await Action(chat.ActionsFor([Occupant(chat, "guest")]), "Remove from room…").RunAsync();
		Assert.Equal("Remove guest from the room?", chat.Ask?.Title);
		Assert.True(chat.Ask!.HasInput);
		chat.Ask.Input = "Not on the team";
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"chat-room-ask-{t}");
		await chat.ConfirmAskCommand.ExecuteAsync(null);
		Assert.Null(chat.Ask);
		Assert.Equal(new ChatService.RoomOperation("remove", s.Ops, OpsRoom, "guest", "Not on the team"), s.Asked[^1]);
		Assert.DoesNotContain(chat.Occupants, o => o.Nick == "guest");

		// A ban is on their address.
		await Action(chat.ActionsFor([Occupant(chat, "sam")]), "Ban…").RunAsync();
		await chat.ConfirmAskCommand.ExecuteAsync(null);
		Assert.Equal(new ChatService.RoomOperation("ban", s.Ops, OpsRoom, "sam@partner.example", string.Empty), s.Asked[^1]);
		Assert.DoesNotContain(chat.Occupants, o => o.Nick == "sam");

		// The room's own menu: the same from the "more" button and from a right-click on the room.
		string[] onRoom = Texts(chat.ActionsFor([room]));
		Assert.Contains("Change subject…", onRoom);
		Assert.Contains("Invite someone…", onRoom);
		Assert.Contains("Room settings…", onRoom);
		Assert.Contains("Banned list…", onRoom);
		Assert.Contains("Leave room…", onRoom);
		Assert.Contains("Delete room…", onRoom);
		Assert.DoesNotContain("Ask to speak", onRoom);

		await Action(chat.ActionsFor([room]), "Change subject…").RunAsync();
		chat.Ask!.Input = "Deploy 14:00, Kim on call";
		await chat.ConfirmAskCommand.ExecuteAsync(null);
		Assert.Equal("Deploy 14:00, Kim on call", room.Subject);

		await Action(chat.ActionsFor([room]), "Invite someone…").RunAsync();
		Assert.False(chat.Ask!.CanConfirm);
		chat.Ask.Input = "pat@elsewhere.example";
		await chat.ConfirmAskCommand.ExecuteAsync(null);
		Assert.Equal("invite", s.Asked[^1].What);
		Assert.Equal("pat@elsewhere.example", s.Asked[^1].Who);

		// The banned list, and letting someone back in.
		await Action(chat.ActionsFor([room]), "Banned list…").RunAsync();
		Assert.True(chat.IsBannedOpen);
		Assert.Equal(["spam@bad.example"], chat.Banned);
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"chat-room-banned-{t}");
		await chat.LetBackInCommand.ExecuteAsync("spam@bad.example");
		Assert.Equal(new ChatService.RoomOperation("unban", s.Ops, OpsRoom, "spam@bad.example"), s.Asked[^1]);
		Assert.Empty(chat.Banned);
		Assert.Equal("Nobody is banned from this room.", chat.BannedNote);
		chat.CloseBannedCommand.Execute(null);

		// The settings are the form the room's service gives.
		await Action(chat.ActionsFor([room]), "Room settings…").RunAsync();
		Assert.True(chat.IsRoomFormOpen);
		Dispatcher.UIThread.RunJobs();
		Assert.True(Shows<TextBlock>(window, x => x.Text?.Contains("Only members may come in", StringComparison.Ordinal) == true)
			|| Shows<CheckBox>(window, x => x.Content?.ToString()?.Contains("Only members may come in", StringComparison.Ordinal) == true)
			|| Shows<ToggleSwitch>(window));
		Capture(window, $"chat-room-settings-{t}");
		await chat.SaveRoomFormCommand.ExecuteAsync(null);
		Assert.False(chat.IsRoomFormOpen);
		Assert.Equal("save", s.Asked[^1].What);

		// A conversation with one person in the room goes through the room, to them alone.
		await Action(chat.ActionsFor([Occupant(chat, "kim")]), "Message privately").RunAsync();
		Assert.True(chat.ShowPeople);
		Assert.True(chat.SelectedPerson!.IsPrivate);
		Assert.Equal("kim in ops-team", chat.SelectedPerson.Label);
		Assert.Equal("In the room now", chat.SelectedPerson.PresenceText);
		chat.Draft = "Can you take the pager tonight?";
		await chat.SendCommand.ExecuteAsync(null);
		Assert.Equal(OpsRoom + "/kim", s.Sent[^1].Jid);
		Assert.False(s.Sent[^1].Room);
		Assert.Contains("The room ops-team", Texts(chat.ActionsFor([chat.SelectedPerson])));
		chat.OpenRoomOfPrivateCommand.Execute(null);
		Assert.Same(room, chat.SelectedPerson);

		// Deleting the room wants its name typed in.
		await Action(chat.ActionsFor([room]), "Delete room…").RunAsync();
		Assert.Equal("ops-team", chat.Ask!.MustMatch);
		Assert.False(chat.Ask.CanConfirm);
		chat.Ask.Input = "ops";
		Assert.False(chat.Ask.CanConfirm);
		chat.Ask.Input = "ops-team";
		Assert.True(chat.Ask.CanConfirm);
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"chat-room-delete-{t}");
		await chat.ConfirmAskCommand.ExecuteAsync(null);
		Assert.Equal("delete", s.Asked[^1].What);
		Assert.Null(chat.SelectedPerson);
		Assert.Null(s.Chat.Room(s.Ops, OpsRoom));
		Assert.Contains("Deleted the room ops-team", s.Said);
		window.Close();
	}

	[AvaloniaFact]
	public async Task WhatYouMayNotDoInARoomIsNotOffered()
	{
		using Sample s = await SampleAsync();
		ChatViewModel chat = s.View;
		Assert.True(chat.Open(s.Admin, IncidentRoom));
		PersonViewModel room = chat.SelectedPerson!;

		// Somebody who only takes part runs nothing.
		string[] onRoom = Texts(chat.ActionsFor([room]));
		Assert.Contains("Change subject…", onRoom);
		Assert.Contains("Invite someone…", onRoom);
		Assert.Contains("Leave room…", onRoom);
		Assert.DoesNotContain("Room settings…", onRoom);
		Assert.DoesNotContain("Banned list…", onRoom);
		Assert.DoesNotContain("Delete room…", onRoom);

		string[] onRobin = Texts(chat.ActionsFor([Occupant(chat, "robin")]));
		Assert.Equal(["Message privately", "Copy name"], onRobin);
		string[] onLead = Texts(chat.ActionsFor([Occupant(chat, "lead")]));
		Assert.Equal(["Message privately", "Copy name", "Copy address", "Add to contacts"], onLead);

		// A moderator who is not an admin may quiet and remove the ones below, but not make moderators or ban.
		s.Chat.Preview(s.Admin, IncidentRoom, new RoomOccupant("admin", "Moderator", "Member", "admin@prod.example.com/aa"));
		chat.Refresh();
		string[] asModerator = Texts(chat.ActionsFor([Occupant(chat, "robin")]));
		Assert.Contains("Remove from room…", asModerator);
		Assert.DoesNotContain("Ban…", asModerator);
		Assert.Equal(2, Action(chat.ActionsFor([Occupant(chat, "robin")]), "Change what they may do").Children!.Count);
		// The owner is above a moderator.
		Assert.DoesNotContain("Remove from room…", Texts(chat.ActionsFor([Occupant(chat, "lead")])));

		// Where you may only read, the box says so and offers to ask.
		s.Chat.Preview(s.Admin, IncidentRoom, new RoomOccupant("admin", "Visitor", "None", "admin@prod.example.com/aa"));
		chat.Refresh();
		Assert.True(room.IsReadOnly);
		Assert.Contains("Ask to speak", Texts(chat.ActionsFor([room])));
		await chat.AskToSpeakCommand.ExecuteAsync(null);
		Assert.Equal("speak", s.Asked[^1].What);

		// Leaving asks first, because the messages kept here go with it.
		await Action(chat.ActionsFor([room]), "Leave room…").RunAsync();
		Assert.Equal("Leave incident-4471?", chat.Ask?.Title);
		await chat.ConfirmAskCommand.ExecuteAsync(null);
		Assert.Null(s.Chat.Room(s.Admin, IncidentRoom));
		Assert.Null(chat.SelectedPerson);
	}

	[AvaloniaFact]
	public async Task ANarrowConversationKeepsItsWidth()
	{
		using Sample s = await SampleAsync();
		ChatViewModel chat = s.View;
		s.Vm.ShowChatCommand.Execute(null);
		MainWindow window = Window(s, "Light", 900, 700);
		Assert.True(chat.Open(s.Ops, OpsRoom));
		Dispatcher.UIThread.RunJobs();

		// The panel beside the room is closed until asked for.
		Assert.False(chat.IsPanelOpen);
		Capture(window, "chat-room-900");
		chat.TogglePanelCommand.Execute(null);
		Assert.True(chat.IsRoomPanel);
		Dispatcher.UIThread.RunJobs();
		Capture(window, "chat-room-900-panel");
		window.Close();
	}
}
