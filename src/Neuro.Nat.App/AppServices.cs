using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App;

/// <summary>Engine services the desktop host hands to the UI.</summary>
public sealed record AppServices(AccountManager Accounts, MonitorService Monitors, FleetService Fleet, Engine.Chat.ChatService Chat, Engine.Identity.IdentityService Identities, Engine.Devices.DevicesService Devices, Engine.PubSub.PubSubService PubSub, string DataFolder)
{
	public EventHub Events => this.Monitors.Hub;

	/// <summary>Start in the tray without showing the window (as when started with Windows).</summary>
	public bool StartHidden { get; init; }

	/// <summary>A NAT link NAT was started with, opened once the app is up.</summary>
	public string? StartLink { get; init; }

	/// <summary>"Start with Windows", where the host supports it.</summary>
	public Watch.IAutostart? Autostart { get; init; }

	/// <summary>Updates of NAT itself, when it was installed with its installer.</summary>
	public Watch.IAppUpdates? Updates { get; init; }

	/// <summary>Where the alert settings are kept. In memory unless the host gives a store.</summary>
	public Engine.Alerts.IAlertSettingsStore AlertSettings { get; init; } = new Engine.Alerts.MemoryAlertSettingsStore();

	/// <summary>Where the look, sizes and windows are kept. In memory unless the host gives a store.</summary>
	public ViewModels.IUiSettingsStore UiSettings { get; init; } = new ViewModels.MemoryUiSettingsStore();

	/// <summary>Copies of what comparisons read. In memory unless the host gives a store.</summary>
	public Engine.Storage.INeuronCopyStore NeuronCopies { get; init; } = new Engine.Storage.MemoryNeuronCopyStore();
	/// <summary>Where runbooks and the records of their runs are kept. In memory unless the host gives a store.</summary>
	public Engine.Storage.IRunbookStore Runbooks { get; init; } = new Engine.Storage.InMemoryRunbookStore();

	/// <summary>Set for a kiosk (<c>--kiosk</c>): only the Big screen, full screen, for good. Null for a normal start.</summary>
	public KioskPolicy? Kiosk { get; init; }

	/// <summary>False where there is no encrypted database yet (macOS and Linux): nothing is kept between runs.</summary>
	public bool KeepsData { get; init; }

	/// <summary>The features the host registered at start, such as app telemetry. None unless the host gives some.</summary>
	public global::Neuro.Nat.App.Features.FeatureSet Features { get; init; } = global::Neuro.Nat.App.Features.FeatureSet.None();
}
