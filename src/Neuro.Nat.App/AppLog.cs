namespace Neuro.Nat.App;

/// <summary>
/// Minimal file log for startup and unhandled errors, so problems that happen before or
/// outside the UI can be diagnosed. The desktop host sets <see cref="FilePath"/>.
/// </summary>
public static class AppLog
{
	private static readonly object sync = new();

	/// <summary>Log file. Nothing is written while it is null.</summary>
	public static string? FilePath { get; set; }

	public static void Info(string message) => Write("INFO", message);

	public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : message + Environment.NewLine + ex);

	// What must never land in the log, even inside the text of an exception from a library:
	// message texts, passwords and tokens, sign-in material, and the full addresses of chat partners.
	private static readonly (System.Text.RegularExpressions.Regex Pattern, string With)[] scrubs =
	[
		// Message text in XMPP stanzas (<body>, Markdown and other content, subjects), and passwords in them.
		(new(@"<(body|subject|content|markdown|html|thread|password|token|secret)\b[^>]*>.*?</\1>", RegexOptions), "<$1>[removed]</$1>"),
		(new(@"<(body|subject|content|markdown|html)\b[^>]*/?>[^<]*", RegexOptions), "<$1>[removed]"),

		// Secrets in URLs: user:password@ and token-like query values.
		(new(@"(?<=://)[^/\s:@]+:[^/\s@]+@", RegexOptions), "[removed]@"),
		(new(@"(?<=[?&](?:token|access_token|key|apikey|api_key|password|pwd|secret|sig|signature|code)=)[^&\s""']+", RegexOptions), "[removed]"),

		// Secrets written as name and value: password=..., "Password":"...", Authorization: Bearer ...
		(new(@"(?<=\b(?:password|passwd|pwd|secret|token|apikey|api_key|hash)\s*[=:]\s*)(?!\[removed\])[^\s,;""'&]+", RegexOptions), "[removed]"),
		(new(@"(?<=""(?:Password|Secret|Token|Value|Hash|HashMethod)""\s*:\s*"")[^""]*", RegexOptions), "[removed]"),
		(new(@"(?<=\b(?:Bearer|Basic)\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions), "[removed]"),

		// A full address names the device someone writes from: keep only the bare address.
		(new(@"(\b[\w.+-]+@[\w.-]+)/[^\s""'<>,;)\]]+", RegexOptions), "$1/[removed]"),
	];

	private const System.Text.RegularExpressions.RegexOptions RegexOptions =
		System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant;

	/// <summary>Takes out of a log line what must never be written down. Every line passes through it.</summary>
	public static string Scrub(string message)
	{
		foreach ((System.Text.RegularExpressions.Regex pattern, string with) in scrubs)
			message = pattern.Replace(message, with);
		return message;
	}

	private static void Write(string level, string message)
	{
		string? path = FilePath;
		if (path is null)
			return;

		message = Scrub(message);

		try
		{
			lock (sync)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
			}
		}
		catch
		{
			// Logging must never take the app down.
		}
	}
}
