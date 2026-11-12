using System.Diagnostics;
using System.Runtime.InteropServices;
using Neuro.Nat.Engine.Alerts;

namespace Neuro.Nat.App.Watch;

/// <summary>
/// Plays the operating system's own sounds, so they follow the user's sound scheme and volume.
/// There are three, and you can tell them apart without looking: a hard one for errors and
/// Neurons going down, a softer one for what a Neuron or a person asks of you, and the
/// operating system's message sound for chat messages.
/// </summary>
public static class AlertSound
{
	private static DateTime lastAlert = DateTime.MinValue;
	private static DateTime lastMessage = DateTime.MinValue;

	/// <summary>Plays the sound for an alert. Several at once make one sound; a message and an alert each keep their own.</summary>
	public static void Play(Alert a)
	{
		DateTime now = DateTime.UtcNow;
		bool message = a.Kind == AlertKind.Message;
		if (now - (message ? lastMessage : lastAlert) < TimeSpan.FromSeconds(2))
			return;

		if (message)
			lastMessage = now;
		else
			lastAlert = now;

		bool hard = a.Kind is AlertKind.Error or AlertKind.NeuronDown;
		try
		{
			if (OperatingSystem.IsWindows())
			{
				if (message)
				{
					// The instant-message sound; older sound schemes only have the mail one.
					if (!PlaySound("Notification.IM", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault) &&
						!PlaySound("MailBeep", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault))
					{
						_ = PlaySound("SystemAsterisk", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
					}
				}
				else
					_ = PlaySound(hard ? "SystemHand" : a.IsRequest ? "SystemAsterisk" : "SystemNotification", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
			}
			else if (OperatingSystem.IsMacOS())
				Run("afplay", hard ? "/System/Library/Sounds/Basso.aiff" : message ? "/System/Library/Sounds/Tink.aiff" : "/System/Library/Sounds/Glass.aiff");
			else if (OperatingSystem.IsLinux())
				Run("canberra-gtk-play", "-i " + (hard ? "dialog-error" : message ? "message-new-instant" : "dialog-information"));
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
