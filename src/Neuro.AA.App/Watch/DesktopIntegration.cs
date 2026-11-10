namespace Neuro.AA.App.Watch;

/// <summary>Starting Neuro.AA when the user signs in. Provided by the desktop host.</summary>
public interface IAutostart
{
	bool IsEnabled { get; }

	void SetEnabled(bool enabled);
}

/// <summary>Updates of Neuro.AA itself, found and downloaded in the background. Provided by the desktop host.</summary>
public interface IAppUpdates
{
	/// <summary>The version that is downloaded and waits to be installed, if any.</summary>
	string? ReadyVersion { get; }

	/// <summary>Raised (on any thread) when an update has been downloaded.</summary>
	event EventHandler? Ready;

	/// <summary>Arranges for the downloaded update to be installed once Neuro.AA has quit, optionally starting it again.</summary>
	void InstallWhenQuit(bool restart);
}
