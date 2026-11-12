using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Neuro.Nat.App.Watch;

/// <summary>
/// Makes the window's taskbar button flash until the window comes to the front: the way Windows
/// says "something here wants you" without taking the keyboard away. Does nothing elsewhere.
/// </summary>
public static class TaskbarFlash
{
	public static void Start(Window window)
	{
		if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not IntPtr handle || handle == IntPtr.Zero)
			return;

		try
		{
			FlashInfo info = new()
			{
				Size = (uint)Marshal.SizeOf<FlashInfo>(),
				Window = handle,
				Flags = FlashTray | FlashTimerNoForeground,
				Count = 3,
			};
			_ = FlashWindowEx(ref info);
		}
		catch (Exception)
		{
			// Flashing is a nicety.
		}
	}

	private const uint FlashTray = 0x00000002;
	private const uint FlashTimerNoForeground = 0x0000000C;

	[StructLayout(LayoutKind.Sequential)]
	private struct FlashInfo
	{
		public uint Size;
		public IntPtr Window;
		public uint Flags;
		public uint Count;
		public uint Timeout;
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool FlashWindowEx(ref FlashInfo info);
}
