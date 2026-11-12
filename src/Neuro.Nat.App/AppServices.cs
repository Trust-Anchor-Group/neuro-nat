using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App;

/// <summary>Engine services the desktop host hands to the UI.</summary>
public sealed record AppServices(AccountManager Accounts, MonitorService Monitors, FleetService Fleet, Engine.Chat.ChatService Chat, Engine.Identity.IdentityService Identities, Engine.Devices.DevicesService Devices, Engine.PubSub.PubSubService PubSub, string DataFolder)
{
	public EventHub Events => this.Monitors.Hub;

	/// <summary>Start in the tray without showing the window (as when started with Windows).</summary>
	public bool StartHidden { get; init; }

	/// <summary>"Start with Windows", where the host supports it.</summary>
	public Watch.IAutostart? Autostart { get; init; }

	/// <summary>Updates of NAT itself, when it was installed with its installer.</summary>
	public Watch.IAppUpdates? Updates { get; init; }
}
