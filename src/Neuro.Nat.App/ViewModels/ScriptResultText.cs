using System.Text;

namespace Neuro.Nat.App.ViewModels;

/// <summary>The literal text inside a broker chat-admin result.</summary>
internal static class ScriptResultText
{
	public static string? Unwrap(string markdown)
	{
		const string open = "<font class=\"result\">";
		const string close = "</font>";
		ReadOnlySpan<char> text = markdown.AsSpan().Trim();
		if (!text.StartsWith(open, StringComparison.Ordinal) || !text.EndsWith(close, StringComparison.Ordinal))
			return null;

		text = text[open.Length..^close.Length].Trim();
		if (text.Length >= 2 && text[0] == '`' && text[^1] == '`' && text.IndexOfAny('\n', '\r') < 0)
			return text[1..^1].ToString();

		string fenced = text.ToString().ReplaceLineEndings("\n");
		if (fenced.Length >= 8 && fenced.StartsWith("```\n", StringComparison.Ordinal) &&
			fenced.EndsWith("\n```", StringComparison.Ordinal) && !fenced.Contains("\n</font>\n", StringComparison.Ordinal))
			return fenced[4..^4];
		return null;
	}

	/// <summary>Indent single-line records, including Waher script's unquoted field names.
	/// Quoted field values retain their spelling.</summary>
	public static string ForDisplay(string literal)
	{
		ReadOnlySpan<char> text = literal.AsSpan().Trim();
		if (text.IsEmpty || text.IndexOfAny('\r', '\n') >= 0 ||
			(text[0] != '{' && !(text[0] == '[' && text[1..].TrimStart().StartsWith("{", StringComparison.Ordinal))))
			return literal;

		StringBuilder result = new();
		Stack<char> closing = new();
		char quote = '\0';
		bool escaped = false;
		bool indent = false;
		foreach (char c in text)
		{
			if (quote != '\0')
			{
				result.Append(c);
				if (escaped)
					escaped = false;
				else if (c == '\\')
					escaped = true;
				else if (c == quote)
					quote = '\0';
				continue;
			}

			bool endsGroup = c is '}' or ']' or ')';
			if (endsGroup && (!closing.TryPop(out char expected) || expected != c))
				return literal;
			if (indent && char.IsWhiteSpace(c))
				continue;
			if ((indent || endsGroup) && !(endsGroup && result[^1] is '{' or '[' or '('))
				result.Append('\n').Append(' ', 2 * Math.Min(closing.Count, 12));
			indent = false;
			result.Append(c);
			switch (c)
			{
				case '"':
				case '\'':
					quote = c;
					break;
				case '{':
				case '[':
				case '(':
					closing.Push(c switch { '{' => '}', '[' => ']', _ => ')' });
					indent = true;
					break;
				case ',':
					indent = true;
					break;
			}
		}
		return quote == '\0' && closing.Count == 0 ? result.ToString() : literal;
	}
}
