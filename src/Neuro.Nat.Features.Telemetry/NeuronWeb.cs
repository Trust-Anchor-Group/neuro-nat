using System.Net;
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
	/// The addresses to try, best first: the web addresses in the Neuron's own notifications (as
	/// the Identity tab opens its admin pages), then https on the usual port at its domain. Plain
	/// http only on this computer, so a token never crosses the network unencrypted.
	/// </summary>
	public static IReadOnlyList<Uri> Candidates(AttentionInbox? notifications, Guid accountId, string domain)
	{
		List<Uri> list = [];
		void Add(Uri u)
		{
			if (!list.Any(x => Uri.Compare(x, u, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0))
				list.Add(u);
		}

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

	public static bool IsThisComputer(string host)
		=> string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
			(IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? ip) && IPAddress.IsLoopback(ip));
}

/// <summary>Asks the account's Neuron for a token over XMPP, one request at a time per app.</summary>
internal sealed class XmppTokens(AccountManager accounts) : IDisposable
{
	/// <summary>How long a token lives. A round takes seconds.</summary>
	private const int Seconds = 60;

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

	public void Dispose() => this.gate.Dispose();
}
