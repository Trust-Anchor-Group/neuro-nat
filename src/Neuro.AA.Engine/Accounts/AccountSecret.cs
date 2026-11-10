namespace Neuro.AA.Engine.Accounts;

/// <summary>
/// Secret used to log in. After the first successful login the engine replaces a plain
/// password with the hash the server negotiated, so the plain password is not kept.
/// </summary>
public sealed record AccountSecret
{
	/// <summary>Plain password, or the password hash when <see cref="HashMethod"/> is set.</summary>
	public required string Value { get; init; }

	/// <summary>Hash method reported by the XMPP client (for example SCRAM-SHA-256), or empty for a plain password.</summary>
	public string HashMethod { get; init; } = string.Empty;

	/// <summary>True when <see cref="Value"/> is a hash rather than a password.</summary>
	public bool IsHash => !string.IsNullOrEmpty(this.HashMethod);

	/// <summary>Creates a secret from a plain password.</summary>
	public static AccountSecret FromPassword(string password) => new() { Value = password };

	/// <summary>Creates a secret from a negotiated password hash.</summary>
	public static AccountSecret FromHash(string hash, string method) => new() { Value = hash, HashMethod = method };

	/// <summary>Never print secrets.</summary>
	public override string ToString() => this.IsHash ? $"[hash:{this.HashMethod}]" : "[password]";
}
