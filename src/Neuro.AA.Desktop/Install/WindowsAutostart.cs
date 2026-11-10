using System.Runtime.Versioning;
using Microsoft.Win32;
using Neuro.AA.App.Watch;
using Velopack;

namespace Neuro.AA.Desktop.Install;

/// <summary>
/// "Start with Windows": a per-user Run entry that starts Neuro.AA in the tray (<c>--tray</c>) when
/// the user signs in. No admin rights needed. The installer turns it on; the tray menu toggles it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAutostart : IAutostart
{
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "Neuro.AA";

	/// <summary>Installing turns "Start with Windows" on, updating keeps it pointing right, uninstalling removes it.</summary>
	public static VelopackApp AddInstallerHooks(VelopackApp app)
		=> app
			.OnAfterInstallFastCallback(_ => Enable())
			.OnAfterUpdateFastCallback(_ => Refresh())
			.OnBeforeUninstallFastCallback(_ => Disable());

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
		string exe = Environment.ProcessPath ?? throw new InvalidOperationException("The path of Neuro.AA is not known.");
		using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
		key.SetValue(ValueName, $"\"{exe}\" {Program.TrayArgument}", RegistryValueKind.String);
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
