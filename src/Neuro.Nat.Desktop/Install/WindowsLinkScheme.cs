using System.Runtime.Versioning;
using Microsoft.Win32;
using Neuro.Nat.Engine.Links;

namespace Neuro.Nat.Desktop.Install;

/// <summary>
/// Links to things in NAT (<c>neuro-nat://…</c>) open NAT when clicked in another program: the
/// scheme is registered for the current user when NAT is installed, kept pointing at NAT when it
/// updates, and removed when it is uninstalled. No admin rights needed. The link is handed to
/// NAT as its one argument; NAT checks it before it looks at anything in it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsLinkScheme
{
	/// <summary>Where a user's own schemes live, under HKEY_CURRENT_USER.</summary>
	internal const string ClassesKey = @"Software\Classes";

	/// <summary>Registers the scheme for this executable, for the current user.</summary>
	public static void Register()
	{
		string exe = Environment.ProcessPath ?? throw new InvalidOperationException("The path of NAT is not known.");
		using RegistryKey classes = Registry.CurrentUser.CreateSubKey(ClassesKey, writable: true);
		Register(classes, exe);
	}

	/// <summary>Removes the scheme, for the current user.</summary>
	public static void Unregister()
	{
		using RegistryKey? classes = Registry.CurrentUser.OpenSubKey(ClassesKey, writable: true);
		if (classes is not null)
			Unregister(classes);
	}

	/// <summary>
	/// Writes the scheme under <paramref name="classes"/>: the class's name, "URL Protocol", the
	/// icon, and the command that starts <paramref name="exe"/> with the link as its argument.
	/// </summary>
	internal static void Register(RegistryKey classes, string exe)
	{
		using RegistryKey scheme = classes.CreateSubKey(NatLink.Scheme, writable: true);
		scheme.SetValue(string.Empty, "URL:NAT link", RegistryValueKind.String);
		scheme.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);

		using (RegistryKey icon = scheme.CreateSubKey("DefaultIcon", writable: true))
			icon.SetValue(string.Empty, $"\"{exe}\",0", RegistryValueKind.String);

		using RegistryKey command = scheme.CreateSubKey(@"shell\open\command", writable: true);
		command.SetValue(string.Empty, CommandFor(exe), RegistryValueKind.String);
	}

	/// <summary>Removes the scheme from under <paramref name="classes"/>. Nothing else there is touched.</summary>
	internal static void Unregister(RegistryKey classes)
		=> classes.DeleteSubKeyTree(NatLink.Scheme, throwOnMissingSubKey: false);

	/// <summary>True when the scheme under <paramref name="classes"/> starts <paramref name="exe"/>.</summary>
	internal static bool IsRegistered(RegistryKey classes, string exe)
	{
		using RegistryKey? command = classes.OpenSubKey(NatLink.Scheme + @"\shell\open\command");
		return command?.GetValue(string.Empty) is string c && string.Equals(c, CommandFor(exe), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>The command a click runs: NAT, with the link as its one argument.</summary>
	internal static string CommandFor(string exe) => $"\"{exe}\" \"%1\"";
}
