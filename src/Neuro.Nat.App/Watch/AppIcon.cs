using Avalonia.Controls;

namespace Neuro.Nat.App.Watch;

/// <summary>
/// The app's live icon, drawn by <see cref="IconArt"/>: the ring around a dot whose colour shows
/// the state of the Neurons. Used by the tray and by every window, which is what the taskbar
/// button shows. Each icon carries all the sizes Windows asks for, so 16 pixels gets the plain arcs.
/// </summary>
public static class AppIcon
{
	private static readonly Dictionary<(IconState, bool), WindowIcon> cache = [];

	/// <param name="state">What the dot says.</param>
	/// <param name="tray">The tray's icon, whose dot is larger at the small sizes.</param>
	public static WindowIcon Get(IconState state = IconState.Quiet, bool tray = false)
	{
		if (cache.TryGetValue((state, tray), out WindowIcon? icon))
			return icon;

		using MemoryStream ms = new(IconArt.Ico(IconArt.LiveSizes, IconArt.DotColor(state), tray));
		icon = new WindowIcon(ms);
		cache[(state, tray)] = icon;
		return icon;
	}
}
