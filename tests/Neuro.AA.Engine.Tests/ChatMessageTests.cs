using System.Xml;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Chat;
using Neuro.AA.Engine.Events;
using Waher.Networking.XMPP;

namespace Neuro.AA.Engine.Tests;

/// <summary>Changing and taking back messages, groups of contacts, and conversations inside a room.</summary>
public class ChatMessageTests
{
	private static async Task<(ChatService Chat, Guid Account, List<ChatService.Delivery> Sent)> SetupAsync(bool connected = true)
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "NeuroAA.ChatMessages." + Guid.NewGuid().ToString("N"));
		AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		ChatService chat = new(manager, new MonitorService(manager, new EventHub()));
		List<ChatService.Delivery> sent = [];
		if (connected)
		{
			chat.Deliver = d =>
			{
				sent.Add(d);
				return Task.FromResult(false);
			};
		}

		return (chat, s.Profile.Id, sent);
	}

	private static XmlElement Stanza(string inner)
	{
		XmlDocument doc = new();
		doc.LoadXml("<message xmlns='jabber:client'>" + inner + "</message>");
		return doc.DocumentElement!;
	}

	[Fact]
	public void ReadsARetractionInBothSpellings()
	{
		Assert.Equal("m1", NeuronMonitor.ReadRetraction(Stanza("<retract id='m1' xmlns='urn:xmpp:message-retract:1'/><body>gone</body>")));
		Assert.Equal("m2", NeuronMonitor.ReadRetraction(Stanza("<apply-to id='m2' xmlns='urn:xmpp:fasten:0'><retract xmlns='urn:xmpp:message-retract:0'/></apply-to>")));
		Assert.Null(NeuronMonitor.ReadRetraction(Stanza("<body>retract id='m3'</body>")));
		Assert.Null(NeuronMonitor.ReadRetraction(Stanza("<retract xmlns='urn:xmpp:message-retract:1'/>")));
		Assert.Null(NeuronMonitor.ReadRetraction(null));
	}

	[Fact]
	public async Task AMessageOfYoursCanBeChanged()
	{
		var (chat, account, sent) = await SetupAsync();
		ChatMessage m = await chat.SendAsync(account, "kim@lab.local", "The backup runs at 02:00.");
		Conversation c = chat.Get(account, "kim@lab.local");

		ChatMessage? changed = await chat.EditAsync(c, m.Id, "The backup runs at 03:00.");

		// The change goes out as a correction of the first message, under an id of its own.
		Assert.Equal(2, sent.Count);
		Assert.Contains("<replace id=\"" + m.MessageId + "\" xmlns=\"urn:xmpp:message-correct:0\"/>", sent[1].CustomXml);
		Assert.Equal("The backup runs at 03:00.", sent[1].Body);
		Assert.NotEqual(m.MessageId, sent[1].Id);
		Assert.Equal("The backup runs at 03:00.", changed!.Markdown);
		Assert.NotNull(changed.Corrected);
		Assert.Null(changed.Error);

		// A second change still names the first message, which is what the other side knows it by.
		await chat.EditAsync(c, m.Id, "The backup runs at 04:00.");
		Assert.Contains("<replace id=\"" + m.MessageId + "\"", sent[2].CustomXml);
		Assert.Equal("The backup runs at 04:00.", Assert.Single(c.Messages).Markdown);

		// Somebody else's message is not yours to change.
		ChatMessage theirs = chat.Preview(account, "kim@lab.local", "Thanks.");
		Assert.Null(await chat.EditAsync(c, theirs.Id, "No thanks."));
		Assert.Equal(3, sent.Count);
	}

	[Fact]
	public async Task AChangeThatCannotBeSentLeavesTheMessageAsItWas()
	{
		var (chat, account, _) = await SetupAsync();
		ChatMessage m = await chat.SendAsync(account, "kim@lab.local", "First");
		Conversation c = chat.Get(account, "kim@lab.local");

		chat.Deliver = null;
		await Assert.ThrowsAsync<InvalidOperationException>(() => chat.EditAsync(c, m.Id, "Second"));
		Assert.Equal("First", Assert.Single(c.Messages).Markdown);

		await Assert.ThrowsAsync<InvalidOperationException>(() => chat.DeleteAsync(c, m.Id));
		Assert.Null(Assert.Single(c.Messages).Deleted);
	}

	[Fact]
	public async Task AMessageOfYoursIsTakenBackForEveryone()
	{
		var (chat, account, sent) = await SetupAsync();
		ChatMessage m = await chat.SendAsync(account, "kim@lab.local", "The password is hunter2");
		Conversation c = chat.Get(account, "kim@lab.local");

		await chat.DeleteAsync(c, m.Id);

		// One stanza says it both ways: taken back, and corrected to a line saying so.
		ChatService.Delivery d = sent[1];
		Assert.Contains("<retract id=\"" + m.MessageId + "\" xmlns=\"urn:xmpp:message-retract:1\"/>", d.CustomXml);
		Assert.Contains("<replace id=\"" + m.MessageId + "\" xmlns=\"urn:xmpp:message-correct:0\"/>", d.CustomXml);
		Assert.Equal(ChatService.DeletedText, d.Body);
		Assert.DoesNotContain("hunter2", d.CustomXml);

		// Its place stays, its text does not.
		ChatMessage gone = Assert.Single(c.Messages);
		Assert.NotNull(gone.Deleted);
		Assert.Equal(string.Empty, gone.Markdown);
		Assert.Equal(string.Empty, gone.Text);

		// It cannot be changed or taken back twice.
		Assert.Null(await chat.EditAsync(c, m.Id, "again"));
		Assert.Equal(2, sent.Count);
	}

	[Fact]
	public async Task SomebodyElsesMessageOnlyLeavesYourOwnHistory()
	{
		var (chat, account, sent) = await SetupAsync();
		ChatMessage theirs = chat.Preview(account, "kim@lab.local", "Hello");
		Conversation c = chat.Get(account, "kim@lab.local");

		await chat.DeleteAsync(c, theirs.Id);
		Assert.Empty(c.Messages);
		Assert.Empty(sent);
	}

	[Fact]
	public void InARoomOnlyTheWriterChangesOrTakesBackAMessage()
	{
		Conversation room = new(Guid.NewGuid(), "ops@muc.lab.local") { IsRoom = true };
		room.Add(new ChatMessage { Id = "a", MessageId = "x1", Direction = ChatDirection.In, Timestamp = DateTime.Now, Markdown = "Deploy at 14:00", Sender = "kim" });

		static Func<ChatMessage, bool> By(string nick) => m => m.Direction == ChatDirection.In && m.Sender == nick;

		Assert.False(room.Correct("x1", "Deploy cancelled", "Deploy cancelled", DateTime.Now, By("mallory")));
		Assert.False(room.Retract("x1", DateTime.Now, By("mallory")));
		Assert.Equal("Deploy at 14:00", room.Messages[0].Markdown);

		Assert.True(room.Correct("x1", "Deploy at 15:00", "Deploy at 15:00", DateTime.Now, By("kim")));
		Assert.Equal("Deploy at 15:00", room.Messages[0].Markdown);
		Assert.True(room.Retract("x1", DateTime.Now, By("kim")));
		Assert.NotNull(room.Messages[0].Deleted);

		// What is taken back stays taken back.
		Assert.False(room.Correct("x1", "Back again", "Back again", DateTime.Now, By("kim")));
		Assert.Equal(string.Empty, room.Messages[0].Markdown);
	}

	[Fact]
	public async Task ContactsCanBeRenamedAndGrouped()
	{
		var (chat, account, _) = await SetupAsync();
		chat.Preview(new Contact(account, "kim@lab.local", "Kim", [], SubscriptionState.Both, true, Availability.Online, null, false));
		chat.Preview(new Contact(account, "robin@lab.local", "Robin", ["On call"], SubscriptionState.Both, false, Availability.Offline, null, false));
		int changes = 0;
		chat.RosterChanged += (_, _) => changes++;

		await chat.RenameContactAsync(account, "kim@lab.local", "  Kim Lindqvist ");
		await chat.SetGroupsAsync(account, "kim@lab.local", ["Operations", " on call ", "", "operations"]);

		Contact kim = chat.Contacts(account).Single(c => c.BareJid == "kim@lab.local");
		Assert.Equal("Kim Lindqvist", kim.Name);
		// A group somebody is already in keeps the spelling it has.
		Assert.Equal(["Operations", "On call"], kim.Groups);
		Assert.Equal(2, changes);
		Assert.Equal(["On call", "Operations"], chat.Groups(account));

		// Someone who is not a contact cannot be renamed while the account is offline.
		await Assert.ThrowsAsync<InvalidOperationException>(() => chat.RenameContactAsync(account, "nobody@lab.local", "Nobody"));
	}

	[Fact]
	public async Task RunningARoomNeedsTheAccountConnected()
	{
		var (chat, account, _) = await SetupAsync();
		RoomInfo room = chat.PreviewRoom(account, "ops@muc.lab.local", "ops", "kim");
		chat.Preview(account, room.RoomJid, new RoomOccupant("ops", "Moderator", "Owner", "ops@lab.local/aa"));
		chat.Preview(account, room.RoomJid, new RoomOccupant("kim", "Visitor", "None", "kim@lab.local/phone"));

		Assert.True(room.Me!.IsModerator);
		Assert.True(room.Me.IsOwner);
		Assert.True(room.Me.IsAdmin);
		RoomOccupant kim = room.Occupants.Single(o => o.NickName == "kim");
		Assert.True(kim.IsVisitor);
		Assert.Equal("kim@lab.local", kim.BareJid);

		InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.RemoveFromRoomAsync(account, room.RoomJid, "kim", "Off topic"));
		Assert.Equal("The account is not connected.", ex.Message);
		await Assert.ThrowsAsync<InvalidOperationException>(() => chat.DeleteRoomAsync(account, room.RoomJid));
		Assert.NotNull(chat.Room(account, room.RoomJid));
	}

	[Fact]
	public void AConversationInsideARoomHasAnAddressAndANameOfItsOwn()
	{
		string jid = ChatService.PrivateJid("ops-team@muc.lab.local", "kim");
		Assert.Equal("ops-team@muc.lab.local/kim", jid);
		Assert.True(ChatService.IsPrivate(jid));
		Assert.False(ChatService.IsPrivate("kim@lab.local"));
		Assert.Equal("kim in ops-team", ChatService.PrivateLabel(jid));
		Assert.Equal("kim@lab.local", ChatService.PrivateLabel("kim@lab.local"));
	}
}
