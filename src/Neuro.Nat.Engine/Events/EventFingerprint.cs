using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Neuro.Nat.Engine.Events;

/// <summary>
/// Groups events that are "the same problem": same severity class, event id, module and
/// the message with volatile parts (numbers, GUIDs, endpoints, JIDs, quoted values)
/// removed, plus the top of the stack trace. Independent of which Neuron logged it, so
/// one issue can span the whole fleet.
/// </summary>
public static partial class EventFingerprint
{
	[GeneratedRegex(@"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}")]
	private static partial Regex Guid();

	[GeneratedRegex(@"\b[0-9a-fA-F]{16,}\b")]
	private static partial Regex LongHex();

	[GeneratedRegex(@"\[[^\]]*\]:\d+|\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b|\b[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+:\d+\b")]
	private static partial Regex Endpoint();

	[GeneratedRegex(@"\b[\w.\-]+@[\w.\-]*(/[\w.\-]+)?")]
	private static partial Regex Jid();

	[GeneratedRegex(@"'[^']*'|""[^""]*""")]
	private static partial Regex Quoted();

	[GeneratedRegex(@"\d+")]
	private static partial Regex Number();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Spaces();

	/// <summary>Normalizes a message so that occurrences of the same problem compare equal.</summary>
	public static string NormalizeMessage(string message)
	{
		string s = message.Trim();
		int nl = s.IndexOfAny(['\r', '\n']);
		if (nl >= 0)
			s = s[..nl];

		s = Guid().Replace(s, "<id>");
		s = LongHex().Replace(s, "<hex>");
		s = Endpoint().Replace(s, "<endpoint>");
		s = Jid().Replace(s, "<jid>");
		s = Quoted().Replace(s, "<value>");
		s = Number().Replace(s, "#");
		s = Spaces().Replace(s, " ");
		return s;
	}

	/// <summary>The first few "at ..." frames, without line numbers.</summary>
	public static string TopFrames(string? stackTrace, int count = 3)
	{
		if (string.IsNullOrWhiteSpace(stackTrace))
			return string.Empty;

		IEnumerable<string> frames = stackTrace
			.Split('\n')
			.Select(l => l.Trim())
			.Where(l => l.StartsWith("at ", StringComparison.Ordinal))
			.Select(l =>
			{
				int i = l.IndexOf(" in ", StringComparison.Ordinal);
				return i > 0 ? l[..i] : l;
			})
			.Take(count);

		return string.Join("|", frames);
	}

	public static string Compute(NeuronEvent e)
	{
		// The stack trace is deliberately left out: events read back from a Neuron's event log
		// carry no stack trace, and must still join the same issue as their live twins.
		string key = string.Join("\u001f",
			e.Severity >= EventSeverity.Error ? "E" : e.Severity.ToString(),
			e.EventId ?? string.Empty,
			e.Module ?? string.Empty,
			NormalizeMessage(e.Message));

		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
		return Convert.ToHexString(hash, 0, 8);
	}
}
