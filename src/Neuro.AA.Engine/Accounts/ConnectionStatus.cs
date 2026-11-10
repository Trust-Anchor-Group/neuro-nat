using Waher.Networking.XMPP;

namespace Neuro.AA.Engine.Accounts;

/// <summary>Connection status as the operator sees it. Coarser than <see cref="XmppState"/>.</summary>
public enum ConnectionStatus
{
	/// <summary>Not connected and not trying to.</summary>
	Offline,

	/// <summary>Opening the connection, negotiating the stream and encryption.</summary>
	Connecting,

	/// <summary>Logging in, binding the resource and fetching the roster.</summary>
	Authenticating,

	/// <summary>Online.</summary>
	Connected,

	/// <summary>The connection was lost; waiting before the next attempt.</summary>
	Reconnecting,

	/// <summary>Gave up; needs the operator (for example a wrong password).</summary>
	Failed,
}

internal static class ConnectionStatusMapping
{
	public static ConnectionStatus FromXmpp(XmppState state) => state switch
	{
		XmppState.Offline => ConnectionStatus.Offline,
		XmppState.Connecting or XmppState.StreamNegotiation or XmppState.StreamOpened or XmppState.StartingEncryption => ConnectionStatus.Connecting,
		XmppState.Authenticating or XmppState.Registering or XmppState.Binding or XmppState.RequestingSession
			or XmppState.FetchingRoster or XmppState.SettingPresence => ConnectionStatus.Authenticating,
		XmppState.Connected => ConnectionStatus.Connected,
		XmppState.Error => ConnectionStatus.Failed,
		_ => ConnectionStatus.Offline,
	};
}
