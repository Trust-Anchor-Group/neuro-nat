using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.HTTPX;

namespace Neuro.Nat.Features.Telemetry;

/// <summary>
/// Where a Neuron's web API is, and the short-lived token that opens it. NAT keeps no password
/// for the web: each round asks the Neuron for a fresh token over the account's XMPP connection.
/// </summary>
internal static class NeuronWeb
{
	/// <summary>
	/// The addresses to try, best first: the account's own web address when it has one, then the
	/// web addresses in the Neuron's own notifications (as the Identity tab opens its admin pages),
	/// then https on the usual port at its domain. Plain http only on this computer, so a token
	/// never crosses the network unencrypted.
	/// </summary>
	public static IReadOnlyList<Uri> Candidates(string? accountWebAddress, AttentionInbox? notifications, Guid accountId, string domain)
	{
		List<Uri> list = [];
		void Add(Uri u)
		{
			if (!list.Any(x => Uri.Compare(x, u, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0))
				list.Add(u);
		}

		if (AccountWeb.Check(accountWebAddress) is string own)
			Add(new Uri(own));

		foreach (NeuronNotification n in notifications?.Items ?? [])
		{
			if (n.AccountId != accountId || !string.Equals(NeuronNames.Domain(n.NeuronJid), domain, StringComparison.OrdinalIgnoreCase))
				continue;

			foreach (NotificationLink l in n.Links)
			{
				if (Uri.TryCreate(l.Url, UriKind.Absolute, out Uri? u) &&
					(u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && IsThisComputer(u.Host))))
				{
					Add(new Uri(u.GetLeftPart(UriPartial.Authority) + "/"));
				}
			}
		}

		if (Uri.CheckHostName(domain) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
			Add(new Uri("https://" + domain + "/"));

		return list;
	}

	public static bool IsThisComputer(string host) => AccountWeb.IsThisComputer(host);
}

/// <summary>
/// A request that goes to a Neuron over XMPP instead of over the web: through this account, to
/// the Neuron's own client. Set on the request, so everything else about it stays as for the web.
/// </summary>
internal sealed record XmppRoute(Guid AccountId, string NeuronJid)
{
	public static readonly HttpRequestOptionsKey<XmppRoute> Option = new("Neuro.Nat.XmppRoute");

	/// <summary>
	/// What stands in for a web address when the API is reached over XMPP. Never looked up: the
	/// route on the request says where it goes.
	/// </summary>
	public static readonly Uri Address = new(Scheme + "://neuron/");

	/// <summary>The scheme of HTTP over XMPP (XEP-0332).</summary>
	public const string Scheme = "httpx";

	public static bool Is(Uri? address) => string.Equals(address?.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Sends a request over XMPP when it carries an <see cref="XmppRoute"/>, and over the web
/// otherwise. A request with a route never reaches the web: its address is not one.
/// </summary>
internal sealed class XmppRoutingHandler(HttpMessageHandler web, Func<XmppRoute, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> overXmpp)
	: DelegatingHandler(web)
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (request.Options.TryGetValue(XmppRoute.Option, out XmppRoute? route))
			return overXmpp(route, request, cancellationToken);

		if (XmppRoute.Is(request.RequestUri))
			throw new HttpRequestException("A request over XMPP without a Neuron to send it to.");

		return base.SendAsync(request, cancellationToken);
	}
}

/// <summary>
/// What NAT does over an account's XMPP connection to reach a Neuron's web API: it asks the
/// account's own Neuron for a token, and it carries whole requests to another Neuron's own client
/// (HTTP over XMPP, XEP-0332). One at a time per app.
/// </summary>
/// <remarks>
/// A request over XMPP needs no token: the Neuron goes by the address it came from. That is how an
/// account on one Neuron reads the API of another Neuron it administers, where it has no account
/// to get a token for.
/// </remarks>
/// <param name="accounts">The accounts whose connections are used.</param>
/// <param name="monitors">Where a Neuron's own client is online, for requests carried to it.</param>
/// <param name="encryption">The end-to-end encryption on an account's connection. A Neuron takes requests only encrypted.</param>
/// <param name="fetchKeys">Makes sure an online resource's keys are known. Gives null when they are, else why not.</param>
internal sealed class XmppTokens(AccountManager accounts, MonitorService? monitors = null,
	Func<Guid, IEndToEndEncryption?>? encryption = null, Func<Guid, string, Task<string?>>? fetchKeys = null) : IDisposable
{
	/// <summary>How long a token lives. A round takes seconds.</summary>
	private const int Seconds = 60;

	/// <summary>The largest piece of an answer in one stanza. Larger answers come in several.</summary>
	private const int MaxChunkSize = 16384;

	/// <summary>How long a Neuron has to answer, and then to finish sending an answer that comes in pieces.</summary>
	private static readonly TimeSpan answerTimeout = TimeSpan.FromSeconds(30);

	private readonly SemaphoreSlim gate = new(1, 1);

	public async Task<string?> GetAsync(Guid accountId, CancellationToken cancel)
	{
		AccountSession? session = accounts.Get(accountId);
		if (session?.Client is not XmppClient client || session.Status != ConnectionStatus.Connected)
			return null;

		// The HTTPX client registers handlers on the connection while it lives; one at a time.
		await this.gate.WaitAsync(cancel);
		try
		{
			using HttpxClient httpx = new(client, 65536);
			return await httpx.GetJwtTokenAsync(Seconds);
		}
		finally
		{
			this.gate.Release();
		}
	}

	/// <summary>
	/// Carries a GET to a Neuron's own client over XMPP and gives its answer as the web would.
	/// Throws <see cref="HttpRequestException"/> when the account is not connected, the Neuron is
	/// not online through it, or it does not answer.
	/// </summary>
	public async Task<HttpResponseMessage> SendAsync(XmppRoute route, HttpRequestMessage request, CancellationToken cancel)
	{
		if (request.Method != HttpMethod.Get || request.RequestUri is not Uri uri)
			throw new HttpRequestException("Only GET goes over XMPP.");

		AccountSession? session = accounts.Get(route.AccountId);
		if (session?.Client is not XmppClient client || session.Status != ConnectionStatus.Connected)
			throw new HttpRequestException("The account is not connected.");

		// The Neuron's client as it is online now. Null when its events do not stream: then this account is not an administrator there.
		string? to = monitors?.For(route.AccountId)?.Concentrator(route.NeuronJid)?.FullJid;
		if (to is null)
			throw new HttpRequestException("The Neuron is not online through this account.");

		// No Authorization: a token for one Neuron is nothing to send to another.
		List<Waher.Networking.HTTP.HttpField> headers = [new("Host", route.NeuronJid)];
		foreach (KeyValuePair<string, IEnumerable<string>> h in request.Headers)
		{
			if (!string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase) && !string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase))
				headers.Add(new Waher.Networking.HTTP.HttpField(h.Key, string.Join(", ", h.Value)));
		}

		// A Neuron takes HTTP over XMPP only end-to-end encrypted. The request is encrypted to the keys of the
		// Neuron's client: from its presence, or asked for now. Without them it goes as it is, and the Neuron says no.
		IEndToEndEncryption? e2e = encryption?.Invoke(route.AccountId);

		await this.gate.WaitAsync(cancel);
		try
		{
			string? noKeys = e2e is null ? "no encryption on this connection" : fetchKeys is null ? null : await fetchKeys(route.AccountId, to).WaitAsync(cancel);
			bool encrypted = e2e is not null && noKeys is null;

			using HttpxClient httpx = e2e is null ? new(client, MaxChunkSize) : new(client, e2e, MaxChunkSize);
			TaskCompletionSource<HttpResponseMessage> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
			MemoryStream body = new();
			HttpResponseMessage? head = null;

			void Finish()
			{
				if (head is null)
					return;

				ByteArrayContent content = new(body.ToArray());
				if (head.Headers.TryGetValues(ContentTypeHeld, out IEnumerable<string>? types) &&
					System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(types.FirstOrDefault(), out System.Net.Http.Headers.MediaTypeHeaderValue? type))
				{
					content.Headers.ContentType = type;
				}

				head.Headers.Remove(ContentTypeHeld);
				head.Content = content;
				done.TrySetResult(head);
			}

			await httpx.Request(to, "GET", uri.PathAndQuery,
				(_, e) =>
				{
					if (!e.Ok)
					{
						// What the far end (or a server on the way) said instead of an answer, for the log.
						string why = e.StanzaError is Exception error ? error.GetType().Name.Replace("Exception", string.Empty, StringComparison.Ordinal) : e.ErrorType.ToString();
						string? text = Printable(e.ErrorText);
						string how = encrypted ? string.Empty : " Sent unencrypted: " + noKeys + ".";
						done.TrySetException(new HttpRequestException("The Neuron did not answer over XMPP: " + why + (string.IsNullOrWhiteSpace(text) ? "." : ", \"" + text + "\".") + how));
						return Task.CompletedTask;
					}

					head = new HttpResponseMessage((System.Net.HttpStatusCode)e.StatusCode) { ReasonPhrase = Printable(e.StatusMessage), RequestMessage = request };
					foreach (KeyValuePair<string, string> h in e.HttpResponse?.GetHeaders() ?? [])
					{
						// The content type belongs to the content, which is made when the body is whole.
						string name = string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ? ContentTypeHeld : h.Key;
						head.Headers.TryAddWithoutValidation(name, h.Value);
					}

					if (e.HasData && e.Data is null)
						return Task.CompletedTask;   // The body follows in pieces.

					if (e.Data is byte[] whole)
						body.Write(whole, 0, whole.Length);
					Finish();
					return Task.CompletedTask;
				},
				(_, d) =>
				{
					if (d.Data is byte[] piece)
					{
						lock (body)
							body.Write(piece, 0, piece.Length);
					}

					if (d.Last)
						Finish();
					return Task.CompletedTask;
				},
				null, [.. headers]);

			try
			{
				return await done.Task.WaitAsync(answerTimeout, cancel);
			}
			catch (TimeoutException)
			{
				throw new HttpRequestException("The Neuron did not answer over XMPP in time.");
			}
		}
		finally
		{
			this.gate.Release();
		}
	}

	/// <summary>Where the content type waits among the answer's headers until the body is whole.</summary>
	private const string ContentTypeHeld = "X-Nat-Content-Type";

	/// <summary>A status message as a reason phrase may be: one line.</summary>
	private static string? Printable(string? message)
		=> string.IsNullOrEmpty(message) ? null : new string([.. message.Where(c => !char.IsControl(c))]);

	public void Dispose() => this.gate.Dispose();
}
