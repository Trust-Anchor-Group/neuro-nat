using System.Security.AccessControl;
using System.Security.Principal;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Desktop.Kiosk;
using Neuro.Nat.Engine.Accounts;

namespace Neuro.Nat.App.Tests;

/// <summary>A kiosk's config file: read at every start, never written, and refused when others can read it.</summary>
public class KioskConfigTests : IDisposable
{
	private readonly string folder = Path.Combine(Path.GetTempPath(), "Nat.Kiosk." + Guid.NewGuid().ToString("N"));

	public KioskConfigTests() => Directory.CreateDirectory(this.folder);

	public void Dispose()
	{
		try
		{
			Directory.Delete(this.folder, true);
		}
		catch (IOException)
		{
		}
	}

	private string Write(string json)
	{
		string path = Path.Combine(this.folder, "nat-kiosk.json");
		File.WriteAllText(path, json);
		if (OperatingSystem.IsWindows())
		{
			// As a kiosk's file must be: only its own user (and the system) may read it.
			FileInfo f = new(path);
			FileSecurity security = new();
			security.SetAccessRuleProtection(true, false);
			security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
			security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
			f.SetAccessControl(security);
		}
		else
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		return path;
	}

	private const string Good = """
		{
		  // The kiosk's own account, on the notify list of the Neurons it shows.
		  "accounts": [
		    { "host": "localhost", "port": 5222, "userName": "kiosk", "password": "s3cret", "webAddress": "http://localhost:8081", "trustServerCertificate": true },
		    { "host": "prod.example.com", "userName": "kiosk", "password": "other" }
		  ],
		  "neurons": [ "Lab.Example.com" ],
		  "pages": [ "neurons" ],
		  "seconds": 30,
		  "look": "dark",
		}
		""";

	[Fact]
	public async Task AGoodFileGivesTheAccountsAndTheBigScreenSettings()
	{
		string path = this.Write(Good);
		byte[] before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

		KioskConfig c = KioskConfig.Load(path);
		Assert.Equal(2, c.Accounts.Count);
		Assert.Equal(new KioskAccount("localhost", 5222, "kiosk", "s3cret", null, "http://localhost:8081", true), c.Accounts[0]);
		Assert.Equal(5222, c.Accounts[1].Port);
		Assert.Equal(["lab.example.com"], c.Neurons);
		Assert.Equal(30, c.Seconds);
		Assert.Equal(LookChoice.Dark, c.Look);

		// What NAT runs on: the accounts with their passwords, in memory; Problems is off because only Neurons was asked for.
		var stores = KioskSetup.Stores(c);
		IReadOnlyList<AccountProfile> profiles = await stores.Accounts.LoadAsync(TestContext.Current.CancellationToken);
		Assert.Equal(["localhost", "prod.example.com"], profiles.Select(p => p.Host));
		Assert.Equal("http://localhost:8081/", profiles[0].WebAddress);
		Assert.True(profiles[0].TrustServerCertificate);
		Assert.Equal("s3cret", (await stores.SignIns.GetAsync(profiles[0].Id, TestContext.Current.CancellationToken))?.Value);
		UiSettings ui = stores.UiSettings.Load();
		Assert.Equal(30, ui.BigScreenSeconds);
		Assert.Contains(BigScreenPages.Problems, ui.BigScreenOff);
		Assert.False(stores.KeepsData);

		// The file itself is never written.
		Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData("{ \"accounts\": [ ", "is not valid JSON")]
	[InlineData("[]", "must be one JSON object")]
	[InlineData("{ }", "needs \"accounts\"")]
	[InlineData("{ \"accounts\": [ { \"host\": \"a\", \"userName\": \"b\" } ] }", "account 1 needs \"password\"")]
	[InlineData("{ \"accounts\": [ { \"host\": \"a\", \"userName\": \"b\", \"password\": \"c\", \"port\": 70000 } ] }", "\"port\" must be a number")]
	[InlineData("{ \"accounts\": [ { \"host\": \"a\", \"userName\": \"b\", \"password\": \"c\", \"webAddress\": \"http://lab.example.com\" } ] }", "\"webAddress\" must be")]
	[InlineData("{ \"accounts\": [ { \"host\": \"a\", \"userName\": \"b\", \"password\": \"c\" } ], \"seconds\": 15 }", "\"seconds\" must be 10, 20, 30 or 60")]
	[InlineData("{ \"accounts\": [ { \"host\": \"a\", \"userName\": \"b\", \"password\": \"c\" } ], \"look\": \"blue\" }", "\"look\" must be")]
	public void ABrokenFileSaysWhatIsWrongAndWhere(string json, string expected)
	{
		string path = this.Write(json);
		KioskConfigException ex = Assert.Throws<KioskConfigException>(() => KioskConfig.Load(path));
		Assert.Contains(expected, ex.Message);
		Assert.Contains("nat-kiosk.json", ex.Message);
		Assert.DoesNotContain("·", ex.Message);
	}

	[Fact]
	public void AMissingFileIsSaid()
	{
		KioskConfigException ex = Assert.Throws<KioskConfigException>(() => KioskConfig.Load(Path.Combine(this.folder, "nat-kiosk.json")));
		Assert.Contains("There is no nat-kiosk.json", ex.Message);
	}

	[Fact]
	public void AFileOthersCanReadIsRefused()
	{
		string path = this.Write(Good);
		Assert.Null(KioskConfig.WhoElseCanRead(path));

		if (OperatingSystem.IsWindows())
		{
			FileInfo f = new(path);
			FileSecurity security = f.GetAccessControl();
			security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
			f.SetAccessControl(security);
		}
		else
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

		Assert.NotNull(KioskConfig.WhoElseCanRead(path));
		KioskConfigException ex = Assert.Throws<KioskConfigException>(() => KioskConfig.Load(path));
		Assert.Contains("holds passwords", ex.Message);
	}
}
