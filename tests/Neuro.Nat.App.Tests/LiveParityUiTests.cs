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
using Neuro.Nat.Engine.Devices;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.PubSub;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Provisioning;
using Waher.Networking.XMPP.Provisioning.Events;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// Devices, Publish/subscribe and Updates against the local test Neuron, for review. The second
/// test account plays a device. Needs NAT_TEST_* (with USER2) and NAT_TEST_NEURON.
/// </summary>
[Collection("Live Neuron")]
public class LiveParityUiTests
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
	public async Task DevicesPubSubAndUpdates(string theme)
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? user2 = Environment.GetEnvironmentVariable("NAT_TEST_USER2");
		string? password2 = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD2");
		string? neuron = Environment.GetEnvironmentVariable("NAT_TEST_NEURON");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(user2) || string.IsNullOrEmpty(password2) || string.IsNullOrEmpty(neuron))
			return;

		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.LiveUi." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession owner = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true, ConnectOnStartup = false },
			AccountSecret.FromPassword(password), connect: false);
		await using AccountManager deviceManager = new(new JsonAccountStore(Path.Combine(dir, "device.json")), new InMemoryCredentialVault());
		AccountSession device = await deviceManager.AddAsync(new AccountProfile { Host = host, UserName = user2, TrustServerCertificate = true, ConnectOnStartup = false },
			AccountSecret.FromPassword(password2), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		await using ChatService chat = new(manager, monitors);
		await using DevicesService devices = new(manager);
		await using PubSubService pubsub = new(manager);
		MainWindowViewModel vm = new(manager, new FleetService(monitors), chat, null, devices, pubsub);
		await vm.StartAsync();
		await owner.ConnectAsync();
		await device.ConnectAsync();
		await Until(() => devices.IsAvailable(owner.Profile.Id) && pubsub.IsAvailable(owner.Profile.Id), 40000);

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();

		// A device registers; the owner claims it through the screen.
		XmppClient dc = device.Client!;
		using ThingRegistryClient deviceRegistry = new(dc, "provisioning." + dc.Domain);
		using ProvisioningClient deviceProvisioning = new(dc, "provisioning." + dc.Domain);
		string key = Guid.NewGuid().ToString("N")[..12];
		TaskCompletionSource<RegistrationEventArgs> registered = new();
		await deviceRegistry.RegisterThing(false, [new MetaDataStringTag("KEY", key), new MetaDataStringTag("MAN", "NAT"), new MetaDataStringTag("MODEL", "Meter 3"),
			new MetaDataStringTag("SN", "GBG-0042"), new MetaDataStringTag("ROOM", "Plant room")], (_, e) => { registered.TrySetResult(e); return Task.CompletedTask; }, null);
		RegistrationEventArgs reg = await registered.Task.WaitAsync(TimeSpan.FromSeconds(20));
		if (reg.IsClaimed)
		{
			await devices.DisownAsync(owner.Profile.Id, new RegistryThing(device.BareJid!, null, null, []));
			registered = new();
			await deviceRegistry.RegisterThing(false, [new MetaDataStringTag("KEY", key), new MetaDataStringTag("MAN", "NAT"), new MetaDataStringTag("MODEL", "Meter 3"),
				new MetaDataStringTag("SN", "GBG-0042"), new MetaDataStringTag("ROOM", "Plant room")], (_, e) => { registered.TrySetResult(e); return Task.CompletedTask; }, null);
			await registered.Task.WaitAsync(TimeSpan.FromSeconds(20));
		}

		vm.ShowDevicesCommand.Execute(null);
		DevicesViewModel d = vm.Devices;
		d.StartClaimCommand.Execute(null);
		d.ClaimTags = $"KEY={key}\nMAN=NAT\nMODEL=Meter 3\nSN=GBG-0042\nROOM=Plant room";
		await Task.Delay(200);
		Save(window, $"devices-claim-{theme.ToLowerInvariant()}");
		await d.ClaimCommand.ExecuteAsync(null);
		Assert.True(d.Error is null, d.Error);
		await Until(() => d.Rows.Any(r => r.Thing?.Jid == device.BareJid));
		d.Selected = d.Rows.First(r => r.Thing?.Jid == device.BareJid);
		await Task.Delay(200);
		Save(window, $"devices-mine-{theme.ToLowerInvariant()}");

		// The device asks to read for someone; the question waits in the screen.
		TaskCompletionSource<IsFriendResponseEventArgs> told = new();
		await deviceProvisioning.IsFriend("visitor@" + dc.Domain, (_, e) => { told.TrySetResult(e); return Task.CompletedTask; }, null);
		await Until(() => d.QuestionCount > 0, 20000);
		d.SetModeCommand.Execute(DevicesMode.Questions);
		d.Selected = d.Rows.First();
		await Task.Delay(200);
		Save(window, $"devices-question-{theme.ToLowerInvariant()}");
		await d.AnswerCommand.ExecuteAsync("reject");
		Assert.False((await told.Task.WaitAsync(TimeSpan.FromSeconds(20))).Friend);

		d.SetModeCommand.Execute(DevicesMode.Mine);
		await Until(() => d.Rows.Any(r => r.Thing?.Jid == device.BareJid));
		d.Selected = d.Rows.First(r => r.Thing?.Jid == device.BareJid);
		d.AskDisownCommand.Execute(null);
		await d.DisownCommand.ExecuteAsync(null);
		TaskCompletionSource<bool> gone = new();
		await deviceRegistry.Unregister((_, e) => { gone.TrySetResult(e.Ok); return Task.CompletedTask; }, null);
		await gone.Task.WaitAsync(TimeSpan.FromSeconds(20));

		// Publish/subscribe: a node with two items.
		vm.ShowPubSubCommand.Execute(null);
		PubSubViewModel p = vm.PubSub;
		await Until(() => !p.IsBusy);
		string name = "plant-room-" + Guid.NewGuid().ToString("N")[..4];
		p.NewNodeName = name;
		await p.CreateNodeCommand.ExecuteAsync(null);
		Assert.True(p.Error is null, p.Error);
		await Until(() => !p.IsBusy && p.SelectedNode?.Node == name);
		p.PublishXml = "<temperature xmlns='urn:example:sensors' unit='°C' value='21.5' timestamp='2026-11-10T10:12:00Z'/>";
		await p.PublishCommand.ExecuteAsync(null);
		p.PublishXml = "<temperature xmlns='urn:example:sensors' unit='°C' value='21.9' timestamp='2026-11-10T10:13:00Z'/>";
		await p.PublishCommand.ExecuteAsync(null);
		await Until(() => !p.IsBusy && p.Items.Count == 2);
		await Task.Delay(200);
		Save(window, $"pubsub-{theme.ToLowerInvariant()}");
		Assert.True(p.EditorError is null, p.EditorError);
		p.AskDeleteCommand.Execute(null);
		p.Ask!.Input = name;
		await p.ConfirmAskCommand.ExecuteAsync(null);
		Assert.True(p.Ask is null, p.Ask?.Error);

		// Updates for the test Neuron.
		await Until(() => monitors.Neurons.Any(n => n.State == WatchState.Live), 30000);
		vm.ShowUpdatesCommand.Execute(null);
		await Until(() => vm.Updates.Selected?.Software is not null, 40000);
		await Task.Delay(300);
		Save(window, $"updates-live-{theme.ToLowerInvariant()}");

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
