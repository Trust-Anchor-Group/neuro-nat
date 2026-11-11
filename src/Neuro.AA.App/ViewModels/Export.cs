using System.Text;

namespace Neuro.AA.App.ViewModels;

/// <summary>Turns rows into text for the clipboard and for files: tab-separated, CSV and Markdown tables.</summary>
public static class Export
{
	/// <summary>Tab-separated, which pastes into a spreadsheet as cells and into chat as readable lines.</summary>
	public static string Tabs(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows, bool withHeader = true)
	{
		StringBuilder sb = new();
		if (withHeader)
			sb.AppendLine(string.Join('\t', header));

		foreach (IReadOnlyList<string?> row in rows)
			sb.AppendLine(string.Join('\t', row.Select(c => OneLine(c))));

		return sb.ToString();
	}

	/// <summary>CSV as Excel reads it: comma, quotes doubled, a header row.</summary>
	public static string Csv(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
	{
		StringBuilder sb = new();
		sb.AppendLine(string.Join(',', header.Select(CsvCell)));
		foreach (IReadOnlyList<string?> row in rows)
			sb.AppendLine(string.Join(',', row.Select(CsvCell)));

		return sb.ToString();
	}

	public static string Markdown(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
	{
		StringBuilder sb = new();
		sb.Append("| ").Append(string.Join(" | ", header)).AppendLine(" |");
		sb.Append('|').Append(string.Concat(header.Select(_ => ":--|"))).AppendLine();
		foreach (IReadOnlyList<string?> row in rows)
			sb.Append("| ").Append(string.Join(" | ", row.Select(c => OneLine(c).Replace("|", "\\|", StringComparison.Ordinal)))).AppendLine(" |");

		return sb.ToString();
	}

	private static string CsvCell(string? value)
	{
		value ??= string.Empty;
		return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
	}

	public static string OneLine(string? value)
		=> (value ?? string.Empty).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

	/// <summary>Markdown text as a quote, for pasting into a reply.</summary>
	public static string Quote(string text)
		=> string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => "> " + l));

	/// <summary>
	/// Pulls the tables out of Markdown, so a console answer can be copied or saved as CSV.
	/// Returns one list of rows per table; the first row of each is its header.
	/// </summary>
	public static List<List<string[]>> MarkdownTables(string markdown)
	{
		List<List<string[]>> tables = [];
		List<string[]>? current = null;

		foreach (string raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
		{
			string line = raw.Trim();
			if (line.StartsWith('|') && line.Length > 1)
			{
				string[] cells = [.. SplitRow(line)];
				bool divider = cells.Length > 0 && cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '));
				if (divider)
					continue;

				current ??= [];
				current.Add(cells);
			}
			else if (current is not null)
			{
				tables.Add(current);
				current = null;
			}
		}

		if (current is not null)
			tables.Add(current);

		return tables;
	}

	private static IEnumerable<string> SplitRow(string line)
	{
		StringBuilder cell = new();
		string inner = line.Trim().Trim('|');
		for (int i = 0; i < inner.Length; i++)
		{
			char c = inner[i];
			if (c == '\\' && i + 1 < inner.Length && inner[i + 1] == '|')
			{
				cell.Append('|');
				i++;
			}
			else if (c == '|')
			{
				yield return Plain(cell.ToString());
				cell.Clear();
			}
			else
				cell.Append(c);
		}

		yield return Plain(cell.ToString());
	}

	/// <summary>Drops the Markdown marks a table cell usually carries: bold, code and line breaks.</summary>
	private static string Plain(string cell)
		=> cell.Trim().Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase).Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
			.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal).Trim();

	/// <summary>All tables of a Markdown text as one CSV, with an empty line between tables.</summary>
	public static string? MarkdownTablesAsCsv(string markdown)
	{
		List<List<string[]>> tables = MarkdownTables(markdown);
		if (tables.Count == 0)
			return null;

		return string.Join(Environment.NewLine, tables.Select(t => Csv(t[0], t.Skip(1))));
	}
}

/// <summary>Makes one-line XML readable: one element per line, indented.</summary>
public static class XmlTidy
{
	/// <summary>True when the text looks like XML worth tidying.</summary>
	public static bool LooksLikeXml(string text)
	{
		ReadOnlySpan<char> t = text.AsSpan().TrimStart();
		return t.Length > 2 && t[0] == '<' && text.AsSpan().TrimEnd()[^1] == '>';
	}

	/// <summary>
	/// The text indented as XML, or null when it is not XML. Only the tags are read, so text a
	/// parser would refuse (an XMPP stream header that is still open, a lone closing tag) is
	/// tidied too, and nothing but line breaks and spaces is ever added.
	/// </summary>
	public static string? Tidy(string text)
	{
		if (!LooksLikeXml(text))
			return null;

		string tidy = Scan(text.Trim());
		return tidy.Contains('\n') ? tidy : null;
	}

	/// <summary>Indents by tags alone.</summary>
	private static string Scan(string text)
	{
		StringBuilder sb = new();
		int depth = 0;
		int i = 0;
		bool lastWasText = false;

		void NewLine(int d)
		{
			if (sb.Length > 0)
				sb.Append('\n');
			sb.Append(' ', Math.Max(0, d) * 2);
		}

		while (i < text.Length)
		{
			if (text[i] == '<')
			{
				int end = FindTagEnd(text, i);
				if (end < 0)
				{
					sb.Append(text, i, text.Length - i);
					break;
				}

				string tag = text.Substring(i, end - i + 1);
				bool closing = tag.StartsWith("</", StringComparison.Ordinal);
				bool self = tag.EndsWith("/>", StringComparison.Ordinal) || tag.StartsWith("<?", StringComparison.Ordinal) || tag.StartsWith("<!", StringComparison.Ordinal);

				if (closing)
				{
					depth--;
					if (!lastWasText)
						NewLine(depth);
				}
				else
					NewLine(depth);

				sb.Append(tag);
				if (!closing && !self)
					depth++;

				lastWasText = false;
				i = end + 1;
			}
			else
			{
				int next = text.IndexOf('<', i);
				if (next < 0)
					next = text.Length;

				string content = text.Substring(i, next - i);
				if (!string.IsNullOrWhiteSpace(content))
				{
					sb.Append(content.Trim());
					lastWasText = true;
				}

				i = next;
			}
		}

		return sb.ToString();
	}

	private static int FindTagEnd(string text, int start)
	{
		char quote = '\0';
		for (int i = start + 1; i < text.Length; i++)
		{
			char c = text[i];
			if (quote != '\0')
			{
				if (c == quote)
					quote = '\0';
			}
			else if (c is '"' or '\'')
				quote = c;
			else if (c == '>')
				return i;
		}

		return -1;
	}
}
