using System.Security.Cryptography;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Storage;
using Waher.Networking.XMPP;
using Waher.Persistence.Files;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// Only contacts that answered that they are Neurons are listed as Neurons, the answer is kept
/// across restarts, and the user's own addresses never are. A "no" is asked again each time the
/// contact comes online. No live Neuron: the answer to the service discovery question is given
/// by the test.
/// </summary>
public class KnownNeuronTests : IDisposable
{
	private const string Kim = "kim@lab.local";
	private const string Gamma = "gamma.lab.local";

	private readonly string folder = Path.Combine(Path.GetTempPath(), "Nat.Known." + Guid.NewGuid().ToString("N"));
	private readonly List<MonitorService> services = [];

	public KnownNeuronTests() => EngineRuntime.Initialize();

	public void Dispose()
	{
		foreach (MonitorService m in this.services)
			m.DisposeAsync().AsTask().GetAwaiter().GetResult();

		try
		{
			if (Directory.Exists(this.folder))
				Directory.Delete(this.folder, true);
		}
		catch (IOException)
		{
		}
	}

	private async Task<(AccountManager Manager, Guid Account)> AccountsAsync()
	{
		AccountManager manager = new(new JsonAccountStore(Path.Combine(this.folder, "accounts.json")), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		return (manager, s.Profile.Id);
	}

	private NeuronMonitor Start(AccountManager manager, Guid account, IKnownNeuronStore store, Func<string, bool?> answer, List<string>? asked = null)
	{
		MonitorService m = new(manager, new EventHub(), known: store);
		this.services.Add(m);
		NeuronMonitor monitor = m.For(account)!;
		monitor.Discover = jid =>
		{
			asked?.Add(jid);
			return Task.FromResult(answer(jid));
		};
		return monitor;
	}

	private async Task<FilesProvider> OpenDatabaseAsync()
	{
		DatabaseOpening o = await LocalDatabase.OpenAsync(Path.Combine(this.folder, "database"), RandomNumberGenerator.GetBytes(32));
		Assert.True(o.IsOpen, o.Problem + " " + o.Error);
		return o.Database!;
	}

	/// <summary>Answers are written in the background; waits until the store has this one.</summary>
	private static async Task WaitForAnswerAsync(IKnownNeuronStore store, Guid account, string jid, bool expected)
	{
		for (int i = 0; i < 100; i++)
		{
			if (store.Load(account).TryGetValue(jid, out bool v) && v == expected)
				return;
			await Task.Delay(50, TestContext.Current.CancellationToken);
		}

		Assert.Fail($"The answer for {jid} was not kept.");
	}

	[Fact]
	public async Task OwnAddressesAreNeverNeurons()
	{
		var (manager, account) = await this.AccountsAsync();
		await manager.AddAsync(new AccountProfile { Host = "prod.example", UserName = "admin", ConnectOnStartup = false }, AccountSecret.FromPassword("y"), connect: false);
		List<string> asked = [];
		NeuronMonitor monitor = this.Start(manager, account, new InMemoryKnownNeuronStore(), _ => true, asked);

		// The account's own address, and another of the user's accounts, both in the roster and online.
		monitor.RosterItemChanged("ops@lab.local", SubscriptionState.Both, "Me");
		monitor.RosterItemChanged("admin@prod.example", SubscriptionState.Both, "My other account");
		await monitor.PresenceAsync("ops@lab.local", "ops@lab.local/phone", true);
		await monitor.PresenceAsync("admin@prod.example", "admin@prod.example/nat", true);
		monitor.Want("ops@lab.local");

		Assert.Empty(monitor.Neurons);
		Assert.Empty(asked);
		Assert.False(await monitor.IsNeuronSenderAsync("admin@prod.example/nat"));
	}

	[Fact]
	public async Task CoworkerNeverSeenOnlineIsNotListed()
	{
		var (manager, account) = await this.AccountsAsync();
		NeuronMonitor monitor = this.Start(manager, account, new InMemoryKnownNeuronStore(), _ => false);

		monitor.RosterItemChanged(Kim, SubscriptionState.Both, "Kim");
		Assert.Empty(monitor.Neurons);

		monitor.ConnectionLost();
		Assert.Empty(monitor.Neurons);
	}

	[Fact]
	public async Task CoworkerWhoAnsweredNoIsNotListedEvenAfterARestart()
	{
		var (manager, account) = await this.AccountsAsync();
		FilesProvider db = await this.OpenDatabaseAsync();
		try
		{
			DatabaseKnownNeuronStore store = new(db);
			NeuronMonitor monitor = this.Start(manager, account, store, _ => false);

			monitor.RosterItemChanged(Kim, SubscriptionState.Both, "Kim");
			await monitor.PresenceAsync(Kim, Kim + "/phone", true);
			Assert.Empty(monitor.Neurons);

			// Gone offline, then the account reconnects: still not a Neuron.
			await monitor.PresenceAsync(Kim, Kim + "/phone", false);
			monitor.ConnectionLost();
			Assert.Empty(monitor.Neurons);

			ChatService chat = new(manager, this.services[^1]);
			Assert.False(chat.IsNeuron(account, Kim));

			await WaitForAnswerAsync(store, account, Kim, false);

			// A restart: the answer is remembered, so Kim is not listed before or while being asked
			// again, and is asked once, not at every presence.
			List<string> asked = [];
			NeuronMonitor again = this.Start(manager, account, new DatabaseKnownNeuronStore(db), _ => false, asked);
			again.RosterItemChanged(Kim, SubscriptionState.Both, "Kim");
			Assert.Empty(again.Neurons);
			await again.PresenceAsync(Kim, Kim + "/laptop", true);
			await again.PresenceAsync(Kim, Kim + "/laptop", true);
			Assert.Empty(again.Neurons);
			Assert.Equal([Kim + "/laptop"], asked);
			Assert.False(await again.IsNeuronSenderAsync(Kim + "/laptop"));
		}
		finally
		{
			await db.Stop();
		}
	}

	[Fact]
	public async Task NeuronThatAnsweredNoTooEarlyIsAskedAgain()
	{
		var (manager, account) = await this.AccountsAsync();
		FilesProvider db = await this.OpenDatabaseAsync();
		try
		{
			// Gamma is asked while it starts, before it can say that it is a Neuron.
			bool ready = false;
			List<string> asked = [];
			DatabaseKnownNeuronStore store = new(db);
			NeuronMonitor monitor = this.Start(manager, account, store, _ => ready, asked);

			monitor.RosterItemChanged(Gamma, SubscriptionState.Both, "Gamma");
			await monitor.PresenceAsync(Gamma, Gamma + "/neuron", true);
			Assert.Empty(monitor.Neurons);
			await WaitForAnswerAsync(store, account, Gamma, false);

			// More presence from the same client is not a reason to ask again.
			ready = true;
			await monitor.PresenceAsync(Gamma, Gamma + "/neuron", true);
			Assert.Empty(monitor.Neurons);
			Assert.Single(asked);

			// It went offline and came back: asked again, and listed now.
			await monitor.PresenceAsync(Gamma, Gamma + "/neuron", false);
			await monitor.PresenceAsync(Gamma, Gamma + "/neuron", true);
			Assert.Equal(2, asked.Count);
			Assert.Equal(Gamma, Assert.Single(monitor.Neurons).BareJid);
			await WaitForAnswerAsync(store, account, Gamma, true);
		}
		finally
		{
			await db.Stop();
		}
	}

	[Fact]
	public async Task StoredNoIsAskedAgainAfterARestartAndAReconnect()
	{
		var (manager, account) = await this.AccountsAsync();
		InMemoryKnownNeuronStore store = new();
		await store.SetAsync(account, Gamma, false);

		// A restart with the old "no" in the store: nothing is listed, and Gamma still says no.
		bool ready = false;
		List<string> asked = [];
		NeuronMonitor monitor = this.Start(manager, account, store, _ => ready, asked);
		monitor.RosterItemChanged(Gamma, SubscriptionState.Both, "Gamma");
		await monitor.PresenceAsync(Gamma, Gamma + "/neuron", true);
		Assert.Empty(monitor.Neurons);
		Assert.Single(asked);

		// The account reconnects and sees Gamma online again: asked again, and it is a Neuron.
		ready = true;
		monitor.ConnectionLost();
		Assert.Empty(monitor.Neurons);
		await monitor.PresenceAsync(Gamma, Gamma + "/neuron", true);
		Assert.Equal(2, asked.Count);
		Assert.Equal(Gamma, Assert.Single(monitor.Neurons).BareJid);
		Assert.True(store.Load(account)[Gamma]);
	}

	[Fact]
	public async Task KnownNeuronDownAtStartIsListedAsOffline()
	{
		var (manager, account) = await this.AccountsAsync();
		FilesProvider db = await this.OpenDatabaseAsync();
		try
		{
			DatabaseKnownNeuronStore store = new(db);
			NeuronMonitor monitor = this.Start(manager, account, store, jid => jid.StartsWith(Gamma, StringComparison.Ordinal));

			monitor.RosterItemChanged(Gamma, SubscriptionState.Both, "Gamma");
			Assert.Empty(monitor.Neurons);   // Not until it has answered.

			await monitor.PresenceAsync(Gamma, Gamma + "/neuron", true);
			Assert.Equal(Gamma, Assert.Single(monitor.Neurons).BareJid);

			await WaitForAnswerAsync(store, account, Gamma, true);

			// A restart while Gamma is down: it shows at once, as offline.
			NeuronMonitor again = this.Start(manager, account, new DatabaseKnownNeuronStore(db), _ => null);
			WatchedNeuron n = Assert.Single(again.Neurons);
			Assert.Equal(Gamma, n.BareJid);
			Assert.Equal(WatchState.Offline, n.State);

			// The roster arrives and the account reconnects: still listed, still offline.
			again.RosterItemChanged(Gamma, SubscriptionState.Both, "Gamma");
			again.ConnectionLost();
			n = Assert.Single(again.Neurons);
			Assert.Equal(WatchState.Offline, n.State);
			Assert.Equal("Gamma", n.Label);
		}
		finally
		{
			await db.Stop();
		}
	}

	[Fact]
	public async Task NeuronAddedOnPurposeWaitsUntilItAnswers()
	{
		var (manager, account) = await this.AccountsAsync();
		NeuronMonitor monitor = this.Start(manager, account, new InMemoryKnownNeuronStore(), _ => false);

		monitor.Want(Gamma);
		Assert.Equal(WatchState.Waiting, Assert.Single(monitor.Neurons).State);

		// It answers that it is not a Neuron after all.
		await monitor.PresenceAsync(Gamma, Gamma + "/x", true);
		Assert.Empty(monitor.Neurons);
	}

	[Fact]
	public async Task DatabaseKeepsOneAnswerPerContact()
	{
		FilesProvider db = await this.OpenDatabaseAsync();
		try
		{
			Guid account = Guid.NewGuid();
			Guid other = Guid.NewGuid();
			DatabaseKnownNeuronStore store = new(db);

			await store.SetAsync(account, "Gamma.Lab.Local", true);
			await store.SetAsync(account, Kim, true);
			await store.SetAsync(account, Kim, false);
			await store.SetAsync(other, Kim, true);

			IReadOnlyDictionary<string, bool> back = new DatabaseKnownNeuronStore(db).Load(account);
			Assert.Equal(2, back.Count);
			Assert.True(back[Gamma]);
			Assert.False(back[Kim]);
			Assert.True(store.Load(other)[Kim]);

			await store.ForgetAsync(account, Kim);
			Assert.False(store.Load(account).ContainsKey(Kim));
			Assert.True(store.Load(other).ContainsKey(Kim));
		}
		finally
		{
			await db.Stop();
		}
	}
}
