using System.Globalization;
using Waher.Content.Xml;
using Waher.Networking.XMPP.Concentrator;

namespace Neuro.Nat.Engine.Events;

/// <summary>
/// Reads past events from a Neuron through its own admin report: the query command
/// "Execute" on the node "Search Event Log" in the "Reports" data source. Results arrive
/// as one table (Timestamp, Type, Level, Object, Actor, EventId, Module, Facility, Message).
/// Stack traces and tags are not part of that table.
/// </summary>
public static class EventHistory
{
	public const string SearchNodeId = "Search Event Log";
	public const string CommandId = "Execute";

	/// <summary>Outcome of a search.</summary>
	/// <param name="Count">Events received.</param>
	/// <param name="Complete">False if the Neuron stopped answering before the search finished.</param>
	public sealed record SearchResult(int Count, bool Complete);

	/// <summary>
	/// Searches the Neuron's event log. Events are handed to <paramref name="onEvent"/> as they
	/// arrive, so a large result shows up progressively.
	/// </summary>
	/// <param name="includeInformational">Also return Debug, Informational and Notice events.</param>
	public static async Task<SearchResult> SearchAsync(ConcentratorClient cc, string fullJid, EventSource source,
		DateTime from, DateTime to, bool includeInformational, Action<NeuronEvent> onEvent, int maxCount = 1000,
		CancellationToken cancel = default)
	{
		int count = 0;

		ReportQuery.Result r = await ReportQuery.RunAsync(cc, fullJid, SearchNodeId, NeuronMonitor.EventsSourceId, CommandId,
			async form =>
			{
				string info = includeInformational ? "true" : "false";
				await ReportQuery.Set(form, "From", XML.Encode(from));
				await ReportQuery.Set(form, "To", XML.Encode(to));
				await ReportQuery.Set(form, "Offset", "0");
				await ReportQuery.Set(form, "MaxCount", maxCount.ToString(CultureInfo.InvariantCulture));
				await ReportQuery.Set(form, "Debug", info);
				await ReportQuery.Set(form, "Informational", info);
				await ReportQuery.Set(form, "Notice", info);
				foreach (string type in (string[])["Warning", "Error", "Critical", "Alert", "Emergency", "Minor", "Medium", "Major"])
					await ReportQuery.Set(form, type, "true");
			},
			(row, columns) =>
			{
				if (ToEvent(row, columns, source) is NeuronEvent e)
				{
					Interlocked.Increment(ref count);
					onEvent(e);
				}
			},
			TimeSpan.FromSeconds(60), cancel);

		return new SearchResult(count, r.Complete);
	}

	/// <summary>Reads a timestamp cell, converting UTC to local time.</summary>
	internal static DateTime? ToLocalTime(object? value)
	{
		DateTime t = value switch
		{
			DateTime d => d,
			DateTimeOffset o => o.LocalDateTime,
			string s when XML.TryParse(s, out DateTime p) => p,
			string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime p2) => p2,
			_ => DateTime.MinValue,
		};

		if (t == DateTime.MinValue)
			return null;

		return t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
	}

	/// <summary>Converts one result row to an event.</summary>
	internal static NeuronEvent? ToEvent(object[] row, IReadOnlyDictionary<string, int> columns, EventSource source)
	{
		string? Get(string col) => columns.TryGetValue(col, out int i) && i < row.Length ? row[i]?.ToString() : null;

		DateTime? timestamp = ToLocalTime(columns.TryGetValue("Timestamp", out int ti) && ti < row.Length ? row[ti] : null);
		if (timestamp is null)
			return null;

		string type = Get("Type") ?? string.Empty;
		EventSeverity severity = type switch
		{
			"Warning" => EventSeverity.Warning,
			"Error" => EventSeverity.Error,
			"Critical" or "Alert" or "Emergency" => EventSeverity.Critical,
			_ => EventSeverity.Info,
		};

		static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

		NeuronEvent e = new()
		{
			Source = source,
			Timestamp = timestamp.Value,
			Severity = severity,
			Type = Blank(type),
			Level = Blank(Get("Level")),
			Message = Get("Message") ?? string.Empty,
			Object = Blank(Get("Object")),
			Actor = Blank(Get("Actor")),
			EventId = Blank(Get("EventId")),
			Module = Blank(Get("Module")),
			Facility = Blank(Get("Facility")),
			FromHistory = true,
		};

		return e with { Fingerprint = EventFingerprint.Compute(e) };
	}
}
