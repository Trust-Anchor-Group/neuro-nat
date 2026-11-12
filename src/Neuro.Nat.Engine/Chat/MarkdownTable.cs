using System.Globalization;
using System.Text.RegularExpressions;
using Neuro.Nat.Engine.Attention;

namespace Neuro.Nat.Engine.Chat;

/// <summary>
/// Reads tables out of chat admin answers: Markdown pipe tables, or tab-separated lines when the
/// Neuron answered without Markdown. Cells come back as plain text, with Markdown escapes removed.
/// </summary>
public static partial class MarkdownTable
{
	/// <summary>All rows, the header row first. Separator rows are skipped.</summary>
	public static IReadOnlyList<string[]> Rows(string text)
	{
		List<string[]> rows = [];
		foreach (string raw in text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string line = raw.Trim();
			if (line.Length == 0)
				continue;

			string[] cells = line.StartsWith('|')
				? [.. CellSplit().Split(TrimPipes(line)).Select(c => Unquote(NotificationClassifier.Plain(c).Trim('`', ' ')))]
				: [.. line.Split('\t').Select(c => Unquote(c.Trim().Trim('`')))];

			if (cells.All(c => Separator().IsMatch(c.Trim())))
				continue;

			rows.Add(cells);
		}

		return rows;
	}

	/// <summary>Data rows as dictionaries keyed by header, case-insensitively.</summary>
	public static IReadOnlyList<IReadOnlyDictionary<string, string>> Records(string text)
	{
		IReadOnlyList<string[]> rows = Rows(text);
		if (rows.Count < 2)
			return [];

		string[] header = rows[0];
		List<IReadOnlyDictionary<string, string>> records = [];
		foreach (string[] row in rows.Skip(1))
		{
			Dictionary<string, string> r = new(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < header.Length; i++)
			{
				if (header[i].Length > 0)
					r[header[i]] = i < row.Length ? row[i] : string.Empty;
			}

			records.Add(r);
		}

		return records;
	}

	/// <summary>
	/// A date as the Neuron's script engine writes it: <c>DateTimeUtc(2026,11,09,23,14,27)</c>,
	/// <c>DateTime(...)</c>, or an ordinary date string. Null for empty and N/A.
	/// </summary>
	public static DateTime? Date(string? s)
	{
		if (string.IsNullOrWhiteSpace(s) || s.Trim() == "N/A")
			return null;

		Match m = ScriptDate().Match(s);
		if (m.Success)
		{
			int[] p = [.. m.Groups["p"].Value.Split(',').Select(x => int.TryParse(x.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0)];
			if (p.Length >= 3)
			{
				try
				{
					DateTime d = new(p[0], p[1], p[2], p.Length > 3 ? p[3] : 0, p.Length > 4 ? p[4] : 0, p.Length > 5 ? p[5] : 0,
						m.Groups["utc"].Success ? DateTimeKind.Utc : DateTimeKind.Unspecified);
					return d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d;
				}
				catch (ArgumentOutOfRangeException)
				{
					return null;
				}
			}
		}

		if (DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime inv))
			return inv;
		if (DateTime.TryParse(s.Trim(), out DateTime local))
			return local;
		return null;
	}

	/// <summary>Script results show strings in quotes: <c>"Kim"</c>.</summary>
	private static string Unquote(string s)
		=> s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal) : s;

	private static string TrimPipes(string line)
	{
		if (line.StartsWith('|'))
			line = line[1..];
		if (line.EndsWith('|') && !line.EndsWith("\\|", StringComparison.Ordinal))
			line = line[..^1];
		return line;
	}

	[GeneratedRegex(@"(?<!\\)\|")]
	private static partial Regex CellSplit();

	[GeneratedRegex(@"^:?-{2,}:?$|^$")]
	private static partial Regex Separator();

	[GeneratedRegex(@"DateTime(?<utc>Utc)?\((?<p>[0-9,\s]+)\)")]
	private static partial Regex ScriptDate();
}
