using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.Engine.Links;

/// <summary>What a link points at.</summary>
public enum LinkKind
{
	/// <summary>A Neuron's page, on one of its tabs.</summary>
	Neuron,

	/// <summary>A problem: events grouped by what went wrong.</summary>
	Problem,

	/// <summary>One event, at its time.</summary>
	Event,

	/// <summary>A data source or a node in a Neuron's Sources.</summary>
	Node,

	/// <summary>A conversation with a person.</summary>
	Chat,

	/// <summary>A room.</summary>
	Room,
}

/// <summary>
/// A link to something in NAT that a person can read and pass on, such as
/// <c>neuro-nat://neuron/lab.example.com/software</c> or <c>neuro-nat://event/lab.example.com/3fa2c19e04b7d851?at=2026-11-12T09:42:13Z</c>.
/// A Neuron is named by its domain, never by the account that reaches it, and a link carries no
/// sign-in. Following one only opens a screen. Every part of a link that comes from outside is
/// checked here before anything looks at it: <see cref="TryParse"/> never throws.
/// </summary>
/// <param name="Kind">What it points at.</param>
/// <param name="Domain">The Neuron it is on, by its domain (lower case). For a person or a room, the domain of their address.</param>
/// <param name="Parts">
/// The rest of the path. A Neuron: its tab, or nothing for its Overview. A problem: its
/// fingerprint. An event: its id. A node: the ids from the data source down to it. A person or
/// a room: their bare address.
/// </param>
/// <param name="At">An event's time, in UTC.</param>
public sealed record NatLink(LinkKind Kind, string Domain, IReadOnlyList<string> Parts, DateTime? At = null)
{
	/// <summary>The scheme, without "://".</summary>
	public const string Scheme = "neuro-nat";

	/// <summary>"neuro-nat://".</summary>
	public const string Prefix = Scheme + "://";

	/// <summary>Longer than this is not a link NAT made.</summary>
	public const int MaxLength = 2048;

	/// <summary>A node deeper than this is not followed.</summary>
	public const int MaxDepth = 32;

	private const int MaxPart = 1024;

	/// <summary>The tabs of a Neuron's page, as a link names them. The Overview has no name.</summary>
	public static readonly IReadOnlyList<string> Tabs = ["events", "sources", "console", "software", "identity", "storage"];

	/// <summary>The words for the kinds, as a link spells them.</summary>
	private static readonly Dictionary<string, LinkKind> kinds = new(StringComparer.OrdinalIgnoreCase)
	{
		["neuron"] = LinkKind.Neuron,
		["problem"] = LinkKind.Problem,
		["event"] = LinkKind.Event,
		["node"] = LinkKind.Node,
		["chat"] = LinkKind.Chat,
		["room"] = LinkKind.Room,
	};

	// ----- Making links -----

	/// <summary>A Neuron's page, on a tab from <see cref="Tabs"/>, or on its Overview when <paramref name="tab"/> is null.</summary>
	public static NatLink Neuron(string neuronJid, string? tab = null)
		=> new(LinkKind.Neuron, DomainOf(neuronJid), tab is null ? [] : [tab]);

	/// <summary>A problem, by its fingerprint, on the Neuron where it was seen.</summary>
	public static NatLink Problem(string neuronJid, string fingerprint)
		=> new(LinkKind.Problem, DomainOf(neuronJid), [fingerprint.ToLowerInvariant()]);

	/// <summary>One event, with its time so it can be found again, or its time shown when the event itself is gone.</summary>
	public static NatLink Event(NeuronEvent e)
		=> new(LinkKind.Event, DomainOf(e.Source.NeuronJid), [EventId(e)], Second(ToUtc(e.Timestamp)));

	/// <summary>A data source or node, by the ids from the data source down to it.</summary>
	public static NatLink Node(string neuronJid, IReadOnlyList<string> path)
		=> new(LinkKind.Node, DomainOf(neuronJid), [.. path]);

	/// <summary>A conversation with a person.</summary>
	public static NatLink Chat(string bareJid)
		=> new(LinkKind.Chat, DomainOf(bareJid), [bareJid.ToLowerInvariant()]);

	/// <summary>A room.</summary>
	public static NatLink Room(string roomJid)
		=> new(LinkKind.Room, DomainOf(roomJid), [roomJid.ToLowerInvariant()]);

	/// <summary>
	/// The id an event has in a link: the same on every computer that has the event, whether it
	/// came live or was read back from the Neuron's log. 16 hex digits.
	/// </summary>
	public static string EventId(NeuronEvent e)
	{
		string key = string.Join("\u001f",
			DomainOf(e.Source.NeuronJid),
			Second(ToUtc(e.Timestamp)).Ticks.ToString(CultureInfo.InvariantCulture),
			e.EventId ?? string.Empty,
			e.Object ?? string.Empty,
			e.Message.Trim());
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8).ToLowerInvariant();
	}

	/// <summary>The tab, for a Neuron. Null for its Overview.</summary>
	public string? Tab => this.Kind == LinkKind.Neuron && this.Parts.Count > 0 ? this.Parts[0] : null;

	/// <summary>The fingerprint, the event id, or the address: the one part a problem, an event, a person and a room have.</summary>
	public string Id => this.Parts.Count > 0 ? this.Parts[^1] : string.Empty;

	public override string ToString()
	{
		StringBuilder sb = new(Prefix);
		sb.Append(this.Kind.ToString().ToLowerInvariant());
		if (this.Kind is not (LinkKind.Chat or LinkKind.Room))
			sb.Append('/').Append(Escape(this.Domain));
		foreach (string p in this.Parts)
			sb.Append('/').Append(Escape(p));
		if (this.At is DateTime at)
			sb.Append("?at=").Append(at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
		return sb.ToString();
	}

	// ----- Reading links -----

	/// <summary>True when the text starts like a NAT link, whether or not the rest is right.</summary>
	public static bool LooksLike(string? text)
		=> text is not null && text.TrimStart().StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Reads a link. Returns false for anything that is not a whole, well-formed NAT link: too
	/// long, another scheme, an unknown kind, a domain or an id with characters it cannot have,
	/// a path that is too deep, or a part that tries to step out of where it is. Never throws.
	/// </summary>
	public static bool TryParse(string? text, out NatLink link)
	{
		link = null!;
		try
		{
			return TryRead(text, out link);
		}
		catch (Exception)
		{
			// Whatever was missed above: garbage is not a link.
			link = null!;
			return false;
		}
	}

	private static bool TryRead(string? text, out NatLink link)
	{
		link = null!;
		if (string.IsNullOrWhiteSpace(text))
			return false;

		text = text.Trim();
		if (text.Length > MaxLength || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
			return false;

		// Only printable ASCII: anything else in a link is percent-encoded by whoever made it.
		foreach (char c in text)
		{
			if (c <= ' ' || c > '~')
				return false;
		}

		string rest = text[Prefix.Length..];
		if (rest.Contains('#'))
			return false;

		string? query = null;
		int q = rest.IndexOf('?');
		if (q >= 0)
		{
			query = rest[(q + 1)..];
			rest = rest[..q];
		}

		// One slash at the very end is forgiven; empty parts anywhere else are not.
		if (rest.EndsWith('/'))
			rest = rest[..^1];

		string[] raw = rest.Split('/');
		if (raw.Length < 2 || raw.Length > MaxDepth + 2 || !kinds.TryGetValue(raw[0], out LinkKind kind))
			return false;

		List<string> parts = new(raw.Length - 1);
		for (int i = 1; i < raw.Length; i++)
		{
			if (!TryUnescape(raw[i], out string part))
				return false;
			parts.Add(part);
		}

		DateTime? at = null;
		if (query is not null && !TryReadQuery(query, out at))
			return false;

		switch (kind)
		{
			case LinkKind.Neuron:
				if (!IsDomain(parts[0]) || parts.Count > 2 || at is not null)
					return false;
				if (parts.Count == 2 && !Tabs.Contains(parts[1].ToLowerInvariant()))
					return false;
				link = new NatLink(kind, parts[0].ToLowerInvariant(), parts.Count == 2 ? [parts[1].ToLowerInvariant()] : []);
				return true;

			case LinkKind.Problem:
			case LinkKind.Event:
				if (parts.Count != 2 || !IsDomain(parts[0]) || !IsHex16(parts[1]))
					return false;
				if (kind == LinkKind.Problem && at is not null)
					return false;
				link = new NatLink(kind, parts[0].ToLowerInvariant(), [parts[1].ToLowerInvariant()], at);
				return true;

			case LinkKind.Node:
				if (parts.Count < 2 || !IsDomain(parts[0]) || at is not null)
					return false;
				for (int i = 1; i < parts.Count; i++)
				{
					if (!IsId(parts[i]))
						return false;
				}
				link = new NatLink(kind, parts[0].ToLowerInvariant(), [.. parts.Skip(1)]);
				return true;

			default:
				if (parts.Count != 1 || at is not null || !IsBareJid(parts[0]))
					return false;
				string jid = parts[0].ToLowerInvariant();
				link = new NatLink(kind, DomainOf(jid), [jid]);
				return true;
		}
	}

	private static bool TryReadQuery(string query, out DateTime? at)
	{
		at = null;
		foreach (string pair in query.Split('&'))
		{
			int eq = pair.IndexOf('=');
			if (eq <= 0)
				return false;

			string name = pair[..eq];
			if (!string.Equals(name, "at", StringComparison.OrdinalIgnoreCase))
				continue;   // Something a later NAT may add: not this one's business.

			if (at is not null || !TryUnescape(pair[(eq + 1)..], out string value))
				return false;

			if (!DateTime.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture,
				DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t))
			{
				return false;
			}

			at = DateTime.SpecifyKind(t, DateTimeKind.Utc);
		}

		return true;
	}

	/// <summary>Finds the NAT links in a piece of text, such as a chat message, at most <paramref name="max"/>, each once.</summary>
	public static IReadOnlyList<NatLink> FindAll(string? text, int max = 4)
	{
		List<NatLink> found = [];
		if (string.IsNullOrEmpty(text))
			return found;

		int from = 0;
		while (found.Count < max && from < text.Length)
		{
			int i = text.IndexOf(Prefix, from, StringComparison.OrdinalIgnoreCase);
			if (i < 0)
				break;

			// A link ends where Markdown or a sentence would end it.
			int end = i;
			while (end < text.Length && end - i <= MaxLength && text[end] > ' ' && text[end] <= '~' && text[end] is not ('<' or '>' or '`' or '"' or ')' or ']' or '|' or '*'))
				end++;
			string candidate = text[i..end].TrimEnd('.', ',', ';', ':', '!', '\'');
			if (TryParse(candidate, out NatLink link) && !found.Contains(link))
				found.Add(link);

			from = Math.Max(end, i + Prefix.Length);
		}

		return found;
	}

	public bool Equals(NatLink? other)
		=> other is not null && this.Kind == other.Kind && this.Domain == other.Domain && this.At == other.At && this.Parts.SequenceEqual(other.Parts);

	public override int GetHashCode() => HashCode.Combine(this.Kind, this.Domain, this.Parts.Count > 0 ? this.Parts[^1] : null);

	// ----- The rules for each part -----

	/// <summary>The domain of a JID, as <see cref="NeuronNames.Domain"/> gives it, in lower case.</summary>
	public static string DomainOf(string jid) => NeuronNames.Domain(jid).ToLowerInvariant();

	/// <summary>A host name: letters, digits, hyphens and dots, in labels of at most 63, 253 in all.</summary>
	internal static bool IsDomain(string s)
	{
		if (s.Length is 0 or > 253 || s[0] == '.' || s[^1] == '.' || s.Contains("..", StringComparison.Ordinal))
			return false;

		foreach (string label in s.Split('.'))
		{
			if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
				return false;
			foreach (char c in label)
			{
				if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
					return false;
			}
		}

		return true;
	}

	private static bool IsHex16(string s) => s.Length == 16 && s.All(char.IsAsciiHexDigit);

	/// <summary>A data source or node id: no control characters, and never "." or "..".</summary>
	private static bool IsId(string s)
		=> s.Length is > 0 and <= MaxPart && s is not ("." or "..") && !s.Any(char.IsControl) && !s.Any(char.IsSurrogate);

	/// <summary>A bare address: a local part without the characters XMPP forbids there, "@", and a domain. No resource.</summary>
	private static bool IsBareJid(string s)
	{
		int at = s.IndexOf('@');
		if (at <= 0 || at > 1023 || s.IndexOf('@', at + 1) >= 0)
			return false;

		foreach (char c in s.AsSpan(0, at))
		{
			if (char.IsControl(c) || char.IsWhiteSpace(c) || char.IsSurrogate(c) || c is '"' or '&' or '\'' or '/' or ':' or '<' or '>' or '\\')
				return false;
		}

		return IsDomain(s[(at + 1)..]);
	}

	/// <summary>Kept as they are in a link: what reads plainly and needs no escaping.</summary>
	private static bool Plain(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '@';

	private static string Escape(string s)
	{
		StringBuilder sb = new(s.Length);
		foreach (byte b in Encoding.UTF8.GetBytes(s))
		{
			char c = (char)b;
			if (b < 0x80 && Plain(c))
				sb.Append(c);
			else
				sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
		}

		return sb.ToString();
	}

	/// <summary>Percent-decodes one part. False for a bad escape, bytes that are not UTF-8, or a part that is too long or empty.</summary>
	private static bool TryUnescape(string s, out string value)
	{
		value = string.Empty;
		if (s.Length is 0 or > MaxPart * 3)
			return false;

		List<byte> bytes = new(s.Length);
		for (int i = 0; i < s.Length; i++)
		{
			char c = s[i];
			if (c == '%')
			{
				if (i + 2 >= s.Length || !char.IsAsciiHexDigit(s[i + 1]) || !char.IsAsciiHexDigit(s[i + 2]))
					return false;
				bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
				i += 2;
			}
			else
				bytes.Add((byte)c);
		}

		try
		{
			value = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString([.. bytes]);
		}
		catch (ArgumentException)
		{
			return false;
		}

		return value.Length is > 0 and <= MaxPart;
	}

	private static DateTime ToUtc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();

	private static DateTime Second(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}
