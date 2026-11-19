using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neuro.Nat.Engine.Software;

/// <summary>Uploads through the broker's web administration endpoints using the IoTGateway upload protocol.</summary>
public static class BrokerPackageUpload
{
	// The broker checks block numbers across upload sessions. Two NAT windows must not
	// interleave their package blocks on the same broker.
	private static readonly HashSet<Uri> activeBrokers = [];

	public static async Task SendAsync(Uri broker, string user, string password, string packagePath, string? signaturePath,
		bool downloadable, IProgress<int> progress, CancellationToken cancel)
	{
		if (!broker.IsAbsoluteUri || broker.Scheme != Uri.UriSchemeHttps || broker.UserInfo.Length != 0 ||
			broker.PathAndQuery != "/" || broker.Fragment.Length != 0)
			throw new ArgumentException("Enter the broker’s HTTPS address, including its port if needed.");

		CookieContainer cookies = new();
		cookies.Add(broker, new Cookie("HttpSessionID", Guid.NewGuid().ToString(), "/") { Secure = true });
		using HttpClient client = new(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = broker };
		lock (activeBrokers)
		{
			if (!activeBrokers.Add(broker))
				throw new InvalidOperationException("Another package upload to this broker is in progress.");
		}
		try
		{
			await TransferAsync(client, user, password, packagePath, signaturePath, downloadable, progress, cancel);
		}
		finally
		{
			lock (activeBrokers)
				activeBrokers.Remove(broker);
		}
	}

	internal static async Task TransferAsync(HttpClient client, string user, string password, string packagePath, string? signaturePath,
		bool downloadable, IProgress<int> progress, CancellationToken cancel)
	{
		using FileStream package = File.OpenRead(packagePath);
		using FileStream? signature = signaturePath is null ? null : File.OpenRead(signaturePath);
		if (package.Length == 0 || signature?.Length == 0)
			throw new InvalidDataException("Choose a nonempty package and, if supplied, a nonempty signature file.");

		string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
		byte[] input = Encoding.UTF8.GetBytes($"{user}:{client.BaseAddress!.Host}:{password}");
		byte[] hash = new Waher.Security.SHA3.SHA3_256().ComputeVariable(input);
		CryptographicOperations.ZeroMemory(input);
		string proof = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(nonce), hash));
		CryptographicOperations.ZeroMemory(hash);
		using HttpResponseMessage login = await client.PostAsJsonAsync("Login",
			new Dictionary<string, string> { ["UserName"] = user, ["PasswordHash"] = proof, ["Nonce"] = nonce }, cancel);
		login.EnsureSuccessStatusCode();
		using JsonDocument reply = JsonDocument.Parse(await login.Content.ReadAsStringAsync(cancel));
		if (!reply.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
			throw new InvalidOperationException(reply.RootElement.TryGetProperty("message", out var msg) ? msg.GetString() : "Broker sign-in failed.");

		try
		{
			string tab = Guid.NewGuid().ToString();
			long total = package.Length + (signature?.Length ?? 0);
			byte[] buffer = new byte[(int)Math.Min(4 * 1024 * 1024, Math.Max(package.Length, signature?.Length ?? 0))];

			async Task SendFile(FileStream file, bool isPackage, long offset)
			{
				int block = 0;
				while (file.Position < file.Length)
				{
					int length = (int)Math.Min(buffer.Length, file.Length - file.Position);
					await file.ReadExactlyAsync(buffer.AsMemory(0, length), cancel);
					using HttpRequestMessage request = new(HttpMethod.Post, isPackage ? "UploadPackage" : "UploadSignature");
					request.Content = new ByteArrayContent(buffer, 0, length);
					request.Content.Headers.ContentType = new("application/octet-stream");
					if (block == 0)
						request.Headers.Add("X-FileName", Path.GetFileName(file.Name));
					request.Headers.Add("X-TabID", tab);
					request.Headers.Add("X-BlockNr", block.ToString(CultureInfo.InvariantCulture));
					request.Headers.Add("X-More", file.Position < file.Length ? "1" : "0");
					request.Headers.Add("X-Downloadable", downloadable ? "1" : "0");
					if (isPackage)
						request.Headers.Add("X-GenSign", signature is null ? "1" : "0");
					using HttpResponseMessage response = await client.SendAsync(request, cancel);
					if (response.StatusCode == HttpStatusCode.Forbidden)
						throw new InvalidOperationException("This web account needs Admin.Software.Upload on the broker.");
					response.EnsureSuccessStatusCode();
					progress.Report((int)(100d * (offset + file.Position) / total));
					block++;
				}
			}

			await SendFile(package, true, 0);
			if (signature is not null)
				await SendFile(signature, false, package.Length);
		}
		finally
		{
			// End the private web session even if the operator cancelled a transfer.
			using CancellationTokenSource logoutTimeout = new(TimeSpan.FromSeconds(5));
			try
			{
				using HttpResponseMessage logout = await client.PostAsync("Logout", null, logoutTimeout.Token);
			}
			catch (HttpRequestException) { }
			catch (OperationCanceledException) { }
		}
	}
}
