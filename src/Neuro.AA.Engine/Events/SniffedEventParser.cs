using System.Text;
using System.Text.RegularExpressions;
using Neuro.AA.Engine.Sniffing;

namespace Neuro.AA.Engine.Events;

/// <summary>
/// Parses the text the Neuron's <c>EventSinkToSniffer</c> writes for each event:
/// the message, then a blank line, then one "Key: Value" field per line (Object, Actor,
/// EventId, Facility, Module, StackTrace and tags). StackTrace values span several lines.
/// </summary>
public static partial class SniffedEventParser
{
	private static readonly HashSet<string> knownFields = new(StringComparer.OrdinalIgnoreCase)
	{
		"Object", "Actor", "EventId", "Facility", "Module", "StackTrace",
	};

	[GeneratedRegex(@"^(?<key>[A-Za-z][A-Za-z0-9_.\-]{0,63}): ?(?<value>.*)$")]
	private static partial Regex FieldLine();

	/// <summary>Converts a sniffed line to an event, or returns null for lines that are not events.</summary>
	public static NeuronEvent? Parse(SnifferLine line, EventSource source)
	{
		EventSeverity severity;
		switch (line.Direction)
		{
			case SnifferDirection.Information: severity = EventSeverity.Info; break;
			case SnifferDirection.Warning: severity = EventSeverity.Warning; break;
			case SnifferDirection.Error: severity = EventSeverity.Error; break;
			case SnifferDirection.Exception: severity = EventSeverity.Critical; break;
			default: return null;
		}

		string text = line.Text.Replace("\r\n", "\n");
		string message;
		string rest;

		// The sink separates the message from the fields with one empty line.
		int sep = text.IndexOf("\n\n", StringComparison.Ordinal);
		if (sep < 0)
		{
			message = text;
			rest = string.Empty;
		}
		else
		{
			message = text[..sep];
			rest = text[(sep + 2)..];
		}

		Dictionary<string, StringBuilder> fields = new(StringComparer.OrdinalIgnoreCase);
		List<string> order = [];
		StringBuilder? current = null;

		foreach (string raw in rest.Split('\n'))
		{
			// A field starts at column 0 with "Key: value". Stack frames are indented, so
			// multi-line stack traces stay in one field.
			Match m = FieldLine().Match(raw);
			if (m.Success && !char.IsWhiteSpace(raw, 0) && !LooksLikeStackFrame(raw))
			{
				string key = m.Groups["key"].Value;
				current = new StringBuilder(m.Groups["value"].Value);
				fields[key] = current;
				order.Add(key);
			}
			else if (current is not null)
			{
				current.Append('\n').Append(raw);
			}
			else if (!string.IsNullOrEmpty(raw))
			{
				message += "\n" + raw;
			}
		}

		string? Get(string key) => fields.TryGetValue(key, out StringBuilder? sb) ? sb.ToString().TrimEnd() : null;

		List<KeyValuePair<string, string>> tags = [];
		foreach (string key in order)
		{
			if (!knownFields.Contains(key))
				tags.Add(new KeyValuePair<string, string>(key, Get(key) ?? string.Empty));
		}

		NeuronEvent e = new()
		{
			Source = source,
			Timestamp = line.Timestamp.Kind == DateTimeKind.Utc ? line.Timestamp.ToLocalTime() : line.Timestamp,
			Severity = severity,
			Message = message.TrimEnd(),
			Object = Get("Object"),
			Actor = Get("Actor"),
			EventId = Get("EventId"),
			Facility = Get("Facility"),
			Module = Get("Module"),
			StackTrace = Get("StackTrace"),
			Tags = tags,
		};

		return e with { Fingerprint = EventFingerprint.Compute(e) };
	}

	private static bool LooksLikeStackFrame(string line)
	{
		string t = line.TrimStart();
		return t.StartsWith("at ", StringComparison.Ordinal) || t.StartsWith("---", StringComparison.Ordinal);
	}
}
