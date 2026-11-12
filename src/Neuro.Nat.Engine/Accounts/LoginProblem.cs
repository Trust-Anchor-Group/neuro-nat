using Waher.Networking.XMPP.AuthenticationErrors;

namespace Neuro.Nat.Engine.Accounts;

/// <summary>Why a login was refused, when the server says so. Drives what the UI offers.</summary>
public enum LoginProblem
{
	/// <summary>No login problem known.</summary>
	None,

	/// <summary>No password is stored for the account.</summary>
	NoStoredPassword,

	/// <summary>The server rejected the user name or password.</summary>
	WrongCredentials,

	/// <summary>
	/// The server's login auditor temporarily blocks this endpoint after repeated failed
	/// logins. Retrying makes it worse; wait, or have an administrator unblock it.
	/// </summary>
	TemporarilyBlocked,

	/// <summary>The account is disabled on the server.</summary>
	AccountDisabled,

	/// <summary>The credentials have expired.</summary>
	CredentialsExpired,

	/// <summary>Some other authentication error.</summary>
	Other,
}

internal static class LoginProblems
{
	/// <summary>Classifies an exception raised during login. Returns null for non-login errors.</summary>
	public static LoginProblem? Classify(Exception ex) => ex switch
	{
		TemporaryAuthFailureException => LoginProblem.TemporarilyBlocked,
		NotAuthorizedException => LoginProblem.WrongCredentials,
		AccountDisabledException => LoginProblem.AccountDisabled,
		CredentialsExpiredException => LoginProblem.CredentialsExpired,
		AuthenticationException => LoginProblem.Other,
		_ => null,
	};
}
