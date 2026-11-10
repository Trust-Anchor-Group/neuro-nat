using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Devices;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Provisioning;
using Waher.Networking.XMPP.Provisioning.Events;
using Xunit.Abstractions;

namespace Neuro.AA.Engine.Tests;

/// <summary>
/// Devices against the local test Neuron. The second test account plays a device: it registers
/// in the thing registry with a secret key, the first account claims it, the device asks whether
/// someone may be its friend, the owner answers through Neuro.AA, and the owner gives it up again.
/// </summary>
[Collection("Live Neuron")]
public class LiveDevicesTests(ITestOutputHelper output)
{
	[Fact]
	public async Task ClaimAnswerAndDisown()
	{
		string? host = Environment.GetEnvironmentVariable("NEUROAA_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NEUROAA_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NEUROAA_TEST_PASSWORD");
		string? user2 = Environment.GetEnvironmentVariable("NEUROAA_TEST_USER2");
		string? password2 = Environment.GetEnvironmentVariable("NEUROAA_TEST_PASSWORD2");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(user2) || string.IsNullOrEmpty(password2))
		{
			output.WriteLine("Not run: NEUROAA_TEST_* not set.");
			return;
		}

		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "NeuroAA.Live." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession owner = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);
		// The device is not one of Neuro.AA's accounts here: it has its own registry client.
		await using AccountManager deviceManager = new(new JsonAccountStore(Path.Combine(dir, "device.json")), new InMemoryCredentialVault());
		AccountSession device = await deviceManager.AddAsync(new AccountProfile { Host = host, UserName = user2, TrustServerCertificate = true }, AccountSecret.FromPassword(password2), connect: false);
		await using DevicesService devices = new(manager);
		await owner.ConnectAsync();
		await device.ConnectAsync();
		await StreamSnifferTests.WaitFor(() => devices.IsAvailable(owner.Profile.Id) || devices.Problem(owner.Profile.Id) is not null, 30000);
		output.WriteLine($"Available: {devices.IsAvailable(owner.Profile.Id)} {devices.Problem(owner.Profile.Id)}");
		Assert.True(devices.IsAvailable(owner.Profile.Id));

		// The "device" registers itself, unclaimed, with a key only its owner knows.
		XmppClient dc = device.Client!;
		string registry = (await dc.ServiceItemsDiscoveryAsync(dc.Domain)).Items.First(i => i.JID.StartsWith("provisioning", StringComparison.OrdinalIgnoreCase)).JID;
		using ThingRegistryClient deviceRegistry = new(dc, registry);
		string key = Guid.NewGuid().ToString("N")[..12];
		string serial = "NAA-" + Guid.NewGuid().ToString("N")[..6];
		TaskCompletionSource<RegistrationEventArgs> registered = new();
		await deviceRegistry.RegisterThing(false, [new MetaDataStringTag("KEY", key), new MetaDataStringTag("MAN", "Neuro.AA"), new MetaDataStringTag("MODEL", "Test meter"), new MetaDataStringTag("SN", serial)],
			(_, e) => { registered.TrySetResult(e); return Task.CompletedTask; }, null);
		RegistrationEventArgs reg = await registered.Task.WaitAsync(TimeSpan.FromSeconds(20));
		output.WriteLine($"Registered: ok {reg.Ok} claimed {reg.IsClaimed} owner {reg.OwnerJid} {reg.ErrorText}");
		Assert.True(reg.Ok, reg.ErrorText);

		if (reg.IsClaimed)
		{
			// Left over from an earlier run: give it up and register again.
			await devices.DisownAsync(owner.Profile.Id, new RegistryThing(device.BareJid!, null, null, []));
			registered = new();
			await deviceRegistry.RegisterThing(false, [new MetaDataStringTag("KEY", key), new MetaDataStringTag("MAN", "Neuro.AA"), new MetaDataStringTag("MODEL", "Test meter"), new MetaDataStringTag("SN", serial)],
				(_, e) => { registered.TrySetResult(e); return Task.CompletedTask; }, null);
			reg = await registered.Task.WaitAsync(TimeSpan.FromSeconds(20));
			output.WriteLine($"Registered again: ok {reg.Ok} claimed {reg.IsClaimed}");
		}

		// Claim it with the key.
		RegistryThing claimed = await devices.ClaimAsync(owner.Profile.Id,
			[new("KEY", key), new("MAN", "Neuro.AA"), new("MODEL", "Test meter"), new("SN", serial)], makePublic: false);
		output.WriteLine($"Claimed: {claimed.Jid} {claimed.Label}");

		IReadOnlyList<RegistryThing> mine = await devices.MyDevicesAsync(owner.Profile.Id);
		output.WriteLine("Mine: " + string.Join(", ", mine.Select(t => $"{t.Jid} {t.Label} [{string.Join(";", t.Tags.Select(x => x.Key + "=" + x.Value))}]")));
		Assert.Contains(mine, t => string.Equals(t.Jid, device.BareJid, StringComparison.OrdinalIgnoreCase));

		// The device asks whether someone may be its friend; the owner is asked.
		using ProvisioningClient deviceProvisioning = new(dc, registry);
		TaskCompletionSource<IsFriendResponseEventArgs> answered = new();
		await deviceProvisioning.IsFriend("visitor@" + dc.Domain, (_, e) => { answered.TrySetResult(e); return Task.CompletedTask; }, null);
		await StreamSnifferTests.WaitFor(() => devices.Questions.Any(q => q.AccountId == owner.Profile.Id && q.Kind == DeviceQuestionKind.Friendship), 20000);
		DeviceQuestion question = devices.Questions.First(q => q.Kind == DeviceQuestionKind.Friendship);
		output.WriteLine($"Question: {question.Kind} {question.DeviceJid} wants {question.RemoteJid}");
		await devices.AnswerAsync(question, accept: false, AnswerScope.Caller);
		IsFriendResponseEventArgs verdict = await answered.Task.WaitAsync(TimeSpan.FromSeconds(20));
		output.WriteLine($"Device told: friend {verdict.Friend}");
		Assert.False(verdict.Friend);
		Assert.Empty(devices.Questions);

		// Give it up again and remove the test registration.
		await devices.DisownAsync(owner.Profile.Id, mine.First(t => string.Equals(t.Jid, device.BareJid, StringComparison.OrdinalIgnoreCase)));
		TaskCompletionSource<bool> gone = new();
		await deviceRegistry.Unregister((_, e) => { gone.TrySetResult(e.Ok); return Task.CompletedTask; }, null);
		output.WriteLine($"Unregistered: {await gone.Task.WaitAsync(TimeSpan.FromSeconds(20))}");
		Assert.DoesNotContain(await devices.MyDevicesAsync(owner.Profile.Id), t => string.Equals(t.Jid, device.BareJid, StringComparison.OrdinalIgnoreCase));
	}
}
