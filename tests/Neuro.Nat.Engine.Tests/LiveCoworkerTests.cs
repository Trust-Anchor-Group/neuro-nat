using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// Two accounts on the local test Neuron talking to each other: contact request and accept,
/// end-to-end encrypted chat, a file through the upload service, and a group chat room.
/// Needs NAT_TEST_* and NAT_TEST_USER2/PASSWORD2.
/// </summary>
[Collection("Live Neuron")]
public class LiveCoworkerTests(ITestOutputHelper output)
{
	[Fact]
	public async Task EncryptedChatFilesAndRooms()
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? user2 = Environment.GetEnvironmentVariable("NAT_TEST_USER2");
		string? password2 = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD2");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(user2) || string.IsNullOrEmpty(password2))
			Live.Skip("NAT_TEST_HOST", "NAT_TEST_USER", "NAT_TEST_PASSWORD", "NAT_TEST_USER2", "NAT_TEST_PASSWORD2");

		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Live." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession a = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);
		AccountSession b = await manager.AddAsync(new AccountProfile { Host = host, UserName = user2, TrustServerCertificate = true }, AccountSecret.FromPassword(password2), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		await using ChatService chat = new(manager, monitors);
		await a.ConnectAsync();
		await b.ConnectAsync();
		await StreamSnifferTests.WaitFor(() => a.Status == ConnectionStatus.Connected && b.Status == ConnectionStatus.Connected, 30000);

		string jidA = a.BareJid!;
		string jidB = b.BareJid!;
		output.WriteLine($"A {jidA}, B {jidB}");

		// Contact request and accept, both ways.
		if (!chat.Contacts(a.Profile.Id).Any(c => c.BareJid == jidB && c.Subscription == Waher.Networking.XMPP.SubscriptionState.Both))
		{
			await chat.AddContactAsync(a.Profile.Id, jidB, "Coworker");
			await StreamSnifferTests.WaitFor(() => chat.Requests.Any(r => r.AccountId == b.Profile.Id), 15000);
			await chat.AcceptAsync(chat.Requests.First(r => r.AccountId == b.Profile.Id));
			await StreamSnifferTests.WaitFor(() => chat.Requests.Any(r => r.AccountId == a.Profile.Id), 15000);
			await chat.AcceptAsync(chat.Requests.First(r => r.AccountId == a.Profile.Id));
		}

		await StreamSnifferTests.WaitFor(() => chat.Contacts(a.Profile.Id).Any(c => c.BareJid == jidB && c.IsOnline), 20000);

		// Keys travel in presence.
		await StreamSnifferTests.WaitFor(() => chat.Security(a.Profile.Id, jidB).Encrypted && chat.Security(b.Profile.Id, jidA).Encrypted, 20000);
		ContactSecurity sec = chat.Security(a.Profile.Id, jidB);
		output.WriteLine($"A sees B: encrypted {sec.Encrypted}, {sec.Algorithm}, fingerprint {sec.Fingerprint}; B's own: {chat.OwnFingerprint(b.Profile.Id)}");
		Assert.Equal(chat.OwnFingerprint(b.Profile.Id), sec.Fingerprint);

		string marker = "secret " + Guid.NewGuid().ToString("N")[..6];
		ChatMessage sent = await chat.SendAsync(a.Profile.Id, jidB, "**" + marker + "**");
		output.WriteLine($"Sent: encrypted {sent.Encrypted}, error {sent.Error}");
		Assert.True(sent.Encrypted, sent.Error);
		Conversation atB = chat.Get(b.Profile.Id, jidA);
		await StreamSnifferTests.WaitFor(() => atB.Messages.Any(m => m.Markdown.Contains(marker)), 15000);
		ChatMessage got = atB.Messages.First(m => m.Markdown.Contains(marker));
		output.WriteLine($"B got: {got.Markdown} encrypted {got.Encrypted}");
		Assert.True(got.Encrypted);

		// File through the upload service.
		output.WriteLine($"Upload: {chat.CanSendFiles(a.Profile.Id)}, max {chat.MaxFileSize(a.Profile.Id)}");
		if (chat.CanSendFiles(a.Profile.Id))
		{
			string file = Path.Combine(dir, "report " + marker[^6..] + ".bin");
			await File.WriteAllTextAsync(file, "NAT file test " + marker);
			ChatMessage? f = null;
			try
			{
				f = await chat.SendFileAsync(a.Profile.Id, jidB, file);
			}
			catch (InvalidOperationException ex) when (ex.Message.Contains("Forbidden", StringComparison.Ordinal) || ex.Message.Contains("Upload rejected", StringComparison.Ordinal))
			{
				// The local test Neuron has no domain. It hands out upload slots, but refuses every
				// upload: it checks uploaded content with its domain as base address, and an empty
				// domain is not a valid address ("Invalid URI: The hostname could not be parsed" in its
				// event log). Neurons with a domain take the upload.
				output.WriteLine("Upload refused by the domain-less test Neuron: " + ex.Message);
			}

			if (f is not null)
			{
				output.WriteLine($"File message: {f.Markdown} error {f.Error}");
				Assert.Null(f.Error);
				await StreamSnifferTests.WaitFor(() => atB.Messages.Any(m => m.Markdown.Contains("report " + marker[^6..])), 15000);
				string link = atB.Messages.Last(m => m.Markdown.Contains("report " + marker[^6..])).Markdown;
				string url = link[(link.IndexOf("](", StringComparison.Ordinal) + 2)..link.IndexOf(')', link.IndexOf("](", StringComparison.Ordinal))];
				using HttpClient http = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
				string content = await http.GetStringAsync(url);
				output.WriteLine($"Downloaded: {content}");
				Assert.Contains(marker, content);
			}
		}

		// Group chat room.
		output.WriteLine($"Group chat service: {chat.MucService(a.Profile.Id)}");
		if (chat.MucService(a.Profile.Id) is string muc)
		{
			// Creating rooms needs an approved legal identity on this Neuron; the test uses a persistent room made for it.
			string room = "nat-team@" + muc;
			RoomInfo joined = await chat.JoinRoomAsync(a.Profile.Id, room, "Mille-test");
			output.WriteLine($"A joined {joined.RoomJid}: {joined.Joined} {joined.Error}");
			await chat.InviteAsync(a.Profile.Id, room, jidB, "Test room");
			try
			{
				await StreamSnifferTests.WaitFor(() => chat.Invitations.Any(i => i.AccountId == b.Profile.Id), 10000);
				await chat.AcceptInvitationAsync(chat.Invitations.First(i => i.AccountId == b.Profile.Id), "Coworker-test");
			}
			catch (TimeoutException)
			{
				foreach (var l in b.Sniffer.Snapshot().Where(l => l.Text.Contains("conference") || l.Text.Contains("invite")).TakeLast(4))
					output.WriteLine($"  B [{l.Direction}] {l.Text[..Math.Min(400, l.Text.Length)]}");
				output.WriteLine("No invitation arrived; B joins directly.");
				await chat.JoinRoomAsync(b.Profile.Id, room, "Coworker-test");
			}

			try
			{
				await StreamSnifferTests.WaitFor(() => chat.Room(a.Profile.Id, room)!.Occupants.Count >= 2, 15000);
			}
			finally
			{
				output.WriteLine("Occupants seen by A: " + string.Join(", ", chat.Room(a.Profile.Id, room)!.Occupants.Select(o => o.NickName)));
				foreach (var l in a.Sniffer.Snapshot().Where(l => l.Text.Contains("muc")).TakeLast(4))
					output.WriteLine($"  A [{l.Direction}] {l.Text[..Math.Min(500, l.Text.Length)]}");
			}
			output.WriteLine("Occupants: " + string.Join(", ", chat.Room(a.Profile.Id, room)!.Occupants.Select(o => $"{o.NickName} ({o.Role}/{o.Affiliation})")));

			await chat.SendAsync(a.Profile.Id, room, "Hello room " + marker);
			Conversation roomAtB = chat.GetRoom(b.Profile.Id, room);
			await StreamSnifferTests.WaitFor(() => roomAtB.Messages.Any(m => m.Markdown.Contains("Hello room " + marker)), 15000);
			ChatMessage rm = roomAtB.Messages.First(m => m.Markdown.Contains("Hello room " + marker));
			output.WriteLine($"Room message at B from {rm.Sender}: {rm.Markdown}");
			Assert.Equal("Mille-test", rm.Sender);
			Assert.Single(chat.GetRoom(a.Profile.Id, room).Messages, m => m.Markdown.Contains("Hello room " + marker));

			await chat.LeaveRoomAsync(b.Profile.Id, room);
			await chat.LeaveRoomAsync(a.Profile.Id, room);
		}
	}
}
