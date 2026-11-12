namespace Neuro.Nat.App;

/// <summary>
/// How a kiosk runs: only the Big screen, full screen, and it cannot be left from the keyboard
/// (Alt+F4 still quits, for service). No main window, no tray, no pop-ups, no sounds.
/// </summary>
/// <param name="Neurons">The domains of the Neurons the kiosk shows, from its config file.</param>
public sealed record KioskPolicy(IReadOnlyList<string> Neurons);
