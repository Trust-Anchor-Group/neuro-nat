using System.Text;

namespace Neuro.Nat.App.ViewModels;

/// <summary>A script value retains its text for copying. Only inspect the next level when it is needed.</summary>
internal sealed class ScriptValue(ReadOnlyMemory<char> source, bool quoted = false)
{
	private bool inspected;
	private Dictionary<string, ScriptValue>? fields;
	private List<ScriptValue>? items;

	public string Text => source.ToString();
	public IReadOnlyDictionary<string, ScriptValue>? Fields
	{
		get
		{
			this.Inspect();
			return this.fields;
		}
	}
	public IReadOnlyList<ScriptValue>? Items
	{
		get
		{
			this.Inspect();
			return this.items;
		}
	}
	public string? Summary => this.Fields is { } map ? Ui.Count(map.Count, "field") :
		this.Items is { } list ? Ui.Count(list.Count, "item") : null;

	private void Inspect()
	{
		if (this.inspected)
			return;
		this.inspected = true;
		var text = source.Trim();
		if (quoted || text.IsEmpty)
			return;

		LiteralReader reader = new(text);
		if (text.Span[0] == '{' && reader.ReadRecord(out var map) && reader.End)
			this.fields = map;
		else if (text.Span[0] == '[' && reader.ReadList(out var list) && reader.End)
			this.items = list;
	}

	// Split only at the current level. Parentheses, escapes and quoted delimiters belong to the value.
	private ref struct LiteralReader(ReadOnlyMemory<char> text)
	{
		private ReadOnlyMemory<char> rest = text;
		private ReadOnlySpan<char> Span => this.rest.Span;
		public bool End => this.rest.TrimStart().IsEmpty;

		public bool Take(char c)
		{
			this.rest = this.rest.TrimStart();
			if (this.rest.IsEmpty || this.Span[0] != c)
				return false;
			this.rest = this.rest[1..];
			return true;
		}

		public bool ReadRecord(out Dictionary<string, ScriptValue> fields)
		{
			fields = new(StringComparer.Ordinal);
			if (!this.Take('{'))
				return false;
			if (this.Take('}'))
				return true;
			do
			{
				this.rest = this.rest.TrimStart();
				string? key;
				if (!this.rest.IsEmpty && this.Span[0] is '"' or '\'')
					key = this.ReadString();
				else
				{
					int i = 0;
					while (i < this.rest.Length && (char.IsLetterOrDigit(this.Span[i]) || this.Span[i] == '_'))
						i++;
					if (i == 0)
						return false;
					key = this.rest[..i].ToString();
					this.rest = this.rest[i..];
				}
				if (key is null || !this.Take(':') || this.ReadValue() is not ScriptValue value || !fields.TryAdd(key, value))
					return false;
			}
			while (this.Take(','));
			return this.Take('}');
		}

		public bool ReadList(out List<ScriptValue> items)
		{
			items = [];
			if (!this.Take('['))
				return false;
			if (this.Take(']'))
				return true;
			do
			{
				if (this.ReadValue() is not { } value)
					return false;
				items.Add(value);
			}
			while (this.Take(','));
			return this.Take(']');
		}

		private string? ReadString()
		{
			char quote = this.Span[0];
			StringBuilder value = new();
			for (int i = 1; i < this.rest.Length; i++)
			{
				char c = this.Span[i];
				if (c == quote)
				{
					this.rest = this.rest[(i + 1)..];
					return value.ToString();
				}
				if (c != '\\')
					value.Append(c);
				else if (++i >= this.rest.Length)
					return null;
				else if (this.Span[i] == 'u' && i + 4 < this.rest.Length && ushort.TryParse(this.Span.Slice(i + 1, 4),
					System.Globalization.NumberStyles.AllowHexSpecifier, null, out ushort code))
				{
					value.Append((char)code);
					i += 4;
				}
				else
				{
					char escaped = this.Span[i] switch
					{
						'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'a' => '\a', 'v' => '\v',
						'\\' or '"' or '\'' or '/' => this.Span[i], _ => '\0',
					};
					if (escaped == '\0')
						return null;
					value.Append(escaped);
				}
			}
			return null;
		}

		private ScriptValue? ReadValue()
		{
			this.rest = this.rest.TrimStart();
			if (!this.rest.IsEmpty && this.Span[0] is '"' or '\'')
				return this.ReadString() is string decoded ? new ScriptValue(decoded.AsMemory(), quoted: true) : null;
			Stack<char> closing = new();
			char quote = '\0';
			int i = 0;
			for (; i < this.rest.Length; i++)
			{
				char c = this.Span[i];
				if (quote != '\0')
				{
					if (c == '\\') i++;
					else if (c == quote) quote = '\0';
				}
				else if (closing.Count == 0 && c is ',' or '}' or ']')
					break;
				else if (c is '"' or '\'')
					quote = c;
				else if (c is '{' or '[' or '(')
					closing.Push(c switch { '{' => '}', '[' => ']', _ => ')' });
				else if (c is '}' or ']' or ')' && (!closing.TryPop(out char expected) || c != expected))
					return null;
			}
			if (quote != '\0' || closing.Count != 0 || i == 0)
				return null;
			ScriptValue value = new(this.rest[..i].TrimEnd());
			this.rest = this.rest[i..];
			return value;
		}
	}
}
