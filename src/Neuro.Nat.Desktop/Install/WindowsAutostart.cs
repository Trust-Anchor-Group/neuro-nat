using System.Runtime.Versioning;
using Microsoft.Win32;
using Neuro.Nat.App.Watch;
using Velopack;
using Velopack.Locators;

namespace Neuro.Nat.Desktop.Install;

/// <summary>
/// "Start with Windows": a per-user Run entry that starts NAT in the tray (<c>--tray</c>) when
/// the user signs in. No admin rights needed. The installer turns it on; the tray menu toggles it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAutostart : IAutostart
{
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "NAT";

	/// <summary>The entry's name before the app was called NAT. Only <see cref="MoveOldEntry()"/> reads it.</summary>
	internal const string OldValueName = "Neuro.AA";

	/// <summary>
	/// At start of an installed copy: an entry left by Neuro.AA, as the app was called before, is
	/// replaced by NAT's own, pointing at this executable. Like the installer hooks, a copy that was
	/// not installed (a dev build) writes nothing. Returns true when the entry was moved.
	/// </summary>
	public static bool MoveOldEntry()
	{
		if (!VelopackLocator.IsCurrentSet || VelopackLocator.Current.CurrentlyInstalledVersion is null || Environment.ProcessPath is not string exe)
			return false;

		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
		return key is not null && MoveOldEntry(key, CommandFor(exe));
	}

	/// <summary>Replaces <see cref="OldValueName"/> with this app's entry in <paramref name="runKey"/>. Returns true when there was one.</summary>
	internal static bool MoveOldEntry(RegistryKey runKey, string command)
	{
		if (runKey.GetValue(OldValueName) is not string)
			return false;

		runKey.SetValue(ValueName, command, RegistryValueKind.String);
		runKey.DeleteValue(OldValueName, throwOnMissingValue: false);
		return true;
	}

	private static string CommandFor(string exe) => $"\"{exe}\" {Program.TrayArgument}";

	/// <summary>
	/// Installing turns "Start with Windows" on, updating keeps it pointing right, uninstalling
	/// removes it. The same goes for NAT's links (<see cref="WindowsLinkScheme"/>), so a click on
	/// one in another program opens NAT. One failing does not keep the other from being done.
	/// </summary>
	public static VelopackApp AddInstallerHooks(VelopackApp app)
		=> app
			.OnAfterInstallFastCallback(_ => Both(Enable, WindowsLinkScheme.Register))
			.OnAfterUpdateFastCallback(_ => Both(Refresh, WindowsLinkScheme.Register))
			.OnBeforeUninstallFastCallback(_ => Both(Disable, WindowsLinkScheme.Unregister));

	private static void Both(Action first, Action second)
	{
		try
		{
			first();
		}
		finally
		{
			second();
		}
	}

	public bool IsEnabled
	{
		get
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
			return key?.GetValue(ValueName) is string;
		}
	}

	public void SetEnabled(bool enabled)
	{
		if (enabled)
			Enable();
		else
			Disable();
	}

	/// <summary>Starts this executable in the tray when the user signs in.</summary>
	public static void Enable()
	{
		string exe = Environment.ProcessPath ?? throw new InvalidOperationException("The path of NAT is not known.");
		using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
		key.SetValue(ValueName, CommandFor(exe), RegistryValueKind.String);
	}

	public static void Disable()
	{
		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
		key?.DeleteValue(ValueName, throwOnMissingValue: false);
	}

	/// <summary>After an update: keep the entry (if any) pointing at this executable.</summary>
	public static void Refresh()
	{
		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
		if (key?.GetValue(ValueName) is string)
			Enable();
	}
}
