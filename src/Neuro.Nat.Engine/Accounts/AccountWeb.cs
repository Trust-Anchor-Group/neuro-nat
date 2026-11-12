using System.Net;

namespace Neuro.Nat.Engine.Accounts;

/// <summary>
/// The web address of an account's Neuron. Plain http is only for this computer, so nothing NAT
/// sends over the web (a token, for one) crosses the network unencrypted.
/// </summary>
public static class AccountWeb
{
	/// <summary>
	/// A web address as NAT keeps it ("https://lab.example.org/"), or null when it is not one NAT
	/// will use: not http or https, or http to another computer. A bare host name gets https.
	/// </summary>
	public static string? Check(string? text)
	{
		string t = (text ?? string.Empty).Trim();
		if (t.Length == 0)
			return null;

		if (!t.Contains("://", StringComparison.Ordinal))
			t = "https://" + t;

		if (!Uri.TryCreate(t, UriKind.Absolute, out Uri? u) || string.IsNullOrEmpty(u.Host))
			return null;

		if (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && IsThisComputer(u.Host)))
			return u.GetLeftPart(UriPartial.Authority) + "/";

		return null;
	}

	/// <summary>True for localhost and loopback addresses.</summary>
	public static bool IsThisComputer(string host)
		=> string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
			(IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? ip) && IPAddress.IsLoopback(ip));
}
