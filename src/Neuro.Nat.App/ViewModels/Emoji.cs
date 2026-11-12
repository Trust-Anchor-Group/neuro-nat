using System.Text;
using System.Text.RegularExpressions;

namespace Neuro.Nat.App.ViewModels;

/// <summary>An emoji and the name it is typed by, as in ":rocket:".</summary>
public sealed record EmojiChoice(string Name, string Symbol)
{
	public string Hint => ":" + this.Name + ":";
}

/// <summary>A row of the emoji picker.</summary>
public sealed record EmojiGroup(string Title, IReadOnlyList<EmojiChoice> Items);

/// <summary>
/// The emoji the composer offers: a small set that suits talk between operators, picked from
/// the button beside the box or typed as ":name". Any other emoji still works when pasted or
/// typed with the system's own emoji keyboard.
/// </summary>
public static partial class Emoji
{
	public static IReadOnlyList<EmojiGroup> Groups { get; } =
	[
		new("Reactions", Make(
			"thumbsup 👍", "thumbsdown 👎", "ok_hand 👌", "clap 👏", "pray 🙏", "wave 👋", "raised_hands 🙌", "muscle 💪",
			"eyes 👀", "point_up ☝️", "handshake 🤝", "shrug 🤷", "facepalm 🤦", "heart ❤️", "tada 🎉", "100 💯")),
		new("Faces", Make(
			"smile 😄", "grin 😁", "joy 😂", "slight_smile 🙂", "wink 😉", "sunglasses 😎", "thinking 🤔", "neutral 😐",
			"confused 😕", "worried 😟", "sweat 😅", "cry 😢", "angry 😠", "scream 😱", "sleeping 😴", "party 🥳")),
		new("Status", Make(
			"check ✅", "x ❌", "warning ⚠️", "question ❓", "exclamation ❗", "rotating_light 🚨", "stop 🛑", "construction 🚧",
			"green_circle 🟢", "yellow_circle 🟡", "red_circle 🔴", "blue_circle 🔵", "white_circle ⚪", "hourglass ⏳", "clock ⏰", "zzz 💤")),
		new("Work", Make(
			"rocket 🚀", "fire 🔥", "bug 🐛", "wrench 🔧", "hammer 🔨", "gear ⚙️", "bulb 💡", "zap ⚡",
			"boom 💥", "sparkles ✨", "star ⭐", "lock 🔒", "unlock 🔓", "key 🔑", "bell 🔔", "mag 🔍",
			"link 🔗", "pushpin 📌", "memo 📝", "calendar 📅", "chart_up 📈", "chart_down 📉", "package 📦", "computer 💻",
			"phone 📱", "satellite 📡", "battery 🔋", "plug 🔌", "thermometer 🌡️", "cloud ☁️", "recycle ♻️", "coffee ☕")),
	];

	/// <summary>Other names people type for the same emoji.</summary>
	private static readonly Dictionary<string, string> alias = new(StringComparer.OrdinalIgnoreCase)
	{
		["+1"] = "thumbsup",
		["-1"] = "thumbsdown",
		["ok"] = "ok_hand",
		["white_check_mark"] = "check",
		["done"] = "check",
		["no"] = "x",
		["warn"] = "warning",
		["laugh"] = "joy",
		["sad"] = "cry",
		["think"] = "thinking",
		["party_popper"] = "tada",
		["idea"] = "bulb",
		["search"] = "mag",
		["note"] = "memo",
		["alarm"] = "rotating_light",
		["tools"] = "wrench",
	};

	public static IReadOnlyList<EmojiChoice> All { get; } = [.. Groups.SelectMany(g => g.Items)];

	private static readonly Dictionary<string, EmojiChoice> byName = All.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

	private static EmojiChoice[] Make(params string[] pairs)
		=> [.. pairs.Select(p => p.Split(' ', 2)).Select(p => new EmojiChoice(p[0], p[1]))];

	/// <summary>The emoji with this name, or one of its other names.</summary>
	public static EmojiChoice? Find(string name)
		=> byName.TryGetValue(name, out EmojiChoice? e) ? e
			: alias.TryGetValue(name, out string? real) ? byName[real]
			: null;

	/// <summary>Emoji whose names start with what was typed, then those that contain it.</summary>
	public static IReadOnlyList<EmojiChoice> Suggest(string typed, int max = 6)
	{
		if (typed.Length == 0)
			return [];

		List<EmojiChoice> found = [.. All.Where(e => e.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))];
		foreach (KeyValuePair<string, string> a in alias.Where(a => a.Key.StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
		{
			if (!found.Contains(byName[a.Value]))
				found.Add(byName[a.Value]);
		}

		foreach (EmojiChoice e in All.Where(e => e.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)))
		{
			if (!found.Contains(e))
				found.Add(e);
		}

		return found.Count > max ? found[..max] : found;
	}

	/// <summary>
	/// The ":name" being typed just before the caret, when there is one: a colon at the start of
	/// a word and at least two letters after it. Null otherwise, so "10:30" suggests nothing.
	/// </summary>
	public static (int Start, string Typed)? WordAt(string text, int caret)
	{
		caret = Math.Clamp(caret, 0, text.Length);
		int i = caret;
		while (i > 0 && IsNameChar(text[i - 1]))
			i--;

		if (i == 0 || text[i - 1] != ':' || caret - i < 2)
			return null;

		int colon = i - 1;
		if (colon > 0 && !char.IsWhiteSpace(text[colon - 1]))
			return null;

		return (colon, text[i..caret]);
	}

	private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '-';

	[GeneratedRegex(@"(?<![\w:]):([A-Za-z0-9_+\-]{1,24}):(?![\w:])")]
	private static partial Regex Named();

	/// <summary>
	/// Turns every ":name:" that names a known emoji into the emoji. Text between backticks is
	/// left alone, so code and addresses keep their colons.
	/// </summary>
	public static string Replace(string text)
	{
		if (!text.Contains(':', StringComparison.Ordinal))
			return text;

		StringBuilder sb = new();
		bool code = false;
		foreach (string part in text.Split('`'))
		{
			if (sb.Length > 0 || code)
				sb.Append('`');

			sb.Append(code ? part : Named().Replace(part, m => Find(m.Groups[1].Value)?.Symbol ?? m.Value));
			code = !code;
		}

		return sb.ToString();
	}
}
