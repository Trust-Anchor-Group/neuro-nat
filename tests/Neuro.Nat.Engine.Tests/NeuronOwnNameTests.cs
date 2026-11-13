using System.Security.Cryptography;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Storage;
using Neuro.Nat.Engine.Topology;
using Waher.Networking.XMPP;
using Waher.Persistence.Files;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// A Neuron goes by what it calls itself, not by the domain of its client's address: that client
/// is often an account on the Neuron's parent broker. No live Neuron: the tests give the answers.
/// </summary>
public class NeuronOwnNameTests : IDisposable
{
	/// <summary>The child Neuron's own client: an account on its parent broker.</summary>
	private const string LabClient = "lab-neuron@example.com";
	private const string LabName = "lab.example.com";
	private const string ParentGraph = "```dot\ndigraph G {\nrankdir=LR\n\"example.com\" -> \"lab.example.com\" [dir=back];\n}\n```\n";

	private readonly string folder = Path.Combine(Path.GetTempPath(), "Nat.OwnName." + Guid.NewGuid().ToString("N"));
	private readonly List<MonitorService> services = [];

	public NeuronOwnNameTests() => EngineRuntime.Initialize();

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
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = LabName, UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		return (manager, s.Profile.Id);
	}

	private MonitorService Start(AccountManager manager, IKnownNeuronStore store)
	{
		MonitorService m = new(manager, new EventHub(), known: store);
		this.services.Add(m);
		return m;
	}

	private static async Task SeenAsNeuronAsync(NeuronMonitor monitor, string jid)
	{
		monitor.Discover = _ => Task.FromResult<bool?>(true);
		monitor.RosterItemChanged(jid, SubscriptionState.Both, null);
		await monitor.PresenceAsync(jid, jid + "/neuron", true);
	}

	private static async Task WaitForAsync(Func<bool> done, string what)
	{
		for (int i = 0; i < 100; i++)
		{
			if (done())
				return;
			await Task.Delay(50, TestContext.Current.CancellationToken);
		}

		Assert.Fail(what);
	}

	[Fact]
	public void ParentGraphNamesTheNeuronThatAnswered()
	{
		Assert.Equal(LabName, TopologyService.OwnName(ParentGraph));

		// Not a graph, nothing named, or a name no domain or account could be: no name.
		Assert.Null(TopologyService.OwnName(null));
		Assert.Null(TopologyService.OwnName("Unknown command."));
		Assert.Null(TopologyService.OwnName("```dot\ndigraph G {\n}\n```"));
		Assert.Null(TopologyService.OwnName("```dot\ndigraph G {\n\"a.example\" -> \"two words\" [dir=back];\n}\n```"));
	}

	[Fact]
	public void RosterNameThenOwnNameThenDomain()
	{
		Assert.Equal("Lab", NeuronNames.Of("Lab", LabName, LabClient));
		Assert.Equal(LabName, NeuronNames.Of(null, LabName, LabClient));
		Assert.Equal(LabName, NeuronNames.Of(" ", LabName, LabClient));
		Assert.Equal("example.com", NeuronNames.Of(null, null, LabClient));
		Assert.Equal("example.com", NeuronNames.Of(null, " ", LabClient));
	}

	[Fact]
	public void TwoChildrenOfOneParentGetTheirOwnNames()
	{
		Guid account = Guid.NewGuid();
		WatchedNeuron lab = new() { AccountId = account, BareJid = LabClient, OwnName = LabName };
		WatchedNeuron edge = new() { AccountId = account, BareJid = "edge-neuron@example.com", OwnName = "edge.example.com" };
		WatchedNeuron parent = new() { AccountId = account, BareJid = "neuron@example.org", OwnName = "example.com" };

		IReadOnlyDictionary<string, string> names = NeuronNames.Distinct([lab, edge, parent]);
		Assert.Equal(LabName, names[LabClient]);
		Assert.Equal("edge.example.com", names[edge.BareJid]);
		Assert.Equal("example.com", names[parent.BareJid]);
		Assert.Equal(LabName, NeuronNames.For([lab, edge, parent], LabClient));

		// Before they have said what they call themselves, both children would be "example.com": they show their addresses.
		IReadOnlyDictionary<string, string> unnamed = NeuronNames.Distinct([lab with { OwnName = null }, edge with { OwnName = null }]);
		Assert.Equal(LabClient, unnamed[LabClient]);
		Assert.Equal(edge.BareJid, unnamed[edge.BareJid]);

		// A roster name still wins, also when the same Neuron is seen through another account without one.
		WatchedNeuron other = lab with { AccountId = Guid.NewGuid() };
		Assert.Equal("Lab", NeuronNames.Distinct([other, lab with { Name = "Lab" }])[LabClient]);
	}

	[Fact]
	public async Task NeuronIsListedUnderWhatItCallsItselfAlsoAfterARestart()
	{
		var (manager, account) = await this.AccountsAsync();
		DatabaseOpening o = await LocalDatabase.OpenAsync(Path.Combine(this.folder, "database"), RandomNumberGenerator.GetBytes(32));
		Assert.True(o.IsOpen, o.Problem + " " + o.Error);
		FilesProvider db = o.Database!;
		try
		{
			DatabaseKnownNeuronStore store = new(db);
			NeuronMonitor monitor = this.Start(manager, store).For(account)!;
			await SeenAsNeuronAsync(monitor, LabClient);
			Assert.Equal("example.com", Assert.Single(monitor.Neurons).Label);   // The parent's domain, until it has said.

			int changes = 0;
			monitor.NeuronsChanged += (_, _) => changes++;
			monitor.SetOwnName(LabClient, " " + LabName + " ");
			WatchedNeuron n = Assert.Single(monitor.Neurons);
			Assert.Equal(LabName, n.OwnName);
			Assert.Equal(LabName, n.Label);
			Assert.Equal(1, changes);

			// The same name again changes nothing; someone who is not followed gets no name.
			monitor.SetOwnName(LabClient, LabName);
			monitor.SetOwnName("stranger@example.net", "stranger.example.net");
			Assert.Equal(1, changes);
			Assert.Single(monitor.Neurons);

			await WaitForAsync(() => new DatabaseKnownNeuronStore(db).LoadNames(account).TryGetValue(LabClient, out string? kept) && kept == LabName, "The name was not kept.");

			// A restart while the Neuron is down: listed at once, offline, under its own name.
			NeuronMonitor again = this.Start(manager, new DatabaseKnownNeuronStore(db)).For(account)!;
			n = Assert.Single(again.Neurons);
			Assert.Equal(WatchState.Offline, n.State);
			Assert.Equal(LabName, n.Label);

			// The roster gives it a name: that one wins. Removed from the roster: the name is forgotten too.
			again.RosterItemChanged(LabClient, SubscriptionState.Both, "Lab");
			Assert.Equal("Lab", Assert.Single(again.Neurons).Label);
			again.RosterItemRemoved(LabClient);
			await WaitForAsync(() => !new DatabaseKnownNeuronStore(db).LoadNames(account).ContainsKey(LabClient), "The name was not forgotten.");
		}
		finally
		{
			await db.Stop();
		}
	}

	[Fact]
	public async Task LiveNeuronIsAskedOncePerStreamWhatItCallsItself()
	{
		var (manager, account) = await this.AccountsAsync();
		MonitorService monitors = this.Start(manager, new InMemoryKnownNeuronStore());
		NeuronMonitor monitor = monitors.For(account)!;
		await using ChatService chat = new(manager, monitors);

		List<string> asked = [];
		string? answer = "Unknown command.";
		chat.AskParentGraph = n =>
		{
			lock (asked)
				asked.Add(n.BareJid);
			return Task.FromResult(answer);
		};

		// Not live yet: not asked.
		await SeenAsNeuronAsync(monitor, LabClient);
		Assert.Empty(asked);

		// Its events stream: asked. It does not know the command, so it keeps the stand-in name.
		DateTime first = new(2026, 11, 13, 10, 0, 0);
		monitor.Preview(new WatchedNeuron { AccountId = account, BareJid = LabClient, State = WatchState.Live, LiveSince = first });
		await WaitForAsync(() => asked.Count == 1, "The Neuron was not asked.");
		Assert.Equal("example.com", Assert.Single(monitor.Neurons).Label);

		// More news about the same stream is not a reason to ask again.
		monitor.Preview(new WatchedNeuron { AccountId = account, BareJid = LabClient, State = WatchState.Live, LiveSince = first });
		await Task.Delay(200, TestContext.Current.CancellationToken);
		Assert.Single(asked);

		// It came back with a new stream, and answers now: asked again, and renamed.
		answer = ParentGraph;
		monitor.Preview(new WatchedNeuron { AccountId = account, BareJid = LabClient, State = WatchState.Live, LiveSince = first.AddHours(1) });
		await WaitForAsync(() => monitor.Neurons.Any(n => n.Label == LabName), "The Neuron was not renamed.");
		Assert.Equal(2, asked.Count);
		Assert.True(chat.IsNeuron(account, LabClient));
	}
}
