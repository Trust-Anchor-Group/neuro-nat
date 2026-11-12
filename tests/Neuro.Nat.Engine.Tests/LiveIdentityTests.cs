using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Identity;
using Waher.Networking.XMPP.Contracts;
using Waher.Security;
using Xunit.Abstractions;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// Legal identities against the local test Neuron: the identity service is found, keys are made,
/// an application shows up as pending on the Neuron, and is then obsoleted again.
/// </summary>
[Collection("Live Neuron")]
public class LiveIdentityTests(ITestOutputHelper output)
{
	[Fact]
	public async Task ApplyListPendingAndObsolete()
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER2");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD2");
		string? admin = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? adminPassword = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? neuron = Environment.GetEnvironmentVariable("NAT_TEST_NEURON");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(admin) || string.IsNullOrEmpty(neuron))
		{
			output.WriteLine("Not run: NAT_TEST_* not set.");
			return;
		}

		EngineRuntime.Initialize();
		System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Live." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);
		AccountSession op = await manager.AddAsync(new AccountProfile { Host = host, UserName = admin, TrustServerCertificate = true }, AccountSecret.FromPassword(adminPassword!), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		await using ChatService chat = new(manager, monitors);
		await using IdentityService ids = new(manager, chat);
		await s.ConnectAsync();
		await op.ConnectAsync();

		await StreamSnifferTests.WaitFor(() => ids.IsAvailable(s.Profile.Id) || ids.Problem(s.Profile.Id) is not null, 30000);
		output.WriteLine($"Available: {ids.IsAvailable(s.Profile.Id)} {ids.Problem(s.Profile.Id)}");
		Assert.True(ids.IsAvailable(s.Profile.Id));

		IReadOnlyList<LegalIdentity> before = await ids.GetIdentitiesAsync(s.Profile.Id);
		output.WriteLine($"Before: {before.Count} identities: " + string.Join(", ", before.Select(i => $"{i.Id} {i.State}")));

		LegalIdentity applied = await ids.ApplyAsync(s.Profile.Id,
		[
			new Property("FIRST", "Kim"),
			new Property("LAST", "Lindqvist"),
			new Property("COUNTRY", "SE"),
		]);
		output.WriteLine($"Applied: {applied.Id} {applied.State}, from {applied.From:yyyy-MM-dd} to {applied.To:yyyy-MM-dd} ({sw.Elapsed.TotalSeconds:F1} s)");
		Assert.Equal(IdentityState.Created, applied.State);

		IReadOnlyList<LegalIdentity> after = await ids.GetIdentitiesAsync(s.Profile.Id);
		Assert.Contains(after, i => i.Id == applied.Id);

		// The operator sees it pending on the Neuron, and the Neuron's own identity.
		await StreamSnifferTests.WaitFor(() => monitors.For(op.Profile.Id)?.Concentrator(neuron) is not null, 30000);
		IReadOnlyList<PendingApplication>? pending = await ids.PendingApplicationsAsync(op.Profile.Id, neuron);
		output.WriteLine($"Pending after {sw.Elapsed.TotalSeconds:F1} s: " + string.Join("; ", pending?.Select(p => $"{p.Id} {p.Account} {p.Created} {p.Name} {p.Country}") ?? ["no answer"]));
		Assert.NotNull(pending);
		PendingApplication mine = Assert.Single(pending, p => p.Id == applied.Id);
		Assert.Equal("Kim Lindqvist", mine.Name);
		Assert.Equal("SE", mine.Country);
		Assert.NotNull(mine.Created);

		NeuronIdentity nid = await ids.NeuronIdentityAsync(op.Profile.Id, neuron);
		output.WriteLine($"Neuron identity: {nid.Id ?? "none"} expires {nid.Expires} {nid.Error} ({sw.Elapsed.TotalSeconds:F1} s)");
		Assert.True(nid.Id is null || !nid.Id.Contains('|'), nid.Id);

		// Retiring an application the operators have not approved yet rejects it.
		LegalIdentity obsoleted = await ids.ObsoleteAsync(s.Profile.Id, applied.Id);
		output.WriteLine($"Obsoleted: {obsoleted.State} ({sw.Elapsed.TotalSeconds:F1} s)");
		Assert.True(obsoleted.State is IdentityState.Obsoleted or IdentityState.Rejected, obsoleted.State.ToString());
	}
}
