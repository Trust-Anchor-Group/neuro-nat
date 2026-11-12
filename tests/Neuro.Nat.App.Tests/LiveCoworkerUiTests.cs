using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// Two accounts on the local test Neuron in one app: encrypted chat between them, the test room,
/// a share from another workspace, and the raw XMPP console. Saves frames for review.
/// Needs NAT_TEST_* and NAT_TEST_USER2/PASSWORD2; does nothing without them.
/// </summary>
[Collection("Live Neuron")]
public class LiveCoworkerUiTests
{
	private static async Task Until(Func<bool> condition, int timeoutMs = 30000)
	{
		long end = Environment.TickCount64 + timeoutMs;
		while (!condition())
		{
			if (Environment.TickCount64 > end)
				throw new TimeoutException("Condition not met in time.");
			Dispatcher.UIThread.RunJobs();
			await Task.Delay(50);
		}
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task EncryptedChatRoomShareAndConsole(string theme)
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? user2 = Environment.GetEnvironmentVariable("NAT_TEST_USER2");
		string? password2 = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD2");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(user2) || string.IsNullOrEmpty(password2))
			return;

		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.LiveUi." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession a = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true, ConnectOnStartup = false, ColorIndex = 0 },
			AccountSecret.FromPassword(password), connect: false);
		AccountSession b = await manager.AddAsync(new AccountProfile { Host = host, UserName = user2, TrustServerCertificate = true, ConnectOnStartup = false, ColorIndex = 3 },
			AccountSecret.FromPassword(password2), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		await using ChatService chat = new(manager, monitors);
		MainWindowViewModel vm = new(manager, new FleetService(monitors), chat);
		await vm.StartAsync();
		await a.ConnectAsync();
		await b.ConnectAsync();
		await Until(() => chat.Security(a.Profile.Id, b.BareJid!).Encrypted && chat.MucService(a.Profile.Id) is not null, 40000);

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();

		// Encrypted chat, A to B and back.
		vm.ShowChatCommand.Execute(null);
		ChatViewModel c = vm.Chat;
		await Until(() => c.People.Any(p => p.AccountId == a.Profile.Id && p.BareJid == b.BareJid));
		c.SelectedPerson = c.People.First(p => p.AccountId == a.Profile.Id && p.BareJid == b.BareJid);
		c.Draft = "Hi! The **Gothenburg** Neuron logged three backup failures last night. Can you take a look?";
		await c.SendCommand.ExecuteAsync(null);
		await chat.SendAsync(b.Profile.Id, a.BareJid!, "Sure, looking now. It's the disk on `backup@vault`, I'll clear space.");
		await Until(() => c.Messages.Count(m => !m.IsMine) >= 1);
		await Task.Delay(500);
		Assert.True(c.SelectedPerson!.IsEncrypted);
		Assert.All(c.Messages, m => Assert.True(m.IsEncrypted));
		Save(window, $"chat-e2e-{theme.ToLowerInvariant()}");

		// The test room, with B in it too.
		string room = "nat-team@" + chat.MucService(a.Profile.Id);
		c.JoinAccount = c.AccountChoices.First(x => x.Id == a.Profile.Id);
		c.JoinRoom = "nat-team";
		c.JoinNick = "Mille";
		await c.JoinRoomNowCommand.ExecuteAsync(null);
		await chat.JoinRoomAsync(b.Profile.Id, room, "Kim");
		await chat.SendAsync(b.Profile.Id, room, "Morning all. Deploying **3.11.2** to the lab Neurons at 10:00.");
		await chat.SendAsync(a.Profile.Id, room, "Good. I'll watch the Fleet view while it rolls out.");
		Assert.True(c.JoinError is null, c.JoinError);
		Assert.True(c.SelectedPerson?.IsRoom == true, "Selected: " + c.SelectedPerson?.BareJid + " rooms: " + string.Join(",", chat.Rooms.Select(r => r.RoomJid + "/" + r.Joined)));
		try
		{
			await Until(() => c.Messages.Count >= 2, 15000);
		}
		catch (TimeoutException)
		{
			Assert.Fail($"Room messages in engine A: {chat.GetRoom(a.Profile.Id, room).Messages.Count}, B: {chat.GetRoom(b.Profile.Id, room).Messages.Count}; shown: {c.Messages.Count}; selected {c.SelectedPerson?.BareJid} vs {room}; " +
				string.Join(" | ", chat.GetRoom(a.Profile.Id, room).Messages.Select(m => m.Sender + ":" + m.Markdown + ":" + m.Error)));
		}
		await Task.Delay(500);
		Save(window, $"chat-room-{theme.ToLowerInvariant()}");

		// Share from another workspace: Chat opens with the share banner.
		vm.Chat.BeginShare("Issue: Unable to upload backup", "**Issue:** Unable to upload backup\n\n| | |\n|:--|:--|\n| Severity | Error |\n| Occurrences | 3 |");
		vm.Workspace = WorkspaceKind.Chat;
		await Task.Delay(300);
		Save(window, $"chat-share-{theme.ToLowerInvariant()}");
		c.CancelShareCommand.Execute(null);

		// Raw console: disco info of the server.
		vm.ShowAccountsCommand.Execute(null);
		vm.SelectedAccount = vm.Accounts.First(x => x.Session == a);
		vm.SelectedAccount.ToggleRawCommand.Execute(null);
		await vm.SelectedAccount.SendRawCommand.ExecuteAsync(null);
		await Task.Delay(300);
		Assert.Contains("identity", vm.SelectedAccount.RawResult);
		Save(window, $"accounts-raw-{theme.ToLowerInvariant()}");

		await chat.LeaveRoomAsync(b.Profile.Id, room);
		await chat.LeaveRoomAsync(a.Profile.Id, room);
		window.Close();
	}

	private static void Save(MainWindow window, string name)
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
}
