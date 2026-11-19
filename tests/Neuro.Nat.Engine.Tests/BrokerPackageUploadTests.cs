using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neuro.Nat.Engine.Software;

namespace Neuro.Nat.Engine.Tests;

public class BrokerPackageUploadTests
{
	private sealed class Broker : HttpMessageHandler
	{
		public List<string> Paths { get; } = [];
		public List<(string Block, string More, string? Sign, string Download, string? Name, int Length)> Blocks { get; } = [];
		public string? Tab { get; private set; }
		public bool Refuse { get; set; }
		public bool SupportsLegalId { get; init; } = true;
		public bool AlreadyApproved { get; init; }
		public bool WaitForCancellation { get; init; }
		public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private string? loginTab;
		private int polls;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
		{
			string path = request.RequestUri!.AbsolutePath;
			this.Paths.Add(path);
			if (path == "/Agent/Account/RemoteQuickLogin")
			{
				using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancel));
				var payload = json.RootElement;
				Assert.Equal("id@example", payload.GetProperty("legalId").GetString());
				Assert.Contains(request.RequestUri.Authority, payload.GetProperty("purpose").GetString());
				this.loginTab = payload.GetProperty("tabId").GetString();
				Assert.True(Guid.TryParse(this.loginTab, out _));
				return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { loggedIn = this.AlreadyApproved, petitionSent = this.SupportsLegalId && !this.AlreadyApproved }) };
			}
			if (path == "/ClientEvents")
			{
				Assert.Equal(this.loginTab, request.Headers.GetValues("X-TabID").Single());
				Assert.Equal(new Uri(request.RequestUri, "/Login.md").AbsoluteUri, await request.Content!.ReadAsStringAsync(cancel));
				this.Waiting.TrySetResult();
				if (this.WaitForCancellation)
					await Task.Delay(Timeout.InfiniteTimeSpan, cancel);
				string events = ++this.polls == 1 ? """[{"type":"NOP"},{"type":"SignatureReceivedBE","data":{"Id":"someone-else"}}]""" :
					"""[{"type":"SignatureReceivedBE","data":{"Id":"id@example"}}]""";
				return new(HttpStatusCode.OK) { Content = new StringContent(events) };
			}
			if (path == "/Login")
			{
				using JsonDocument json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancel));
				var fields = json.RootElement;
				Assert.Equal("operator", fields.GetProperty("UserName").GetString());
				string nonce = fields.GetProperty("Nonce").GetString()!;
				Assert.Equal(32, Convert.FromBase64String(nonce).Length);
				byte[] inner = new Waher.Security.SHA3.SHA3_256().ComputeVariable("operator:broker.example:sample"u8.ToArray());
				Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(nonce), inner)), fields.GetProperty("PasswordHash").GetString());
				return new(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
			}
			if (path == "/Logout")
				return new(HttpStatusCode.SeeOther);

			string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
			if (path == "/UploadPackage" && Header("X-BlockNr") == "0")
				this.Tab = Header("X-TabID");
			Assert.True(Guid.TryParse(this.Tab, out _));
			Assert.Equal(this.Tab, Header("X-TabID"));
			byte[] data = await request.Content!.ReadAsByteArrayAsync(cancel);
			this.Blocks.Add((Header("X-BlockNr")!, Header("X-More")!, Header("X-GenSign"), Header("X-Downloadable")!, Header("X-FileName"), data.Length));
			return new(this.Refuse ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
		}
	}

	[Theory]
	[InlineData(false, false, false)]
	[InlineData(true, true, false)]
	[InlineData(true, false, true)]
	public async Task TransfersPackageBlocksAndOptionalSignature(bool signed, bool downloadable, bool denied)
	{
		using var folder = new TemporaryUploadFiles();
		string package = Path.Combine(folder.Path, "demo.package");
		string signature = Path.Combine(folder.Path, "demo.xml");
		const int blockSize = 4 * 1024 * 1024;
		await File.WriteAllBytesAsync(package, new byte[blockSize + 5]);
		await File.WriteAllTextAsync(signature, "sig");
		var broker = new Broker { Refuse = denied };
		await using BrokerUploadSessions sessions = new(origin => new HttpClient(broker, false) { BaseAddress = origin });
		Task send = sessions.UploadAsync(Guid.NewGuid(), new Uri("https://broker.example/"), UploadSignIn.WebAccount, "operator", "sample", package, signed ? signature : null, downloadable,
			new Progress<int>(), CancellationToken.None);
		if (denied)
		{
			Assert.Contains("Admin.Software.Upload", (await Assert.ThrowsAsync<InvalidOperationException>(() => send)).Message);
			Assert.Equal(["/Login", "/UploadPackage", "/Logout"], broker.Paths);
			return;
		}
		await send;
		await sessions.DisposeAsync();
		string sign = signed ? "0" : "1", download = downloadable ? "1" : "0";
		Assert.Equal(("0", "1", sign, download, "demo.package", blockSize), broker.Blocks[0]);
		Assert.Equal(("1", "0", sign, download, (string?)null, 5), broker.Blocks[1]);
		if (signed)
			Assert.Equal(("0", "0", (string?)null, download, "demo.xml", 3), broker.Blocks[2]);
		Assert.Equal(signed ? ["/Login", "/UploadPackage", "/UploadPackage", "/UploadSignature", "/Logout"] :
			new[] { "/Login", "/UploadPackage", "/UploadPackage", "/Logout" }, broker.Paths);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LegalApprovalCreatesAReusableScopedSession(bool alreadyApproved)
	{
		using var folder = new TemporaryUploadFiles();
		string package = Path.Combine(folder.Path, "demo.package");
		await File.WriteAllBytesAsync(package, [1]);
		var broker = new Broker { AlreadyApproved = alreadyApproved };
		Uri origin = new("https://broker.example/");
		Guid account = Guid.NewGuid();
		int connections = 0;
		await using BrokerUploadSessions sessions = new(uri =>
		{
			connections++;
			return new HttpClient(broker, false) { BaseAddress = uri };
		});
		Task Upload(Guid id, Uri uri, UploadSignIn mode) => sessions.UploadAsync(id, uri, mode, "id@example", null, package, null, false, new Progress<int>(), default);
		await Upload(account, origin, UploadSignIn.LegalId);
		Assert.Equal(alreadyApproved ? ["/Agent/Account/RemoteQuickLogin", "/UploadPackage"] :
			new[] { "/Agent/Account/RemoteQuickLogin", "/ClientEvents", "/ClientEvents", "/UploadPackage" }, broker.Paths);
		Assert.Equal("id@example", sessions.SignedInAs(account, origin));
		await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(Guid.NewGuid(), origin, UploadSignIn.ExistingSession));
		await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(account, new Uri("https://broker.example:8443/"), UploadSignIn.ExistingSession));
		await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(account, new Uri("https://other.example/"), UploadSignIn.ExistingSession));
		await Upload(account, origin, UploadSignIn.ExistingSession);
		Assert.Equal(1, connections);
		Assert.Equal(2, broker.Blocks.Count);
		broker.Refuse = true;
		await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(account, origin, UploadSignIn.ExistingSession));
		Assert.Null(sessions.SignedInAs(account, origin));
		Assert.Equal("/Logout", broker.Paths.Last());
		Assert.Equal(3, broker.Blocks.Count);
	}

	[Fact]
	public async Task AnUnsupportedBrokerCanSignInOnceWithWebCredentials()
	{
		using var folder = new TemporaryUploadFiles();
		string package = Path.Combine(folder.Path, "demo.package");
		await File.WriteAllBytesAsync(package, [1]);
		var broker = new Broker { SupportsLegalId = false };
		await using BrokerUploadSessions sessions = new(uri => new HttpClient(broker, false) { BaseAddress = uri });
		Uri origin = new("https://broker.example/");
		Guid account = Guid.NewGuid();
		Task Upload(UploadSignIn mode, string identity, string? password) =>
			sessions.UploadAsync(account, origin, mode, identity, password, package, null, false, new Progress<int>(), default);
		await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(UploadSignIn.LegalId, "id@example", null));
		Assert.Empty(broker.Blocks);
		await Upload(UploadSignIn.WebAccount, "operator", "sample");
		await Upload(UploadSignIn.ExistingSession, "", null);
		Assert.Equal("operator", sessions.SignedInAs(account, origin));
		Assert.Equal(["/Agent/Account/RemoteQuickLogin", "/Logout", "/Login", "/UploadPackage", "/UploadPackage"], broker.Paths);
	}

	[Fact]
	public async Task CancellingAPendingPetitionSendsNoPackage()
	{
		using var folder = new TemporaryUploadFiles();
		string package = Path.Combine(folder.Path, "demo.package");
		await File.WriteAllBytesAsync(package, [1]);
		var broker = new Broker { WaitForCancellation = true };
		await using BrokerUploadSessions sessions = new(uri => new HttpClient(broker, false) { BaseAddress = uri });
		Uri origin = new("https://broker.example/");
		Guid account = Guid.NewGuid();
		using CancellationTokenSource cancel = new();
		Task upload = sessions.UploadAsync(account, origin, UploadSignIn.LegalId, "id@example", null, package, null, false, new Progress<int>(), cancel.Token);
		await broker.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.UploadAsync(Guid.NewGuid(), origin, UploadSignIn.WebAccount,
			"operator", "sample", package, null, false, new Progress<int>(), default));
		await cancel.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
		Assert.Empty(broker.Blocks);
		Assert.Null(sessions.SignedInAs(account, origin));
		Assert.Equal("/Logout", broker.Paths.Last());
	}

	private sealed class TemporaryUploadFiles : IDisposable
	{
		public string Path { get; } = Directory.CreateTempSubdirectory("nat-upload-").FullName;
		public void Dispose() => Directory.Delete(this.Path, true);
	}
}
