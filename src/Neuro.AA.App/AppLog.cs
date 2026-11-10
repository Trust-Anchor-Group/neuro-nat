namespace Neuro.AA.App;

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

	private static void Write(string level, string message)
	{
		string? path = FilePath;
		if (path is null)
			return;

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
