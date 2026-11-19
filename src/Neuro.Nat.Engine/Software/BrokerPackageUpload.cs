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
	public static async Task SendAsync(Uri broker, string user, string password, string packagePath, string? signaturePath,
		bool downloadable, IProgress<int> progress, CancellationToken cancel)
	{
		await using BrokerUploadSessions sessions = new();
		await sessions.UploadAsync(Guid.Empty, broker, UploadSignIn.WebAccount, user, password, packagePath, signaturePath, downloadable, progress, cancel);
	}

	internal static async Task PasswordLoginAsync(HttpClient client, string user, string password, CancellationToken cancel)
	{
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
	}

	internal static async Task SendFilesAsync(HttpClient client, FileStream package, FileStream? signature, bool downloadable,
		IProgress<int> progress, CancellationToken cancel)
	{
		progress.Report(0);
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
				if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
					throw new InvalidOperationException("The broker refused the upload. Sign in again with an account that has Admin.Software.Upload.");
				response.EnsureSuccessStatusCode();
				progress.Report((int)(100d * (offset + file.Position) / total));
				block++;
			}
		}

		await SendFile(package, true, 0);
		if (signature is not null)
			await SendFile(signature, false, package.Length);
	}
}
