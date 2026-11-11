using System.Diagnostics;
using System.Runtime.InteropServices;
using Neuro.AA.Engine.Alerts;

namespace Neuro.AA.App.Watch;

/// <summary>
/// Plays the operating system's own alert sounds, so they follow the user's sound scheme and
/// volume: a harder one for errors and Neurons going down, a softer one for the rest.
/// </summary>
public static class AlertSound
{
	private static DateTime last = DateTime.MinValue;

	/// <summary>Plays the sound for an alert. Several alerts at once make one sound.</summary>
	public static void Play(Alert a)
	{
		DateTime now = DateTime.UtcNow;
		if (now - last < TimeSpan.FromSeconds(2))
			return;
		last = now;

		bool hard = a.Kind is AlertKind.Error or AlertKind.NeuronDown;
		try
		{
			if (OperatingSystem.IsWindows())
				_ = PlaySound(hard ? "SystemHand" : a.Kind == AlertKind.ContactRequest ? "SystemAsterisk" : "SystemNotification", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
			else if (OperatingSystem.IsMacOS())
				Run("afplay", hard ? "/System/Library/Sounds/Basso.aiff" : "/System/Library/Sounds/Glass.aiff");
			else if (OperatingSystem.IsLinux())
				Run("canberra-gtk-play", "-i " + (hard ? "dialog-error" : "message-new-instant"));
		}
		catch (Exception)
		{
			// No sound is no reason to fail an alert.
		}
	}

	private static void Run(string file, string args)
		=> Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();

	private const uint SndAsync = 0x0001;
	private const uint SndNoDefault = 0x0002;
	private const uint SndAlias = 0x00010000;

	[DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
