using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Software;

namespace Neuro.Nat.Engine.Tests;

/// <summary>Reading the chat admin's software answers, as the broker writes them.</summary>
public class SoftwareTests
{
	[Theory]
	[InlineData(true, "install fo.package")]
	[InlineData(false, "install nobackup fo.package")]
	public async Task InstallSendsTheKeyOnlyWithTheChosenPackage(bool backup, string expected)
	{
		string key = Convert.ToBase64String(new byte[57]) + (backup ? "" : new string('a', 32));
		string? sent = null;
		string? sentTo = null;
		Guid account = Guid.NewGuid();
		ConsoleSession? console = null;
		console = new(account, "root.example", () => "root.example/admin", (to, body, thread) =>
		{
			sentTo = to;
			sent = body;
			console!.Receive(new(account, "root.example", "1", null, DateTime.Now, "Installing fo.package", ""), thread);
			return Task.CompletedTask;
		});
		List<string> recorded = [];
		console.Changed += (_, entry) => recorded.Add(entry.Command);

		await Assert.ThrowsAsync<ArgumentException>(() => SoftwareService.InstallAsync(console, "nobackup", key, backup));
		await Assert.ThrowsAsync<ArgumentException>(() => SoftwareService.InstallAsync(console, "fo.package", "", backup));
		Assert.Null(sent);
		Assert.Equal(["Installing fo.package"], await SoftwareService.InstallAsync(console, "fo.package", key, backup));
		Assert.Equal("root.example/admin", sentTo);
		Assert.Equal(expected + " " + key, sent);
		Assert.NotEmpty(recorded);
		Assert.All(recorded, command => Assert.Equal(expected, command));
		Assert.Empty(console.Entries);
	}

	[Theory]
	[InlineData(null, true, "upgrade")]
	[InlineData(null, false, "upgrade nobackup")]
	[InlineData("fo.package", true, "upgrade fo.package")]
	[InlineData("fo.package", false, "upgrade nobackup fo.package")]
	public void UpgradeTargetsOnlyTheChosenScope(string? package, bool backup, string expected)
		=> Assert.Equal(expected, SoftwareService.UpgradeCommand(package, backup));

	[Theory]
	[InlineData("")]
	[InlineData("nobackup")]
	[InlineData("IoTBroker.package")]
	[InlineData("other.package key")]
	public void InvalidPackageCannotBecomeAWholeNeuronUpgrade(string package)
		=> Assert.Throws<ArgumentException>(() => SoftwareService.UpgradeCommand(package, true));

	[Fact]
	public void ReadsAutoUpdateSettings()
	{
		(int? delay, bool? content) = SoftwareService.ParseAuto(["New packages will be installed 15 minutes after receiving a new package.", "New content-only packages will be installed when received."]);
		Assert.Equal(15, delay);
		Assert.True(content);

		(delay, content) = SoftwareService.ParseAuto(["Auto-installation of new packages not activated.", "Content-only packages are processed as other packages."]);
		Assert.Null(delay);
		Assert.False(content);
	}

	[Fact]
	public void ReadsInstalledPackages()
	{
		Assert.Empty(SoftwareService.ParseInstalled("-"));
		Assert.Equal(["TAG.Neuron.package", "TAG.Payments.package"], SoftwareService.ParseInstalled("TAG.Neuron.package; TAG.Payments.package"));
	}

	[Fact]
	public void ReadsPackageTable()
	{
		string md = "| File Name | Size | Published | Supersedes | Created | Installed | Signature | Remote Endpoint |\n" +
			"|:----------|:----:|:---------:|:----------:|:-------:|:---------:|:----------|:----------------|\n" +
			"| [TAG.Neuron.package](https://x/Packages/TAG.Neuron.package) | 48.2 MB | 2026-11-01 10:00 | | 2026-11-02 08:00 | 2026-11-02 08:05 | <a href='javascript:Popup.Alert(\"abc\")'>abcdefghij...</a> | lab.example.com |\n" +
			"| <a href=\"https://x/ChatFile/TAG.Payments.package?jid=ops&amp;t=token\">TAG.Payments.package</a> | 1.1 MB | 2026-10-01 10:00 | | 2026-10-02 08:00 | | x | lab.example.com |\n";
		IReadOnlyList<SoftwarePackage> p = SoftwareService.ParsePackages(md);
		Assert.Equal(2, p.Count);
		Assert.Equal("TAG.Neuron.package", p[0].FileName);
		Assert.True(p[0].IsInstalled);
		Assert.False(p[0].CanInstall);
		Assert.Equal("TAG.Payments.package", p[1].FileName);
		Assert.True(p[1].CanInstall);
		Assert.Equal("lab.example.com", p[1].From);
	}
}

/// <summary>Software of the local test Neuron, through chat admin.</summary>
[Collection("Live Neuron")]
public class LiveSoftwareTests(ITestOutputHelper output)
{
	[Fact]
	public async Task ReadsTheNeuronsSoftware()
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? neuron = Environment.GetEnvironmentVariable("NAT_TEST_NEURON");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(neuron))
			Live.Skip("NAT_TEST_HOST", "NAT_TEST_USER", "NAT_TEST_PASSWORD", "NAT_TEST_NEURON");

		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Live." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		await using ChatService chat = new(manager, monitors);
		await s.ConnectAsync();
		await StreamSnifferTests.WaitFor(() => monitors.Neurons.Any(n => n.State == WatchState.Live), 30000);

		SoftwareService software = new(monitors, chat);
		NeuronSoftware sw = await software.ReadAsync(s.Profile.Id, neuron);
		output.WriteLine($"Build {sw.BuildTime}; auto {sw.AutoDelayMinutes?.ToString() ?? "off"}, content {sw.ContentAutoInstall}; installed: {string.Join(", ", sw.Installed)}; packages: {string.Join(", ", sw.Packages.Select(p => p.FileName))}; error {sw.Error}");
		Assert.Null(sw.Error);
		// A developer build of the Neuron records no build time; BuildTime is then null.
		Assert.NotNull(sw.ContentAutoInstall);

		// The console doubles as the script console: a script over several lines runs as one.
		string? answer = await chat.Console(s.Profile.Id, neuron).AskAsync("x:=6;\ny:=7;\nx*y", TimeSpan.FromSeconds(20));
		output.WriteLine("Script: " + answer);
		Assert.Contains("42", answer ?? string.Empty);
	}
}
