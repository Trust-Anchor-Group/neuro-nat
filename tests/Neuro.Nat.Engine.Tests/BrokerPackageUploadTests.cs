using System.Net;
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
		public bool Refuse { get; init; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
		{
			string path = request.RequestUri!.AbsolutePath;
			this.Paths.Add(path);
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
			this.Tab ??= Header("X-TabID");
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
		using HttpClient client = new(broker) { BaseAddress = new("https://broker.example/") };
		Task send = BrokerPackageUpload.TransferAsync(client, "operator", "sample", package, signed ? signature : null, downloadable,
			new Progress<int>(), CancellationToken.None);
		if (denied)
		{
			Assert.Contains("Admin.Software.Upload", (await Assert.ThrowsAsync<InvalidOperationException>(() => send)).Message);
			Assert.Equal(["/Login", "/UploadPackage", "/Logout"], broker.Paths);
			return;
		}
		await send;
		string sign = signed ? "0" : "1", download = downloadable ? "1" : "0";
		Assert.Equal(("0", "1", sign, download, "demo.package", blockSize), broker.Blocks[0]);
		Assert.Equal(("1", "0", sign, download, (string?)null, 5), broker.Blocks[1]);
		if (signed)
			Assert.Equal(("0", "0", (string?)null, download, "demo.xml", 3), broker.Blocks[2]);
		Assert.Equal(signed ? ["/Login", "/UploadPackage", "/UploadPackage", "/UploadSignature", "/Logout"] :
			new[] { "/Login", "/UploadPackage", "/UploadPackage", "/Logout" }, broker.Paths);
	}

	private sealed class TemporaryUploadFiles : IDisposable
	{
		public string Path { get; } = Directory.CreateTempSubdirectory("nat-upload-").FullName;
		public void Dispose() => Directory.Delete(this.Path, true);
	}
}
