using System.Globalization;
using System.Text.Json;
using Neuro.Nat.App.Features;

namespace Neuro.Nat.Features.Telemetry;

/// <summary>What asking a Neuron for app telemetry came to.</summary>
public sealed record TelemetryStatus
{
	public required FeaturePresence Presence { get; init; }

	/// <summary>The web address that answered, when one did.</summary>
	public Uri? WebAddress { get; init; }

	/// <summary>The Neuron's own name for itself in /Status (role "self").</summary>
	public string? Neuron { get; init; }

	/// <summary>The latest day the Neuron has numbers for (role "self"). Null when it has none yet.</summary>
	public DateTime? LastComputedDay { get; init; }

	/// <summary>How many Neurons it collects from (role "source"). Zero when it only has its own numbers.</summary>
	public int Sources { get; init; }

	/// <summary>The privilege the account lacks, when not allowed.</summary>
	public string? Privilege { get; init; }

	/// <summary>Why the answer is unknown, for the log. Never shown.</summary>
	public string? Problem { get; init; }

	public bool IsCollector => this.Sources > 0;

	public static TelemetryStatus Unknown(string problem) => new() { Presence = FeaturePresence.Unknown, Problem = problem };

	public static TelemetryStatus Absent(string problem) => new() { Presence = FeaturePresence.Absent, Problem = problem };

	/// <summary>
	/// Reads a /Status answer: { "neurons": [ { "neuron", "role": "self" | "source", "lastComputedDay": "YYYY-MM-DD" | null, ... } ] }.
	/// Null when it is not one (something else answered on that address).
	/// </summary>
	internal static TelemetryStatus? Parse(string json, Uri webAddress)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object ||
				!doc.RootElement.TryGetProperty("neurons", out JsonElement neurons) || neurons.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			string? self = null;
			DateTime? day = null;
			int sources = 0;
			foreach (JsonElement n in neurons.EnumerateArray())
			{
				if (n.ValueKind != JsonValueKind.Object)
					continue;

				string? role = Text(n, "role");
				if (role == "source")
					sources++;
				else if (role == "self" && self is null)
				{
					self = Text(n, "neuron") ?? string.Empty;
					if (Text(n, "lastComputedDay") is string d &&
						DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
					{
						day = parsed.Date;
					}
				}
			}

			if (self is null)
				return null;

			return new TelemetryStatus { Presence = FeaturePresence.Present, WebAddress = webAddress, Neuron = self, LastComputedDay = day, Sources = sources };
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// The privilege to name when the API says no. Its 403 answer reads "The account needs the
	/// privilege External or Internal."; the internal one gives everything, so that one is named.
	/// </summary>
	internal static string PrivilegeIn(string? json)
	{
		try
		{
			if (!string.IsNullOrEmpty(json))
			{
				using JsonDocument doc = JsonDocument.Parse(json);
				if (doc.RootElement.ValueKind == JsonValueKind.Object && Text(doc.RootElement, "message") is string message)
				{
					System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(message, @"privilege\s+(\S+)\s+or\s+(\S+?)\.?$");
					if (m.Success)
						return m.Groups[2].Value;
				}
			}
		}
		catch (JsonException)
		{
			// Not the package's own answer: name the usual privilege.
		}

		return TelemetryFeature.InternalPrivilege;
	}

	private static string? Text(JsonElement e, string name)
		=> e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
