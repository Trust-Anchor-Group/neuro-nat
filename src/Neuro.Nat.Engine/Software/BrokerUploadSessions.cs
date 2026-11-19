using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Neuro.Nat.Engine.Software;

public enum UploadSignIn { ExistingSession, LegalId, WebAccount }

/// <summary>In-memory web sessions for Software uploads, shared by windows using the same NAT account.</summary>
public sealed class BrokerUploadSessions : IAsyncDisposable
{
	private sealed record Session(HttpClient Client, string Identity);

	// The broker tracks upload block numbers across sessions. Serialize transfers to an origin across windows.
	private static readonly HashSet<Uri> activeBrokers = [];
	private readonly Dictionary<(Guid, Uri), Session> sessions = [];
	private readonly Func<Uri, HttpClient> createClient;
	private readonly CancellationTokenSource shutdown = new();
	private bool disposed;

	public BrokerUploadSessions() : this(CreateClient) { }
	internal BrokerUploadSessions(Func<Uri, HttpClient> createClient) => this.createClient = createClient;

	private static HttpClient CreateClient(Uri broker)
	{
		CookieContainer cookies = new();
		cookies.Add(broker, new Cookie("HttpSessionID", Guid.NewGuid().ToString(), "/") { Secure = true });
		HttpClient client = new(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = broker };
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
		return client;
	}

	public string? SignedInAs(Guid account, Uri broker)
	{
		lock (activeBrokers)
			return this.sessions.GetValueOrDefault((account, broker))?.Identity;
	}

	public async Task UploadAsync(Guid account, Uri broker, UploadSignIn mode, string identity, string? password,
		string packagePath, string? signaturePath, bool downloadable, IProgress<int> progress, CancellationToken cancel)
	{
		if (!broker.IsAbsoluteUri || broker.Scheme != Uri.UriSchemeHttps || broker.UserInfo.Length != 0 ||
			broker.PathAndQuery != "/" || broker.Fragment.Length != 0)
			throw new ArgumentException("Enter the broker’s HTTPS address, including its port if needed.");
		if (!Enum.IsDefined(mode))
			throw new ArgumentOutOfRangeException(nameof(mode));
		if (mode != UploadSignIn.ExistingSession && string.IsNullOrWhiteSpace(identity))
			throw new ArgumentException("Enter a Legal ID or web account.");
		if (mode == UploadSignIn.WebAccount && string.IsNullOrEmpty(password))
			throw new ArgumentException("Enter the web account password.");

		using FileStream package = File.OpenRead(packagePath);
		using FileStream? signature = signaturePath is null ? null : File.OpenRead(signaturePath);
		if (package.Length == 0 || signature?.Length == 0)
			throw new InvalidDataException("Choose a nonempty package and, if supplied, a nonempty signature file.");

		Session? session;
		CancellationTokenSource operation;
		lock (activeBrokers)
		{
			ObjectDisposedException.ThrowIf(this.disposed, this);
			if (!activeBrokers.Add(broker))
				throw new InvalidOperationException("Another package upload to this broker is in progress.");
			this.sessions.Remove((account, broker), out session);
			operation = CancellationTokenSource.CreateLinkedTokenSource(this.shutdown.Token, cancel);
		}
		try
		{
			if (mode != UploadSignIn.ExistingSession)
			{
				if (session is { } previous)
				{
					session = null;
					await EndSessionAsync(previous);
				}
				session = new Session(this.createClient(broker), identity);
				if (mode == UploadSignIn.LegalId)
					await LegalIdLoginAsync(session.Client, identity, Path.GetFileName(package.Name), operation.Token);
				else
					await BrokerPackageUpload.PasswordLoginAsync(session.Client, identity, password!, operation.Token);
			}
			if (session is null)
				throw new InvalidOperationException("Sign in to this broker before uploading.");

			await BrokerPackageUpload.SendFilesAsync(session.Client, package, signature, downloadable, progress, operation.Token);
			lock (activeBrokers)
			{
				if (!this.disposed)
				{
					this.sessions[(account, broker)] = session;
					session = null;
				}
			}
		}
		finally
		{
			// A failed or interrupted transfer may leave partial blocks. Use a fresh session on the next attempt.
			try
			{
				if (session is not null)
					await EndSessionAsync(session);
			}
			finally
			{
				operation.Dispose();
				lock (activeBrokers)
					activeBrokers.Remove(broker);
			}
		}
	}

	private static async Task LegalIdLoginAsync(HttpClient client, string legalId, string fileName, CancellationToken cancel)
	{
		string tabId = Guid.NewGuid().ToString();
		string purpose = $"Sign in to {client.BaseAddress!.Authority} for NAT package uploads, starting with {fileName}.";
		using HttpResponseMessage login = await client.PostAsJsonAsync("Agent/Account/RemoteQuickLogin", new { legalId, purpose, tabId }, cancel);
		if (login.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
			throw new InvalidOperationException("Legal ID sign-in is unavailable on this broker.");
		login.EnsureSuccessStatusCode();
		using JsonDocument reply = JsonDocument.Parse(await login.Content.ReadAsStringAsync(cancel));
		if (reply.RootElement.TryGetProperty("loggedIn", out var loggedIn) && loggedIn.ValueKind == JsonValueKind.True)
			return;
		if (!reply.RootElement.TryGetProperty("petitionSent", out var sent) || sent.ValueKind != JsonValueKind.True)
			throw new InvalidOperationException("This broker could not request Legal ID approval.");

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		timeout.CancelAfter(TimeSpan.FromMinutes(5));
		try
		{
			// The broker queues this tab's approval event after it associates the identity with the HTTP session.
			while (true)
			{
				using HttpRequestMessage poll = new(HttpMethod.Post, "ClientEvents");
				poll.Headers.Add("X-TabID", tabId);
				poll.Content = new StringContent(new Uri(client.BaseAddress, "Login.md").AbsoluteUri);
				using HttpResponseMessage response = await client.SendAsync(poll, timeout.Token);
				response.EnsureSuccessStatusCode();
				string json = await response.Content.ReadAsStringAsync(timeout.Token);
				if (string.IsNullOrWhiteSpace(json))
					continue;
				using JsonDocument events = JsonDocument.Parse(json);
				foreach (var item in events.RootElement.EnumerateArray())
					if (item.GetProperty("type").GetString() == "SignatureReceivedBE" &&
						item.GetProperty("data").TryGetProperty("Id", out var id) && string.Equals(id.GetString(), legalId, StringComparison.OrdinalIgnoreCase))
						return;
			}
		}
		catch (OperationCanceledException) when (!cancel.IsCancellationRequested && timeout.IsCancellationRequested)
		{
			throw new TimeoutException("Legal ID approval did not arrive in five minutes.");
		}
	}

	private static async Task EndSessionAsync(Session session)
	{
		using (session.Client)
		using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5)))
		{
			try { using var response = await session.Client.PostAsync("Logout", null, timeout.Token).ConfigureAwait(false); }
			catch (HttpRequestException) { }
			catch (OperationCanceledException) { }
		}
	}

	public async ValueTask DisposeAsync()
	{
		Session[] remaining;
		lock (activeBrokers)
		{
			if (this.disposed)
				return;
			this.disposed = true;
			remaining = [.. this.sessions.Values];
			this.sessions.Clear();
		}
		await this.shutdown.CancelAsync().ConfigureAwait(false);
		this.shutdown.Dispose();
		await Task.WhenAll(remaining.Select(EndSessionAsync)).ConfigureAwait(false);
	}
}
