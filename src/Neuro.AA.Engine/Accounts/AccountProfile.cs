namespace Neuro.AA.Engine.Accounts;

/// <summary>
/// Non-secret description of an XMPP account the operator uses. Secrets live in
/// <see cref="ICredentialVault"/>, never here.
/// </summary>
public sealed record AccountProfile
{
	/// <summary>Stable identifier, also the key in the credential vault.</summary>
	public Guid Id { get; init; } = Guid.NewGuid();

	/// <summary>Name shown in the UI. Defaults to the bare JID.</summary>
	public string DisplayName { get; init; } = string.Empty;

	/// <summary>Host name or IP address of the Neuron.</summary>
	public required string Host { get; init; }

	/// <summary>XMPP client-to-server port.</summary>
	public int Port { get; init; } = 5222;

	/// <summary>
	/// XMPP domain, when it differs from <see cref="Host"/> (common for local Neurons reached
	/// as localhost). Empty means the host name is the domain.
	/// </summary>
	public string Domain { get; init; } = string.Empty;

	/// <summary>Account (user) name, without the domain.</summary>
	public required string UserName { get; init; }

	/// <summary>
	/// Accept the server certificate even if it cannot be validated. Needed for local
	/// Neurons with self-signed certificates; never on by default.
	/// </summary>
	public bool TrustServerCertificate { get; init; }

	/// <summary>
	/// Allow PLAIN, CRAM-MD5 and DIGEST-MD5. Off by default; SCRAM is used otherwise.
	/// </summary>
	public bool AllowInsecureMechanisms { get; init; }

	/// <summary>
	/// When true, the app refuses to send commands that change anything on the server
	/// through this account. Meant for production accounts.
	/// </summary>
	public bool ReadOnly { get; init; }

	/// <summary>Index into the UI's account colour palette.</summary>
	public int ColorIndex { get; init; }

	/// <summary>Connect automatically when the app starts.</summary>
	public bool ConnectOnStartup { get; init; } = true;

	/// <summary>Label for logs and the UI when no display name is set.</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public string Label => string.IsNullOrWhiteSpace(this.DisplayName) ? this.UserName + "@" + this.EffectiveDomain : this.DisplayName;

	/// <summary>The XMPP domain actually used.</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public string EffectiveDomain => string.IsNullOrWhiteSpace(this.Domain) ? this.Host : this.Domain;
}
