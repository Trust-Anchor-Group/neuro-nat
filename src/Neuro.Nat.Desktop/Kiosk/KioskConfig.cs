using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Neuro.Nat.Desktop.Kiosk;

/// <summary>An account a kiosk signs in with, from its config file.</summary>
internal sealed record KioskAccount(string Host, int Port, string UserName, string Password, string? Domain, string? WebAddress, bool TrustServerCertificate = false);

/// <summary>Something wrong with a kiosk config file, in words the person setting it up can act on.</summary>
internal sealed class KioskConfigException(string message) : Exception(message);

/// <summary>
/// A kiosk's config file (nat-kiosk.json, given with --config): the accounts, which Big screen
/// pages and for how long, and the look. It is read at every start and never written; nothing
/// the kiosk learns is kept. The file holds passwords, so it must be readable by its owner only.
/// </summary>
/// <example>
/// {
///   "accounts": [ { "host": "neuron.example.com", "userName": "kiosk", "password": "..." } ],
///   "neurons": [ "neuron.example.com" ],
///   "pages": [ "neurons", "problems" ],
///   "seconds": 20,
///   "look": "dark"
/// }
/// </example>
internal sealed record KioskConfig(
	IReadOnlyList<KioskAccount> Accounts,
	IReadOnlyList<string> Neurons,
	IReadOnlyList<string>? Pages,
	int Seconds,
	App.ViewModels.LookChoice Look)
{
	/// <summary>How long a Big screen page may show, in seconds: the same choices as Settings.</summary>
	public static readonly int[] SecondsAllowed = [10, 20, 30, 60];

	/// <summary>Reads and checks a config file. Throws <see cref="KioskConfigException"/> saying what is wrong and where.</summary>
	public static KioskConfig Load(string path)
	{
		string name = Path.GetFileName(path);
		if (!File.Exists(path))
			throw new KioskConfigException($"There is no {name} at {Path.GetDirectoryName(Path.GetFullPath(path))}.");

		if (WhoElseCanRead(path) is string who)
			throw new KioskConfigException($"{name} holds passwords, but {who} can read it. Let only your own user read it, then start NAT again.");

		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
		}
		catch (JsonException ex)
		{
			string at = ex.LineNumber is long line ? $", line {line + 1}" : string.Empty;
			throw new KioskConfigException($"{name}{at} is not valid JSON.");
		}

		using (doc)
			return Read(doc.RootElement, name);
	}

	private static KioskConfig Read(JsonElement root, string name)
	{
		if (root.ValueKind != JsonValueKind.Object)
			throw new KioskConfigException($"{name} must be one JSON object.");

		if (!root.TryGetProperty("accounts", out JsonElement accounts) || accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() == 0)
			throw new KioskConfigException($"{name} needs \"accounts\": a list with at least one account.");

		List<KioskAccount> list = [];
		int i = 0;
		foreach (JsonElement a in accounts.EnumerateArray())
		{
			i++;
			string where = $"{name}, account {i}";
			if (a.ValueKind != JsonValueKind.Object)
				throw new KioskConfigException($"{where} must be an object.");

			string host = Text(a, "host", where, required: true)!;
			string user = Text(a, "userName", where, required: true)!;
			string password = Text(a, "password", where, required: true)!;
			string? domain = Text(a, "domain", where, required: false);
			string? web = Text(a, "webAddress", where, required: false);
			int port = 5222;
			if (a.TryGetProperty("port", out JsonElement p))
			{
				if (!p.TryGetInt32(out port) || port is < 1 or > 65535)
					throw new KioskConfigException($"{where}: \"port\" must be a number from 1 to 65535.");
			}

			if (web is not null && Engine.Accounts.AccountWeb.Check(web) is null)
				throw new KioskConfigException($"{where}: \"webAddress\" must be an https address, or http on this computer.");

			bool trust = false;
			if (a.TryGetProperty("trustServerCertificate", out JsonElement tc))
			{
				if (tc.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
					throw new KioskConfigException($"{where}: \"trustServerCertificate\" must be true or false.");
				trust = tc.GetBoolean();
			}

			list.Add(new KioskAccount(host, port, user, password, domain, web, trust));
		}

		List<string> neurons = [];
		if (root.TryGetProperty("neurons", out JsonElement ns))
		{
			if (ns.ValueKind != JsonValueKind.Array || ns.EnumerateArray().Any(n => n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString())))
				throw new KioskConfigException($"{name}: \"neurons\" must be a list of Neuron domains.");
			neurons = [.. ns.EnumerateArray().Select(n => n.GetString()!.Trim().ToLowerInvariant())];
		}

		List<string>? pages = null;
		if (root.TryGetProperty("pages", out JsonElement ps))
		{
			if (ps.ValueKind != JsonValueKind.Array || ps.EnumerateArray().Any(n => n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString())))
				throw new KioskConfigException($"{name}: \"pages\" must be a list of page names, such as \"neurons\" and \"problems\".");
			pages = [.. ps.EnumerateArray().Select(n => n.GetString()!.Trim())];
		}

		int seconds = 20;
		if (root.TryGetProperty("seconds", out JsonElement s) && (!s.TryGetInt32(out seconds) || !SecondsAllowed.Contains(seconds)))
			throw new KioskConfigException($"{name}: \"seconds\" must be 10, 20, 30 or 60.");

		App.ViewModels.LookChoice look = App.ViewModels.LookChoice.System;
		if (root.TryGetProperty("look", out JsonElement l))
		{
			look = l.ValueKind == JsonValueKind.String ? l.GetString()?.Trim().ToLowerInvariant() switch
			{
				"light" => App.ViewModels.LookChoice.Light,
				"dark" => App.ViewModels.LookChoice.Dark,
				"system" => App.ViewModels.LookChoice.System,
				_ => throw new KioskConfigException($"{name}: \"look\" must be \"light\", \"dark\" or \"system\"."),
			} : throw new KioskConfigException($"{name}: \"look\" must be \"light\", \"dark\" or \"system\".");
		}

		return new KioskConfig(list, neurons, pages, seconds, look);
	}

	private static string? Text(JsonElement o, string property, string where, bool required)
	{
		if (!o.TryGetProperty(property, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
		{
			if (required)
				throw new KioskConfigException($"{where} needs \"{property}\".");
			return null;
		}

		if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
			throw new KioskConfigException($"{where}: \"{property}\" must be text.");

		return property == "password" ? v.GetString()! : v.GetString()!.Trim();
	}

	/// <summary>Who else than the file's owner can read it, in words, or null when nobody can.</summary>
	internal static string? WhoElseCanRead(string path)
	{
		if (OperatingSystem.IsWindows())
			return WindowsReaders(path);

		UnixFileMode mode = File.GetUnixFileMode(path);
		if ((mode & UnixFileMode.OtherRead) != 0)
			return "every user on this computer";
		if ((mode & UnixFileMode.GroupRead) != 0)
			return "the file's group";
		return null;
	}

	[SupportedOSPlatform("windows")]
	private static string? WindowsReaders(string path)
	{
		using WindowsIdentity me = WindowsIdentity.GetCurrent();
		SecurityIdentifier self = me.User!;
		SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
		SecurityIdentifier admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

		FileSecurity security = new FileInfo(path).GetAccessControl();
		foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
		{
			if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & FileSystemRights.ReadData) == 0)
				continue;

			SecurityIdentifier who = (SecurityIdentifier)rule.IdentityReference;
			if (who == self || who == system || who == admins)
				continue;

			try
			{
				return who.Translate(typeof(NTAccount)).Value;
			}
			catch (IdentityNotMappedException)
			{
				return who.Value;
			}
		}

		return null;
	}
}
