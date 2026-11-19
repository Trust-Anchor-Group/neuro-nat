namespace Neuro.Nat.App.Watch;

/// <summary>Starting NAT when the user signs in. Provided by the desktop host.</summary>
public interface IAutostart
{
	bool IsEnabled { get; }

	void SetEnabled(bool enabled);
}

/// <summary>Updates of NAT itself, found and downloaded in the background. Provided by the desktop host.</summary>
public interface IAppUpdates
{
	AppUpdateStatus Status { get; }

	/// <summary>Raised on any thread when the check, download or staged release changes.</summary>
	event EventHandler? Changed;

	Task CheckAsync();

	/// <summary>Arranges for the downloaded update to be installed once NAT has quit, optionally starting it again.</summary>
	void InstallWhenQuit(bool restart);
}

public enum AppUpdatePhase { Unavailable, Idle, Checking, Current, Downloading, Ready, Failed }

public sealed record AppUpdateStatus(AppUpdatePhase Phase, string Message, string? ReadyVersion = null)
{
	public static AppUpdateStatus Unavailable { get; } = new(AppUpdatePhase.Unavailable, "To enable updates, launch an installed copy of NAT in normal desktop mode.");
	public bool CanAct => this.Phase is not (AppUpdatePhase.Unavailable or AppUpdatePhase.Checking or AppUpdatePhase.Downloading);
	public string ActionLabel => this.ReadyVersion is null ? "Check for updates" : $"Update to {this.ReadyVersion} and restart";
}
