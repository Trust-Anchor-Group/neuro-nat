namespace Neuro.AA.Engine.Accounts;

/// <summary>
/// What creating an account on a Neuron needs besides user name and password: an API key and
/// secret from the Neuron's operators, and optionally an e-mail address and phone number.
/// </summary>
public sealed record AccountRegistration(string ApiKey, string ApiSecret, string? EMail = null, string? Phone = null);
