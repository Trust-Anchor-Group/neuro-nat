namespace Neuro.AA.App.ViewModels;

/// <summary>One way to save something: the name shown in the file window, its extension, and the text.</summary>
public sealed record SaveFormat(string Name, string Extension, Func<string> Content);

/// <summary>
/// What every screen needs from the window it lives in: the clipboard, the file window for
/// Save, and the short note that confirms what just happened ("Copied 3 events"). The window
/// plugs these in when it opens. Tests plug in their own.
/// </summary>
public static class Ui
{
	/// <summary>Puts text on the clipboard.</summary>
	public static Func<string, Task>? SetClipboard { get; set; }

	/// <summary>Asks where to save, writes the chosen format, and returns the file's name, or null when cancelled.</summary>
	public static Func<string, IReadOnlyList<SaveFormat>, Task<string?>>? SaveFile { get; set; }

	/// <summary>Opens something in a window of its own. Given the view model; the window knows which view fits.</summary>
	public static Action<object>? OpenWindow { get; set; }

	/// <summary>Opens a screen in a window of its own. Given what to show, as <see cref="MainWindowViewModel.Address"/> writes it.</summary>
	public static Action<string>? NewWindow { get; set; }

	/// <summary>Opens the Wall view: the Neurons and their latest problems, large, for a big screen.</summary>
	public static Action? OpenWall { get; set; }

	/// <summary>Remembers how wide the panes were dragged.</summary>
	public static ILayoutStore? Layout { get; set; }

	/// <summary>Raised with a short note for the person. May come from any thread.</summary>
	public static event Action<string>? Said;

	/// <summary>Shows a short note at the bottom of the window for a couple of seconds.</summary>
	public static void Say(string note)
	{
		if (!string.IsNullOrWhiteSpace(note))
			Said?.Invoke(note);
	}

	/// <summary>Raised with a note and the way to take back what it says. May come from any thread.</summary>
	public static event Action<string, Action>? Offered;

	/// <summary>
	/// Says what just happened and offers to take it back: the note shows with an Undo button
	/// for a few seconds. For things done with one click that are easy to do by mistake.
	/// </summary>
	public static void Say(string note, Action undo)
	{
		if (string.IsNullOrWhiteSpace(note))
			return;

		Said?.Invoke(note);
		Offered?.Invoke(note, undo);
	}

	/// <summary>Copies text and confirms it. <paramref name="what"/> finishes the sentence "Copied …".</summary>
	public static async Task Copy(string? text, string what)
	{
		if (string.IsNullOrEmpty(text))
		{
			Say("Nothing to copy");
			return;
		}

		if (SetClipboard is not Func<string, Task> set)
			return;

		await set(text);
		Say("Copied " + what);
	}

	/// <summary>Asks for a file and saves one of the formats to it.</summary>
	public static async Task Save(string fileName, IReadOnlyList<SaveFormat> formats)
	{
		if (SaveFile is not Func<string, IReadOnlyList<SaveFormat>, Task<string?>> save || formats.Count == 0)
			return;

		string? saved = await save(fileName, formats);
		if (saved is not null)
			Say("Saved " + saved);
	}

	/// <summary>"1 event" or "3 events".</summary>
	public static string Count(int n, string one, string? many = null)
		=> n == 1 ? "1 " + one : $"{n:N0} {many ?? one + "s"}";

	/// <summary>A file name that is safe on every system, with today's date and the time in it.</summary>
	public static string FileName(string what, DateTime? when = null)
	{
		char[] bad = Path.GetInvalidFileNameChars();
		string clean = new([.. what.Select(c => Array.IndexOf(bad, c) >= 0 || c is ' ' or '/' or '\\' or ':' ? '-' : c)]);
		while (clean.Contains("--", StringComparison.Ordinal))
			clean = clean.Replace("--", "-", StringComparison.Ordinal);

		return clean.Trim('-') + " " + (when ?? DateTime.Now).ToString("yyyy-MM-dd HHmm");
	}
}
