using System.Security.Cryptography;
using System.Text;
using Neuro.Nat.Engine.Compare;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Links;
using Neuro.Nat.Engine.Software;
using Neuro.Nat.Engine.Storage;
using Waher.Persistence.Files;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// Comparing two Neurons: two stand-in Neurons with known differences, a Neuron against itself,
/// a password that differs but is never shown or kept, state now that never counts, kept copies
/// and comparing with them, the link, and a Neuron the account has no access to.
/// </summary>
public class CompareTests : IDisposable
{
	internal const string Lab = "lab.example.com";
	internal const string Prod = "prod.example.com";
	internal const string LabSecret = "Lab-mqtt-pw-8812-kiwi";
	internal const string ProdSecret = "Prod-mqtt-pw-4471-fig";

	private readonly string folder = Path.Combine(Path.GetTempPath(), "Nat.Compare." + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(this.folder))
				Directory.Delete(this.folder, true);
		}
		catch (IOException)
		{
		}
	}

	/// <summary>
	/// A stand-in Neuron, built with the same functions the live reader uses. Lab and Production
	/// differ in eight things: version, build time, automatic updates, TAG.Neuron's version,
	/// TAG.Identity only on Production, port 8080 only on Lab, port 443 only on Production, and
	/// the MQTT event sink's password.
	/// </summary>
	internal static NeuronSnapshot Snapshot(string jid, bool production = false, double cpu = 14, int? autoMinutes = null, DateTime? readAt = null)
	{
		List<SnapshotValue> v = [];
		NeuronSoftware sw = new()
		{
			AccountId = Guid.Empty,
			NeuronJid = jid,
			BuildTime = production ? "2026-11-10, 08:12:40" : "2026-10-28, 09:14:10",
			AutoDelayMinutes = autoMinutes ?? (production ? null : 15),
			ContentAutoInstall = false,
			Installed = production ? ["TAG.Neuron.package", "TAG.Payments.package", "TAG.Identity.package"] : ["TAG.Neuron.package", "TAG.Payments.package"],
			Packages = production
				?
				[
					new SoftwarePackage("TAG.Neuron.3.11.1.package", "48 MB", "2026-10-28", null, "2026-10-28", "2026-10-28 14:46", Lab),
					new SoftwarePackage("TAG.Neuron.3.11.2.package", "48 MB", "2026-11-10", null, "2026-11-10", "2026-11-10 10:31", Lab),
					new SoftwarePackage("TAG.Payments.2.4.0.package", "1 MB", "2026-10-02", null, "2026-10-02", "2026-10-02 10:05", Lab),
					new SoftwarePackage("TAG.Identity.1.2.0.package", "2 MB", "2026-09-01", null, "2026-09-01", "2026-09-01 10:05", Lab),
				]
				:
				[
					new SoftwarePackage("TAG.Neuron.3.11.1.package", "48 MB", "2026-10-28", null, "2026-10-28", "2026-10-28 14:46", Lab),
					new SoftwarePackage("TAG.Neuron.3.11.2.package", "48 MB", "2026-11-10", null, "2026-11-10", null, Lab),
					new SoftwarePackage("TAG.Payments.2.4.0.package", "1 MB", "2026-10-02", null, "2026-10-02", "2026-10-02 10:05", Lab),
				],
		};
		v.AddRange(LiveNeuronReader.SoftwareValues(sw, production ? "Waher.IoTGateway 3.11.2.0" : "Waher.IoTGateway 3.11.1.0"));

		v.AddRange(Port(5222, "XMPP.C2S"));
		v.AddRange(Port(5269, "XMPP.S2S"));
		v.AddRange(production ? Port(443, "HTTPS") : Port(8080, "HTTP"));

		v.AddRange(LiveNeuronReader.SettingValues("WebServer", "Web server", "WebServer", null,
			[("Http2Enabled", "Enabled", "true", false), ("ClientCertificates", "Client Certificates (mTLS):", "NotUsed", false)]));
		v.AddRange(LiveNeuronReader.SettingValues("WebServer", LiveNeuronReader.DomainsSection, "WebServer/www.example.com", "www.example.com",
			[("LocalDomain", "Local Domain:", "www.example.com", false), ("RemoteDomain", "Remote Domain:", "10.0.0.5", false)]));
		v.AddRange(LiveNeuronReader.SettingValues("LoginAuditor", "Sign-in guard", "LoginAuditor/1", "1",
			[("Attempts", "Number of attempts:", "5", false), ("Interval", "Interval Duration:", "PT1H", false)]));
		v.AddRange(LiveNeuronReader.SettingValues("EventSinks", "Where events are sent", "EventSinks/MqttSink", "MqttSink",
			[("Broker", "Broker:", "mqtt.example.com", false), ("Password", "Password:", production ? ProdSecret : LabSecret, false)]));

		v.AddRange(LiveNeuronReader.StateValues(new NeuronHealth
		{
			AccountId = Guid.Empty,
			NeuronJid = jid,
			Cpu = new SampleSeries(cpu, [cpu], "%"),
			AllocatedMemory = new SampleSeries(production ? 1800 : 640, [], "MB"),
			FreeDisk = new SampleSeries(production ? 40.2 : 182.4, [], "GB"),
		}, production ? "312" : "18"));

		return new NeuronSnapshot { NeuronJid = jid, Name = production ? "Production" : "Lab", ReadAt = readAt ?? DateTime.UtcNow, Values = v };

		static IEnumerable<SnapshotValue> Port(int n, string protocol)
			=> LiveNeuronReader.SettingValues("Ports", "Ports", "Ports/" + n, n.ToString(System.Globalization.CultureInfo.InvariantCulture),
				[("Protocol", "Protocol:", protocol, false), ("PortNumber", "Port Number:", n.ToString(System.Globalization.CultureInfo.InvariantCulture), false)]);
	}

	/// <summary>Hands out snapshots in place of the Neurons.</summary>
	internal sealed class StandInReader(params NeuronSnapshot[] snapshots) : INeuronReader
	{
		public Dictionary<string, NeuronSnapshot> Neurons { get; } = snapshots.ToDictionary(s => s.NeuronJid, StringComparer.OrdinalIgnoreCase);

		public int Reads { get; private set; }

		public Task<NeuronSnapshot> ReadAsync(Guid accountId, string neuronJid, CancellationToken cancel = default)
		{
			this.Reads++;
			return Task.FromResult(this.Neurons[neuronJid] with { ReadAt = DateTime.UtcNow });
		}
	}

	// ----- Two Neurons with known differences -----

	[Fact]
	public void TwoNeuronsShowTheirKnownDifferences()
	{
		Comparison c = NeuronComparer.Compare(Snapshot(Lab), Snapshot(Prod, production: true), "Lab", "Production");

		Assert.Equal(8, c.Differences);
		Assert.Equal(4, c.GroupsWithDifferences);
		Assert.Equal("8 differences in 4 groups", c.CountText);

		CompareRow Row(string label) => c.Rows.Single(r => r.Label == label);
		Assert.Equal(("Waher.IoTGateway 3.11.1.0", "Waher.IoTGateway 3.11.2.0", RowKind.Different), (Row("Version").Left, Row("Version").Right, Row("Version").Kind));
		Assert.Equal(("After 15 min", "Off"), (Row("Automatic updates").Left, Row("Automatic updates").Right));
		Assert.Equal(("3.11.1", "3.11.2"), (Row("TAG.Neuron").Left, Row("TAG.Neuron").Right));
		Assert.Equal(RowKind.Same, Row("TAG.Payments").Kind);
		Assert.Equal(RowKind.OnlyRight, Row("TAG.Identity").Kind);
		Assert.Equal(RowKind.OnlyLeft, Row("Port 8080").Kind);
		Assert.Equal("HTTP", Row("Port 8080").Left);
		Assert.Equal(RowKind.OnlyRight, Row("Port 443").Kind);
		Assert.Equal(RowKind.Same, Row("Port 5222").Kind);
		Assert.Equal(RowKind.Same, Row("www.example.com, Remote Domain").Kind);
		Assert.Equal(RowKind.Same, Row("1, Number of attempts").Kind);

		// Groups come in their order, and the headings in the order they were read.
		Assert.Equal([CompareGroup.Software, CompareGroup.Settings, CompareGroup.StateNow], c.Rows.Select(r => r.Group).Distinct());
		Assert.Equal(["Software", "Installed packages", "Ports", "Web server", "Domains", "Sign-in guard", "Where events are sent", "Now"], c.Rows.Select(r => r.Section).Distinct());

		string text = c.ToText(onlyDifferences: true);
		Assert.Contains("8 differences in 4 groups", text, StringComparison.Ordinal);
		Assert.Contains("Port 8080: HTTP (only on Lab)", text, StringComparison.Ordinal);
		Assert.Contains("TAG.Neuron: 3.11.1 | 3.11.2", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Port 5222", text, StringComparison.Ordinal);
		Assert.Contains("Port 5222", c.ToText(onlyDifferences: false), StringComparison.Ordinal);
		Assert.DoesNotContain("·", text, StringComparison.Ordinal);
		Assert.DoesNotContain("—", text, StringComparison.Ordinal);
	}

	[Fact]
	public void ANeuronAgainstItselfShowsNoDifferences()
	{
		NeuronSnapshot lab = Snapshot(Lab);
		Comparison c = NeuronComparer.Compare(lab, lab, "Lab", "Lab");
		Assert.Equal(0, c.Differences);
		Assert.Equal("No differences", c.CountText);
		Assert.Empty(c.Notes);
		Assert.All(c.Rows.Where(r => r.Group != CompareGroup.StateNow), r => Assert.Equal(RowKind.Same, r.Kind));
	}

	[Fact]
	public void StateNowIsShownButNeverCounted()
	{
		// The same Neuron, read a minute apart: busier, but no different.
		Comparison c = NeuronComparer.Compare(Snapshot(Lab, cpu: 14), Snapshot(Lab, cpu: 97), "Lab", "Lab");
		Assert.Equal(0, c.Differences);

		CompareRow cpu = c.Rows.Single(r => r.Key == "state/cpu");
		Assert.Equal(RowKind.Background, cpu.Kind);
		Assert.Equal(("14 %", "97 %"), (cpu.Left, cpu.Right));
		Assert.False(cpu.IsDifference);

		// Production's state differs everywhere, and still adds nothing to the count.
		Comparison two = NeuronComparer.Compare(Snapshot(Lab), Snapshot(Prod, production: true, cpu: 88), "Lab", "Production");
		Assert.Equal(8, two.Differences);
		Assert.All(two.Rows.Where(r => r.Group == CompareGroup.StateNow), r => Assert.Equal(RowKind.Background, r.Kind));
		Assert.Contains(two.Rows, r => r.Key == "state/clients" && r.Left == "18" && r.Right == "312");
	}

	// ----- Secrets -----

	[Fact]
	public void APasswordDiffersButIsNeverShown()
	{
		NeuronSnapshot lab = Snapshot(Lab);
		NeuronSnapshot prod = Snapshot(Prod, production: true);

		// Read as a secret by its name: kept as a hash, never as the value.
		SnapshotValue pw = lab.Values.Single(v => v.Label == "MqttSink, Password");
		Assert.True(pw.IsSecret);
		Assert.Null(pw.Value);
		Assert.DoesNotContain(LabSecret, pw.SecretHash!, StringComparison.Ordinal);

		Comparison c = NeuronComparer.Compare(lab, prod, "Lab", "Production");
		CompareRow row = c.Rows.Single(r => r.Label == "MqttSink, Password");
		Assert.Equal(RowKind.Different, row.Kind);
		Assert.True(row.IsSecret);
		Assert.Equal(("Different", "Different"), (row.Left, row.Right));

		// The same password is "Same", and still not shown.
		CompareRow same = NeuronComparer.Compare(lab, Snapshot(Lab), "Lab", "Lab").Rows.Single(r => r.Label == "MqttSink, Password");
		Assert.Equal(("Same", "Same", RowKind.Same), (same.Left, same.Right, same.Kind));

		foreach (string text in new[] { c.ToText(true), c.ToText(false), string.Join("\n", c.Rows.Select(r => r.Left + r.Right + r.Label)) })
		{
			Assert.DoesNotContain(LabSecret, text, StringComparison.Ordinal);
			Assert.DoesNotContain(ProdSecret, text, StringComparison.Ordinal);
		}

		// Names that mean a secret, and some that do not.
		Assert.True(Secrets.IsSecret("Password"));
		Assert.True(Secrets.IsSecret("ApiKey"));
		Assert.True(Secrets.IsSecret("x", "Access token:"));
		Assert.True(Secrets.IsSecret("CertificatePrivateKey"));
		Assert.True(Secrets.IsSecret("ClientSecret"));
		Assert.False(Secrets.IsSecret("Broker", "Broker:"));
		Assert.False(Secrets.IsSecret("PortNumber", "Port Number:"));
	}

	[Fact]
	public void APrivateFieldIsASecretWhateverItIsCalled()
	{
		SnapshotValue v = Assert.Single(LiveNeuronReader.SettingValues("EventSinks", "Where events are sent", "EventSinks/X", "X", [("Code", "Code:", "4471", true)]));
		Assert.True(v.IsSecret);
		Assert.Null(v.Value);
	}

	// ----- Missing rights on one side -----

	[Fact]
	public void MissingRightsOnOneSideIsANoteAndTheOtherSideStillShows()
	{
		NeuronSnapshot prod = new() { NeuronJid = Prod, Name = "Production", ReadAt = DateTime.UtcNow, NoAccess = true };
		Comparison c = NeuronComparer.Compare(Snapshot(Lab), prod, "Lab", "Production");

		Assert.Equal(["No access on Production. Only the other side shows."], c.Notes);
		Assert.Equal(0, c.Differences);
		Assert.Equal("No differences", c.CountText);

		CompareRow version = c.Rows.Single(r => r.Label == "Version");
		Assert.Equal(RowKind.NotRead, version.Kind);
		Assert.Equal(("Waher.IoTGateway 3.11.1.0", "Not read"), (version.Left, version.Right));
		Assert.Contains(c.Rows, r => r.Label == "Port 8080" && r.Left == "HTTP");

		// The secret stays a secret on the side that was read.
		Assert.Equal("Set", c.Rows.Single(r => r.Label == "MqttSink, Password").Left);

		// One group that could not be read is named with the Neuron.
		NeuronSnapshot partly = Snapshot(Prod, production: true) with { NotRead = new Dictionary<CompareGroup, string> { [CompareGroup.Settings] = "Forbidden." } };
		Comparison p = NeuronComparer.Compare(Snapshot(Lab), partly, "Lab", "Production");
		Assert.Equal(["Settings not read on Production: Forbidden"], p.Notes);
		Assert.Equal(5, p.Differences);
	}

	// ----- Kept copies -----

	[Fact]
	public async Task AKeptCopyComparesWithTheNeuronAsItIsNow()
	{
		MemoryNeuronCopyStore store = new();
		NeuronSnapshot then = await store.KeepAsync(Snapshot(Lab, readAt: DateTime.UtcNow.AddDays(-2)));
		Assert.True(NeuronCopies.IsId(then.CopyId));
		Assert.All(then.Values.Where(v => v.IsSecret), v => Assert.Null(v.Value));

		KeptCopy listed = Assert.Single(await store.ListAsync(Lab));
		Assert.Equal(then.CopyId, listed.CopyId);
		Assert.Empty(await store.ListAsync(Prod));

		// Since then, automatic updates were set to an hour.
		NeuronSnapshot now = Snapshot(Lab, autoMinutes: 60);
		Comparison c = NeuronComparer.Compare(now, (await store.GetAsync(then.CopyId!))!, "Lab", "Lab, kept");
		Assert.Equal(1, c.Differences);
		CompareRow auto = c.Rows.Single(r => r.IsDifference);
		Assert.Equal(("After 60 min", "After 15 min"), (auto.Left, auto.Right));

		// The password is compared with the kept hash, and is the same.
		Assert.Equal(RowKind.Same, c.Rows.Single(r => r.Label == "MqttSink, Password").Kind);
	}

	[Fact]
	public async Task KeptCopiesAreInTheEncryptedDatabaseWithoutTheirSecrets()
	{
		EngineRuntime.Initialize();
		byte[] key = RandomNumberGenerator.GetBytes(32);
		string dbFolder = Path.Combine(this.folder, "database");

		DatabaseOpening o = await LocalDatabase.OpenAsync(dbFolder, key);
		Assert.True(o.IsOpen, o.Problem);
		FilesProvider db = o.Database!;
		DatabaseNeuronCopyStore store = new(db);
		NeuronSnapshot older = await store.KeepAsync(Snapshot(Lab, readAt: DateTime.UtcNow.AddDays(-3)));
		NeuronSnapshot newer = await store.KeepAsync(Snapshot(Lab, autoMinutes: 30, readAt: DateTime.UtcNow.AddHours(-1)));
		await store.KeepAsync(Snapshot(Prod, production: true));
		await db.Stop();

		// Back after a restart, newest first, by Neuron.
		o = await LocalDatabase.OpenAsync(dbFolder, key);
		Assert.True(o.IsOpen, o.Problem);
		db = o.Database!;
		store = new DatabaseNeuronCopyStore(db);
		Assert.Equal([newer.CopyId, older.CopyId], (await store.ListAsync("ops@" + Lab)).Select(k => k.CopyId));
		Assert.Single(await store.ListAsync(Prod));

		NeuronSnapshot back = (await store.GetAsync(older.CopyId!))!;
		Assert.Equal(older.Values.Count, back.Values.Count);
		Assert.Equal(0, NeuronComparer.Compare(Snapshot(Lab), back, "Lab", "Lab, kept").Differences);
		Assert.Equal(1, NeuronComparer.Compare(Snapshot(Lab), (await store.GetAsync(newer.CopyId!))!, "Lab", "Lab, kept").Differences);
		Assert.Null(await store.GetAsync("not-an-id"));
		Assert.Null(await store.GetAsync(Guid.NewGuid().ToString("N")));
		await db.Stop();

		// Neither the secret nor anything else is readable in the files.
		foreach (string file in Directory.EnumerateFiles(dbFolder, "*", SearchOption.AllDirectories))
		{
			byte[] bytes = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
			foreach (string secret in new[] { LabSecret, ProdSecret, "mqtt.example.com" })
			{
				Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(secret)), $"{secret} is readable in {Path.GetFileName(file)}.");
				Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(secret)), $"{secret} is readable in {Path.GetFileName(file)}.");
			}
		}
	}

	[Fact]
	public async Task OnlyTheNewestCopiesOfANeuronAreKept()
	{
		MemoryNeuronCopyStore store = new();
		DateTime start = DateTime.UtcNow.AddDays(-100);
		for (int i = 0; i < NeuronCopies.MaxPerNeuron + 5; i++)
			await store.KeepAsync(Snapshot(Lab, readAt: start.AddHours(i)));

		IReadOnlyList<KeptCopy> kept = await store.ListAsync(Lab);
		Assert.Equal(NeuronCopies.MaxPerNeuron, kept.Count);
		Assert.Equal(start.AddHours(NeuronCopies.MaxPerNeuron + 4), kept[0].Taken);
	}

	// ----- The link -----

	[Fact]
	public void TheLinkRoundTrips()
	{
		NatLink two = NatLink.Compare("ops@" + Lab, Prod);
		Assert.Equal("neuro-nat://compare/lab.example.com/prod.example.com", two.ToString());
		Assert.True(NatLink.TryParse(two.ToString(), out NatLink back));
		Assert.Equal(two, back);
		Assert.Equal(LinkKind.Compare, back.Kind);
		Assert.Equal(Lab, back.Domain);
		Assert.Equal(Prod, back.OtherDomain);
		Assert.Null(back.CopyId);

		string id = Guid.NewGuid().ToString("N");
		NatLink kept = NatLink.CompareWithKept(Lab, id);
		Assert.Equal("neuro-nat://compare/lab.example.com/kept/" + id, kept.ToString());
		Assert.True(NatLink.TryParse(kept.ToString().ToUpperInvariant().Replace("NEURO-NAT://COMPARE", "neuro-nat://compare", StringComparison.Ordinal), out NatLink k));
		Assert.Equal(kept, k);
		Assert.Equal(id, k.CopyId);
		Assert.Null(k.OtherDomain);

		Assert.Equal([two, kept], NatLink.FindAll($"Look at {two} and ({kept})."));

		foreach (string bad in new[]
		{
			"neuro-nat://compare/lab.example.com",
			"neuro-nat://compare/lab.example.com/",
			"neuro-nat://compare/lab.example.com/prod.example.com/more",
			"neuro-nat://compare/lab.example.com/kept/abc",
			"neuro-nat://compare/lab.example.com/kept/" + id + "/x",
			"neuro-nat://compare/lab.example.com/earlier/" + id,
			"neuro-nat://compare/lab.example.com/prod.example.com?at=2026-11-12T09:42:13Z",
			"neuro-nat://compare/lab..example.com/prod.example.com",
			"neuro-nat://compare/lab.example.com/%2E%2E",
			"neuro-nat://compare/lab.example.com/ops@prod.example.com",
		})
		{
			Assert.False(NatLink.TryParse(bad, out _), bad);
		}
	}

	private static bool Contains(byte[] haystack, byte[] needle)
	{
		for (int i = 0; i <= haystack.Length - needle.Length; i++)
		{
			if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
				return true;
		}

		return false;
	}
}
