using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App;

/// <summary>Engine services the desktop host hands to the UI.</summary>
public sealed record AppServices(AccountManager Accounts, MonitorService Monitors, FleetService Fleet, Engine.Chat.ChatService Chat, Engine.Identity.IdentityService Identities, Engine.Devices.DevicesService Devices, Engine.PubSub.PubSubService PubSub, string DataFolder)
{
	public EventHub Events => this.Monitors.Hub;
}
