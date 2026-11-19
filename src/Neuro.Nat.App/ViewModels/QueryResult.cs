using System.Text;

namespace Neuro.Nat.App.ViewModels;

public sealed class ScriptReply
{
	public ScriptReply(string markdown)
	{
		this.Markdown = markdown;
		string original = ScriptResultText.Unwrap(markdown) ?? markdown;
		ScriptValue root = new(original.AsMemory());
		this.Table = QueryResult.Read(original, root);
		if (this.Table is null && root.Items is not null)
			this.List = root;
	}

	public string Markdown { get; }
	internal QueryResult? Table { get; }
	internal ScriptValue? List { get; }

	internal bool HasExportableTable => this.Table is { } table ? table.Columns.Count > 0 : this.List is null && Export.MarkdownTables(this.Markdown).Count > 0;

	internal string ExportTables(bool csv)
	{
		if (this.Table is { } table)
			return csv ? table.Csv() : table.Tabs();
		return string.Join(Environment.NewLine, Export.MarkdownTables(this.Markdown).Select(t =>
			csv ? Export.Csv(t[0], t.Skip(1)) : Export.Tabs(t[0], t.Skip(1))));
	}
}

/// <summary>A tabular reply from the broker. Field order follows first appearance in the reply.</summary>
internal sealed class QueryResult
{
	private readonly List<IReadOnlyDictionary<string, ScriptValue>> rows = [];
	private readonly List<string> columns = [];
	private readonly HashSet<string> seen = new(StringComparer.Ordinal);
	public IReadOnlyList<IReadOnlyDictionary<string, ScriptValue>> Rows => this.rows;
	public IReadOnlyList<string> Columns => this.columns;
	public string Original { get; }

	private QueryResult(string original) => this.Original = original;

	public string[] PreviewColumns(int count)
	{
		// Keep projected columns in query order. For wide stored objects, show recognizable names first.
		if (this.columns.Count <= count || !this.seen.Contains("CollectionName"))
			return [.. this.columns.Take(count)];
		return [.. this.columns.OrderBy(name => name switch
		{
			"Name" or "UserName" or "FirstNames" or "LastNames" or "Label" => 0,
			"CollectionName" or "TypeName" or "ObjectId" => 2,
			_ => 1,
		}).Take(count)];
	}

	public IReadOnlyList<string?> Values(int index) => [.. this.columns.Select(name => this.rows[index].GetValueOrDefault(name)?.Text)];
	public string Csv() => Export.Csv(this.columns, this.rows.Select((_, i) => this.Values(i)));
	public string Tabs() => Export.Tabs(this.columns, this.rows.Select((_, i) => this.Values(i)));
	public string CopyRow(int index) => Export.CsvRow(this.Values(index));

	public static QueryResult? Read(string original, ScriptValue root)
	{
		QueryResult result = new(original);
		ReadOnlySpan<char> text = original.AsSpan().Trim();
		if (!text.IsEmpty && text[0] == '|')
			return result.ReadMatrix(text) ? result : null;
		if (root.Fields is { } single)
			result.AddRecord(single);
		else if (root.Items is { } items)
		{
			foreach (ScriptValue item in items)
			{
				if (item.Fields is not { } fields)
					return null;
				result.AddRecord(fields);
			}
		}
		else
			return null;
		return result;
	}

	private void AddRecord(IReadOnlyDictionary<string, ScriptValue> fields)
	{
		this.rows.Add(fields);
		foreach (string name in fields.Keys)
			if (this.seen.Add(name))
				this.columns.Add(name);
	}

	// Named ObjectMatrix replies consist solely of a header, a divider, and pipe-delimited rows.
	private bool ReadMatrix(ReadOnlySpan<char> text)
	{
		var lines = text.EnumerateLines();
		if (!lines.MoveNext() || MatrixCells(lines.Current) is not { Length: > 0 } header ||
			header.Any(string.IsNullOrWhiteSpace) || header.Distinct(StringComparer.Ordinal).Count() != header.Length || !lines.MoveNext())
			return false;
		string[] divider = lines.Current.Trim().ToString().Trim('|').Split('|');
		if (divider.Length != header.Length || !divider.All(d => d.Trim().Trim(':') is { Length: > 0 } marks && marks.All(c => c == '-')))
			return false;

		this.columns.AddRange(header);
		while (lines.MoveNext())
		{
			string[]? cells = MatrixCells(lines.Current);
			if (cells is null || cells.Length != header.Length)
				return false;
			this.rows.Add(header.Zip(cells).ToDictionary(pair => pair.First, pair => new ScriptValue(pair.Second.AsMemory()), StringComparer.Ordinal));
		}
		return true;
	}

	private static string[]? MatrixCells(ReadOnlySpan<char> line)
	{
		line = line.Trim();
		if (line.Length < 2 || line[0] != '|' || line[^1] != '|')
			return null;
		List<string> cells = [];
		StringBuilder cell = new();
		for (int i = 1; i < line.Length; i++)
		{
			char c = line[i];
			if (c == '|')
			{
				// The broker pads each value with one space. Further whitespace belongs to the value.
				int start = cell.Length > 0 && cell[0] == ' ' ? 1 : 0;
				int end = cell.Length;
				if (end > start && cell[end - 1] == ' ')
					end--;
				cells.Add(cell.ToString(start, end - start));
				cell.Clear();
			}
			else if (c == '\\' && i + 1 < line.Length - 1 && (char.IsPunctuation(line[i + 1]) || char.IsSymbol(line[i + 1])))
				cell.Append(line[++i]);
			else if (line[i..].StartsWith("&#124;"))
			{
				cell.Append('|');
				i += 5;
			}
			else if (line[i..].StartsWith("<br/>"))
			{
				cell.Append('\n');
				i += 4;
			}
			else if (c is '<' or '`' or '*' or '[')
				return null; // Rich Markdown cells retain the normal renderer, including links.
			else
				cell.Append(c);
		}
		return [.. cells];
	}

}
