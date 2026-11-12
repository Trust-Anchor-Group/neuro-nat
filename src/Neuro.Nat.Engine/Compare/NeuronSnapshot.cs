using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Neuro.Nat.Engine.Compare;

/// <summary>The three parts of a comparison.</summary>
public enum CompareGroup
{
	/// <summary>Build time, version, installed packages and the automatic update setting.</summary>
	Software,

	/// <summary>Ports, domains, the web server, the sign-in guard and where events are sent.</summary>
	Settings,

	/// <summary>Processor, memory, disk and signed-in clients. Shown as background, never counted.</summary>
	StateNow,
}

/// <summary>
/// One value read from a Neuron. A secret (a password, a key, a token) has no value here, only
/// a hash of it, so two Neurons can be told to be the same or different without either value
/// being kept or shown.
/// </summary>
/// <param name="Group">Software, Settings or State now.</param>
/// <param name="Section">The heading it goes under, such as "Ports" or "Installed packages".</param>
/// <param name="Key">What it is, the same on every Neuron: values with the same key are compared.</param>
/// <param name="Label">What it is called on screen.</param>
/// <param name="Value">The value as text. Null for a secret.</param>
/// <param name="SecretHash">For a secret: a hash of the value. Null otherwise.</param>
public sealed record SnapshotValue(CompareGroup Group, string Section, string Key, string Label, string? Value, string? SecretHash = null)
{
	public bool IsSecret => this.SecretHash is not null;

	/// <summary>A value, or a secret kept only as its hash when <paramref name="secret"/> is true.</summary>
	public static SnapshotValue Of(CompareGroup group, string section, string key, string label, string? value, bool secret)
		=> secret
			? new SnapshotValue(group, section, key, label, null, Secrets.Hash(key, value ?? string.Empty))
			: new SnapshotValue(group, section, key, label, value ?? string.Empty);
}

/// <summary>
/// What was read from one Neuron at one time: the values to compare, and, per group, why it
/// could not be read when it could not. Kept as it is when a copy is kept.
/// </summary>
public sealed record NeuronSnapshot
{
	/// <summary>The Neuron's bare JID.</summary>
	public required string NeuronJid { get; init; }

	/// <summary>Its roster name when it has one.</summary>
	public string? Name { get; init; }

	/// <summary>When it was read (UTC).</summary>
	public DateTime ReadAt { get; init; }

	/// <summary>Set for a kept copy: its id.</summary>
	public string? CopyId { get; init; }

	public IReadOnlyList<SnapshotValue> Values { get; init; } = [];

	/// <summary>The groups that could not be read, with the reason in a few words.</summary>
	public IReadOnlyDictionary<CompareGroup, string> NotRead { get; init; } = new Dictionary<CompareGroup, string>();

	/// <summary>True when the account that reached it is not an administrator there, so nothing could be read.</summary>
	public bool NoAccess { get; init; }

	public bool WasRead(CompareGroup group) => !this.NoAccess && !this.NotRead.ContainsKey(group);
}

/// <summary>Which values are secrets, and their hashes.</summary>
public static partial class Secrets
{
	/// <summary>
	/// True for what must never be shown: a password, a secret, a key, a token, a credential or
	/// the private part of a certificate, judged by the parameter's name and label. A field the
	/// Neuron itself marks as private counts too (the caller says so).
	/// </summary>
	public static bool IsSecret(string? name, string? label = null)
		=> (name is not null && Secret().IsMatch(name)) || (label is not null && Secret().IsMatch(label));

	/// <summary>
	/// A hash of a secret: the same for the same value under the same key, on any Neuron and in
	/// any kept copy, and no way back to the value. Only this is kept.
	/// </summary>
	public static string Hash(string key, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes("neuro-nat/compare/1\u001f" + key + "\u001f" + value);
		return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
	}

	[GeneratedRegex(@"pass(word|wd|phrase)|pwd|secret|token|credential|private|key|pfx|pkcs", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Secret();
}
