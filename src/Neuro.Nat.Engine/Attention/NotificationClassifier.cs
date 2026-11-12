using System.Text.RegularExpressions;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.Engine.Attention;

/// <summary>
/// Recognizes what a Neuron's notification is about from its Markdown. The wording comes from
/// the Neuron's own source (IoTGateway and the broker), so the rules follow it closely and fall
/// back to "Other" with the first line as title when nothing matches.
/// </summary>
public static partial class NotificationClassifier
{
	public sealed record Result(
		NotificationKind Kind,
		EventSeverity Severity,
		bool NeedsAction,
		string Title,
		string? Subject,
		IReadOnlyList<NotificationField> Fields,
		IReadOnlyList<NotificationLink> Links);

	private static readonly string[] eventTypes = ["Debug", "Informational", "Notice", "Warning", "Error", "Critical", "Alert", "Emergency"];

	public static Result Classify(string markdown)
	{
		markdown ??= string.Empty;
		string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		IReadOnlyList<NotificationField> fields = ReadFields(lines);
		IReadOnlyList<NotificationLink> links = ReadLinks(markdown);
		(string first, int firstIndex) = FirstLine(lines, 0);
		string l = first.ToLowerInvariant();

		string? Field(params string[] keys)
		{
			foreach (string k in keys)
			{
				NotificationField? f = fields.FirstOrDefault(f => string.Equals(f.Key.TrimEnd(':'), k, StringComparison.OrdinalIgnoreCase));
				if (f is not null && !string.IsNullOrWhiteSpace(f.Value))
					return f.Value;
			}

			return null;
		}

		string? FirstLinkText() => links.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k.Text))?.Text;

		Result R(NotificationKind kind, EventSeverity severity, bool action, string title, string? subject = null)
			=> new(kind, severity, action, title, string.IsNullOrWhiteSpace(subject) ? null : subject, fields, links);

		// Alert and Emergency events, forwarded by the Neuron's alert notifier: the event type as a
		// heading, the message, then an "Information" table with the event's fields.
		if (fields.Any(f => f.Key == "Event ID") || (fields.Any(f => f.Key == "Facility") && fields.Any(f => f.Key == "Timestamp")))
		{
			string type = eventTypes.FirstOrDefault(t => string.Equals(t, first, StringComparison.OrdinalIgnoreCase)) ?? "Alert";
			string message = first;
			if (eventTypes.Contains(type) && string.Equals(type, first, StringComparison.OrdinalIgnoreCase))
				message = FirstLine(lines, firstIndex + 1).Line;

			EventSeverity severity = type switch
			{
				"Critical" or "Alert" or "Emergency" => EventSeverity.Critical,
				"Error" => EventSeverity.Error,
				"Warning" => EventSeverity.Warning,
				_ => EventSeverity.Info,
			};

			return R(NotificationKind.Alert, severity, severity >= EventSeverity.Error, Shorten(message.Length > 0 ? message : type), Field("Object", "Actor"));
		}

		if (l.StartsWith("unauthorized access detected", StringComparison.Ordinal))
			return R(NotificationKind.Security, EventSeverity.Error, true, "Unauthorized access was stopped", Field("Type", "Method"));

		if (l.StartsWith("presence subscription request received from", StringComparison.Ordinal))
		{
			string? who = Bold().Match(markdown) is { Success: true } m ? Plain(m.Groups[1].Value) : null;
			return R(NotificationKind.RosterRequest, EventSeverity.Info, true, who is null ? "Contact request" : "Contact request from " + who, who);
		}

		if (l.StartsWith("legal identity application received", StringComparison.Ordinal))
			return R(NotificationKind.LegalIdApplication, EventSeverity.Info, true, "Legal identity application to review", FirstLinkText());

		if (l.StartsWith("legal identity application has been automatically approved", StringComparison.Ordinal))
			return R(NotificationKind.LegalIdDecision, EventSeverity.Info, false, "Legal identity approved automatically", FirstLinkText());

		if (l.StartsWith("legal identity application has been automatically rejected", StringComparison.Ordinal))
			return R(NotificationKind.LegalIdDecision, EventSeverity.Warning, false, "Legal identity rejected automatically", FirstLinkText());

		if (l.StartsWith("peer reviewed legal id approved", StringComparison.Ordinal))
			return R(NotificationKind.LegalIdDecision, EventSeverity.Info, false, "Peer-reviewed legal identity approved", FirstLinkText());

		if (l.StartsWith("no approved legal identity registered for the gateway", StringComparison.Ordinal))
			return R(NotificationKind.NeuronIdentity, EventSeverity.Warning, true, "The Neuron has no approved legal identity");

		if (l.Contains("approved legal identity for the gateway", StringComparison.Ordinal) && l.Contains("expires", StringComparison.Ordinal))
		{
			string when = first[(l.IndexOf("expires", StringComparison.Ordinal) + "expires".Length)..].Trim().TrimEnd('.');
			bool soon = when.StartsWith("today", StringComparison.OrdinalIgnoreCase) || when.StartsWith("tomorrow", StringComparison.OrdinalIgnoreCase);
			return R(NotificationKind.NeuronIdentity, soon ? EventSeverity.Error : EventSeverity.Warning, true, "The Neuron's legal identity expires " + when);
		}

		if (l.StartsWith("contract proposal received", StringComparison.Ordinal))
			return R(NotificationKind.ContractProposal, EventSeverity.Info, true, "Contract template proposed", FirstLinkText());

		if (l.Contains("waiting for your signature", StringComparison.Ordinal) || markdown.Contains("SignatureRequest.md", StringComparison.OrdinalIgnoreCase))
		{
			NotificationLink? sig = links.FirstOrDefault(k => k.Url.Contains("SignatureRequest.md", StringComparison.OrdinalIgnoreCase));
			bool reminder = l.StartsWith("reminder", StringComparison.Ordinal);
			return R(NotificationKind.SignatureRequest, EventSeverity.Info, true, reminder ? "Reminder: a contract waits for the Neuron's signature" : "A contract waits for the Neuron's signature", sig?.Text ?? FirstLinkText());
		}

		if (l.StartsWith("user requests account recovery", StringComparison.Ordinal))
			return R(NotificationKind.AccountRecovery, EventSeverity.Warning, true, "Someone asks to recover an account", Field("Account", "User Name", "Name", "e-Mail"));

		if (l.StartsWith("account requested", StringComparison.Ordinal))
			return R(NotificationKind.AccountRequest, EventSeverity.Info, true, "Account requested", Field("Name", "e-Mail"));

		if (l.StartsWith("api key requested", StringComparison.Ordinal))
			return R(NotificationKind.ApiKeyRequest, EventSeverity.Info, true, "API key requested", Field("Name", "e-Mail"));

		if (l.StartsWith("feedback received", StringComparison.Ordinal))
			return R(NotificationKind.Feedback, EventSeverity.Info, true, "Feedback received", Field("Name", "e-Mail"));

		if (l.StartsWith("xmpp account created", StringComparison.Ordinal))
			return R(NotificationKind.AccountCreated, EventSeverity.Info, false, "Account created", Field("User Name", "JID"));

		if (l.StartsWith("xmpp account deleted", StringComparison.Ordinal))
			return R(NotificationKind.AccountDeleted, EventSeverity.Info, false, "Account deleted", Field("User Name", "JID"));

		if (l.StartsWith("api key created", StringComparison.Ordinal))
			return R(NotificationKind.ApiKeyCreated, EventSeverity.Info, false, "API key created", Field("Owner", "Key"));

		if (l.Contains("backup", StringComparison.Ordinal))
		{
			if (l.StartsWith("unable to upload backup", StringComparison.Ordinal))
				return R(NotificationKind.Backup, EventSeverity.Error, true, "Backup upload failed", FirstLinkText());

			if (l.StartsWith("backup file has been removed", StringComparison.Ordinal))
				return R(NotificationKind.Backup, EventSeverity.Warning, true, "Backup file removed before upload");

			if (l.Contains("successfully", StringComparison.Ordinal))
				return R(NotificationKind.Backup, EventSeverity.Info, false, "Backup uploaded after a retry");

			return R(NotificationKind.Backup, EventSeverity.Info, false, Shorten(first));
		}

		if (l.StartsWith("new version of installed software has been downloaded", StringComparison.Ordinal))
		{
			bool manual = markdown.Contains("You can update the server", StringComparison.OrdinalIgnoreCase);
			return R(NotificationKind.SoftwareUpdate, EventSeverity.Info, manual,
				manual ? "New software downloaded, ready to install" : "New software downloaded, installs automatically",
				Field("Filename", "Package"));
		}

		if (l.StartsWith("xmpp server at", StringComparison.Ordinal) && l.Contains("restarted", StringComparison.Ordinal))
			return R(NotificationKind.Restarted, EventSeverity.Info, false, l.Contains("updated", StringComparison.Ordinal) ? "Neuron updated and restarted" : "Neuron restarted");

		if (l.StartsWith("user ", StringComparison.Ordinal) && (l.Contains("session", StringComparison.Ordinal) || l.Contains("logged", StringComparison.Ordinal) ||
			l.Contains("console", StringComparison.Ordinal) || l.Contains("remote", StringComparison.Ordinal)) || l == "session changed.")
		{
			bool remote = l.Contains("remote", StringComparison.Ordinal);
			return R(NotificationKind.MachineSession, remote ? EventSeverity.Warning : EventSeverity.Info, false, Shorten(first.TrimEnd('.')), Field("User", "UserName", "Session"));
		}

		if (l.StartsWith("accepting an invitation", StringComparison.Ordinal))
			return R(NotificationKind.ChatRoom, EventSeverity.Info, false, "Joined a chat room", Code().Match(first) is { Success: true } m ? m.Groups[1].Value : null);

		if (l.StartsWith("left room", StringComparison.Ordinal))
		{
			Match m = Code().Match(first);
			string reason = first.Contains(';') ? first[(first.IndexOf(';') + 1)..].Trim().TrimEnd('.') : string.Empty;
			return R(NotificationKind.ChatRoom, EventSeverity.Warning, false, reason.Length > 0 ? "Left a chat room: " + reason : "Left a chat room", m.Success ? m.Groups[1].Value : null);
		}

		if (l.Contains("ip localization database", StringComparison.Ordinal))
			return R(NotificationKind.Maintenance, EventSeverity.Info, false, Shorten(first.TrimEnd('.')));

		string trimmed = markdown.TrimStart();
		if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("![", StringComparison.Ordinal) || trimmed.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
		{
			bool uml = trimmed.StartsWith("```uml", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("@startuml", StringComparison.OrdinalIgnoreCase);
			return R(NotificationKind.Diagram, EventSeverity.Info, false, uml ? "Profiling diagram" : "Diagram");
		}

		return R(NotificationKind.Other, EventSeverity.Info, false, first.Length > 0 ? Shorten(first) : "Notification");
	}

	/// <summary>First line with text from <paramref name="start"/>, skipping setext underlines, tables and code fences.</summary>
	private static (string Line, int Index) FirstLine(string[] lines, int start)
	{
		for (int i = start; i < lines.Length; i++)
		{
			string s = lines[i].Trim();
			if (s.Length == 0 || s.StartsWith('|') || s.StartsWith("```", StringComparison.Ordinal) || Underline().IsMatch(s))
				continue;

			return (Plain(s.TrimStart('#', ' ')), i);
		}

		return (string.Empty, lines.Length);
	}

	/// <summary>Two-column table rows ("| Label | Value |") from every table in the notification.</summary>
	private static IReadOnlyList<NotificationField> ReadFields(string[] lines)
	{
		List<NotificationField> fields = [];
		bool inTable = false;

		foreach (string raw in lines)
		{
			string s = raw.Trim();
			if (!s.StartsWith('|'))
			{
				inTable = false;
				continue;
			}

			string[] cells = Cells(s);
			if (!inTable)
			{
				inTable = true;    // header row
				continue;
			}

			if (cells.All(c => Separator().IsMatch(c.Trim())))
				continue;

			if (cells.Length >= 2)
			{
				string key = Plain(cells[0]);
				string value = Plain(string.Join(" ", cells.Skip(1).Where(c => c.Trim().Length > 0)));
				if (key.Length > 0 && fields.Count < 60)
					fields.Add(new NotificationField(key, value));
			}
		}

		return fields;
	}

	private static string[] Cells(string row)
	{
		string inner = row.Trim();
		if (inner.StartsWith('|'))
			inner = inner[1..];
		if (inner.EndsWith('|') && !inner.EndsWith("\\|", StringComparison.Ordinal))
			inner = inner[..^1];

		return CellSplit().Split(inner);
	}

	private static IReadOnlyList<NotificationLink> ReadLinks(string markdown)
	{
		List<NotificationLink> links = [];
		HashSet<string> seen = new(StringComparer.Ordinal);

		void Add(string text, string url)
		{
			url = url.Trim();
			if (url.Length == 0 || !seen.Add(url))
				return;
			if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
				!url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
				return;

			links.Add(new NotificationLink(Plain(text).Length > 0 ? Plain(text) : url, url));
		}

		foreach (Match m in MarkdownLink().Matches(markdown))
			Add(m.Groups["t"].Value, m.Groups["u"].Value);

		foreach (Match m in HtmlLink().Matches(markdown))
			Add(m.Groups["t"].Value, m.Groups["u"].Value);

		foreach (Match m in AutoLink().Matches(markdown))
			Add(m.Groups["u"].Value, m.Groups["u"].Value);

		return links;
	}

	/// <summary>Markdown to one line of plain text: link text, no emphasis or code marks, no escapes.</summary>
	public static string Plain(string s)
	{
		s = MarkdownLink().Replace(s, "${t}");
		s = HtmlTag().Replace(s, string.Empty);
		s = s.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
		s = Escape().Replace(s, "$1");
		s = System.Net.WebUtility.HtmlDecode(s);
		return Whitespace().Replace(s, " ").Trim();
	}

	private static string Shorten(string s) => s.Length <= 140 ? s : s[..137].TrimEnd() + "…";

	[GeneratedRegex(@"^(=+|-+)$")]
	private static partial Regex Underline();

	[GeneratedRegex(@"^:?-{2,}:?$|^$")]
	private static partial Regex Separator();

	[GeneratedRegex(@"(?<!\\)\|")]
	private static partial Regex CellSplit();

	[GeneratedRegex(@"\[(?<t>[^\]]*)\]\((?<u>[^)\s]+)(?:\s+""[^""]*"")?\)")]
	private static partial Regex MarkdownLink();

	[GeneratedRegex(@"<a\s[^>]*href\s*=\s*\\?""(?<u>[^""\\]+)\\?""[^>]*>(?<t>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
	private static partial Regex HtmlLink();

	[GeneratedRegex(@"<(?<u>https?://[^>\s]+)>")]
	private static partial Regex AutoLink();

	[GeneratedRegex(@"<[^>\n]+>")]
	private static partial Regex HtmlTag();

	[GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|<>&""~^:=])")]
	private static partial Regex Escape();

	[GeneratedRegex(@"\*\*(.+?)\*\*")]
	private static partial Regex Bold();

	[GeneratedRegex(@"`([^`]+)`")]
	private static partial Regex Code();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}
