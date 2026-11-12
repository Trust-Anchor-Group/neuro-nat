using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuro.Nat.Features.Telemetry.Api;

// The telemetry API's answers, as docs/API.md of Neuro.App.Telemetry describes them: /Catalog,
// /Query and /Status. Names are case sensitive, as the API's are.

/// <summary>A dimension in /Catalog.</summary>
public sealed record CatalogDimension
{
	[JsonPropertyName("audience")] public string? Audience { get; init; }
	[JsonPropertyName("description")] public string? Description { get; init; }

	/// <summary>The fixed values in display order, when the dimension has a fixed list.</summary>
	[JsonPropertyName("values")] public IReadOnlyList<string>? Values { get; init; }
}

/// <summary>A metric in /Catalog.</summary>
public sealed record CatalogMetric
{
	[JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
	[JsonPropertyName("title")] public string? Title { get; init; }
	[JsonPropertyName("kind")] public string Kind { get; init; } = "count";
	[JsonPropertyName("unit")] public string? Unit { get; init; }
	[JsonPropertyName("numerator")] public string? Numerator { get; init; }
	[JsonPropertyName("denominator")] public string? Denominator { get; init; }
	[JsonPropertyName("dimensions")] public IReadOnlyList<string> Dimensions { get; init; } = [];
	[JsonPropertyName("hasTotal")] public bool? HasTotal { get; init; }
	[JsonPropertyName("excludedValues")] public IReadOnlyDictionary<string, IReadOnlyList<string>>? ExcludedValues { get; init; }
	[JsonPropertyName("followUpDays")] public int FollowUpDays { get; init; }
	[JsonPropertyName("audience")] public string? Audience { get; init; }
	[JsonPropertyName("description")] public string? Description { get; init; }
	[JsonPropertyName("deprecated")] public bool Deprecated { get; init; }

	public bool IsRate => this.Kind == "rate";
	public bool IsDistinct => this.Kind == "distinct";
	public bool Total => this.HasTotal ?? true;
	public string Name => string.IsNullOrWhiteSpace(this.Title) ? this.Key : this.Title;
}

/// <summary>The answer of /Catalog: what this account may ask for.</summary>
public sealed record Catalog
{
	[JsonPropertyName("metricsVersion")] public int MetricsVersion { get; init; }

	/// <summary>"external" (partners) or "internal" (the team).</summary>
	[JsonPropertyName("access")] public string Access { get; init; } = "external";
	[JsonPropertyName("minimumDenominator")] public int MinimumDenominator { get; init; } = 5;
	[JsonPropertyName("dimensions")] public IReadOnlyDictionary<string, CatalogDimension> Dimensions { get; init; } = new Dictionary<string, CatalogDimension>();
	[JsonPropertyName("metrics")] public IReadOnlyList<CatalogMetric> Metrics { get; init; } = [];

	public bool IsInternal => this.Access == "internal";

	/// <summary>Only accounts that see everything may split by Neuron.</summary>
	public bool HasNeurons => this.Dimensions.ContainsKey(TelemetryQuery.Neuron);

	public CatalogMetric? Metric(string key) => this.Metrics.FirstOrDefault(m => m.Key == key);

	public bool Has(string key) => this.Metric(key) is not null;

	/// <summary>True when the metric is there and can be split or filtered by the dimension.</summary>
	public bool Has(string key, string dimension)
		=> dimension == TelemetryQuery.Neuron ? this.HasNeurons && this.Has(key)
			: this.Metric(key) is CatalogMetric m && m.Dimensions.Contains(dimension) && this.Dimensions.ContainsKey(dimension);

	/// <summary>Whether a metric's recent numbers can still change, because its outcomes are followed.</summary>
	public bool FollowsUp(string key) => this.Metric(key)?.FollowUpDays > 0;
}

/// <summary>One point of a series.</summary>
public sealed record QueryPoint
{
	[JsonPropertyName("start")] public string Start { get; init; } = string.Empty;
	[JsonPropertyName("end")] public string End { get; init; } = string.Empty;

	/// <summary>Null for a rate based on too few cases.</summary>
	[JsonPropertyName("value")] public double? Value { get; init; }
	[JsonPropertyName("numerator")] public double? Numerator { get; init; }
	[JsonPropertyName("denominator")] public double? Denominator { get; init; }
	[JsonPropertyName("final")] public bool Final { get; init; } = true;

	public DateTime StartDay => TelemetryQuery.ParseDay(this.Start) ?? DateTime.MinValue;
	public DateTime EndDay => TelemetryQuery.ParseDay(this.End) ?? DateTime.MinValue;
}

/// <summary>One series: a metric, for one group.</summary>
public sealed record QuerySeries
{
	[JsonPropertyName("metric")] public string Metric { get; init; } = string.Empty;
	[JsonPropertyName("kind")] public string? Kind { get; init; }
	[JsonPropertyName("unit")] public string? Unit { get; init; }
	[JsonPropertyName("group")] public IReadOnlyDictionary<string, string> Group { get; init; } = new Dictionary<string, string>();
	[JsonPropertyName("points")] public IReadOnlyList<QueryPoint> Points { get; init; } = [];

	/// <summary>The group's value for a dimension; "Unknown" when it was empty.</summary>
	public string ValueOf(string dimension) => this.Group.TryGetValue(dimension, out string? v) && !string.IsNullOrEmpty(v) ? v : "Unknown";

	public double? Total => this.Points.Count > 0 ? this.Points[0].Value : null;

	public double Sum => this.Points.Sum(p => p.Value ?? 0);
}

/// <summary>The answer of /Query.</summary>
public sealed record QueryAnswer
{
	[JsonPropertyName("from")] public string From { get; init; } = string.Empty;
	[JsonPropertyName("to")] public string To { get; init; } = string.Empty;
	[JsonPropertyName("interval")] public string Interval { get; init; } = "day";
	[JsonPropertyName("complete")] public bool Complete { get; init; } = true;
	[JsonPropertyName("neurons")] public IReadOnlyList<string>? Neurons { get; init; }
	[JsonPropertyName("series")] public IReadOnlyList<QuerySeries> Series { get; init; } = [];

	public static QueryAnswer Empty { get; } = new();

	/// <summary>The series of a metric without a group, or for one group value.</summary>
	public QuerySeries? Find(string metric, string? dimension = null, string? value = null)
		=> dimension is null
			? this.Series.FirstOrDefault(s => s.Metric == metric && s.Group.Count == 0) ?? this.Series.FirstOrDefault(s => s.Metric == metric)
			: this.Series.FirstOrDefault(s => s.Metric == metric && s.ValueOf(dimension) == value);

	public IEnumerable<QuerySeries> All(string metric) => this.Series.Where(s => s.Metric == metric);

	/// <summary>The one number of a metric with interval total, or null.</summary>
	public double? TotalOf(string metric) => this.Find(metric)?.Total;

	/// <summary>The latest point's value of a metric.</summary>
	public double? LastOf(string metric) => this.Find(metric)?.Points is { Count: > 0 } p ? p[^1].Value : null;
}

/// <summary>One Neuron in /Status.</summary>
public sealed record StatusNeuron
{
	[JsonPropertyName("neuron")] public string Neuron { get; init; } = string.Empty;
	[JsonPropertyName("role")] public string Role { get; init; } = "self";
	[JsonPropertyName("metricsVersion")] public int? MetricsVersion { get; init; }
	[JsonPropertyName("lastComputedDay")] public string? LastComputedDay { get; init; }
	[JsonPropertyName("lastCompute")] public DateTime? LastCompute { get; init; }
	[JsonPropertyName("lastComputeError")] public string? LastComputeError { get; init; }
	[JsonPropertyName("computing")] public bool? Computing { get; init; }
	[JsonPropertyName("lastCollect")] public DateTime? LastCollect { get; init; }
	[JsonPropertyName("lastCollectSuccess")] public DateTime? LastCollectSuccess { get; init; }
	[JsonPropertyName("lastCollectError")] public string? LastCollectError { get; init; }
	[JsonPropertyName("lastCollectFacts")] public long? LastCollectFacts { get; init; }

	public bool IsSelf => this.Role == "self";
	public DateTime? LastDay => TelemetryQuery.ParseDay(this.LastComputedDay);
}

/// <summary>The answer of /Status.</summary>
public sealed record StatusAnswer
{
	[JsonPropertyName("neurons")] public IReadOnlyList<StatusNeuron> Neurons { get; init; } = [];

	public StatusNeuron? Self => this.Neurons.FirstOrDefault(n => n.IsSelf);
}

/// <summary>Reads the API's answers.</summary>
public static class TelemetryJson
{
	private static readonly JsonSerializerOptions Options = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

	/// <summary>A /Catalog answer, or null when the JSON is not one (it has no list of metrics).</summary>
	public static Catalog? Catalog(string json) => json.Contains("\"metrics\"", StringComparison.Ordinal) ? Read<Catalog>(json) : null;

	public static QueryAnswer? Query(string json) => Read<QueryAnswer>(json);

	public static StatusAnswer? Status(string json) => Read<StatusAnswer>(json);

	/// <summary>The message of an error answer ({ "error", "message" }), or null.</summary>
	public static string? ErrorMessage(string json)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String
				? m.GetString() : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static T? Read<T>(string json) where T : class
	{
		try
		{
			return JsonSerializer.Deserialize<T>(json, Options);
		}
		catch (JsonException)
		{
			return null;
		}
	}
}

/// <summary>A question for /Query, written the way the API wants it.</summary>
public sealed record TelemetryQuery
{
	public const string Neuron = "neuron";

	public IReadOnlyList<string> Metrics { get; init; } = [];
	public DateTime? From { get; init; }
	public DateTime? To { get; init; }

	/// <summary>day, week, month or total.</summary>
	public string Interval { get; init; } = "day";

	/// <summary>At most "neuron" and one other dimension.</summary>
	public IReadOnlyList<string> GroupBy { get; init; } = [];

	/// <summary>Dimension and the values to keep.</summary>
	public IReadOnlyList<(string Dimension, IReadOnlyList<string> Values)> Filters { get; init; } = [];

	public static TelemetryQuery Of(params string[] metrics) => new() { Metrics = metrics };

	public TelemetryQuery Over(DateTime from, DateTime to, string interval = "day") => this with { From = from.Date, To = to.Date, Interval = interval };

	public TelemetryQuery By(params string[] dimensions) => this with { GroupBy = dimensions };

	public TelemetryQuery Where(string dimension, params string[] values) => this with { Filters = [.. this.Filters, (dimension, values)] };

	/// <summary>The dimensions the query uses.</summary>
	public IEnumerable<string> Dimensions => this.GroupBy.Concat(this.Filters.Select(f => f.Dimension)).Distinct();

	/// <summary>
	/// The same question with only the metrics the account may ask for with these dimensions, so
	/// one hidden metric does not fail a whole panel. Null when none is left.
	/// </summary>
	public TelemetryQuery? NarrowTo(Catalog catalog)
	{
		List<string> metrics = [.. this.Metrics.Where(m => catalog.Has(m) && this.Dimensions.All(d => catalog.Has(m, d)))];
		if (metrics.Count == 0)
			return null;

		// A metric without a total needs a split, and people counts have no total over a range.
		metrics.RemoveAll(m => catalog.Metric(m) is CatalogMetric c &&
			((!c.Total && !this.Dimensions.Any(d => d != Neuron)) || (c.IsDistinct && this.Interval == "total")));
		return metrics.Count == 0 ? null : this with { Metrics = metrics };
	}

	/// <summary>"/Query?metrics=...", with | written as %7C and , and : as they are.</summary>
	public string ToPath()
	{
		StringBuilder sb = new("/Query?metrics=");
		sb.Append(string.Join(",", this.Metrics.Select(Encode)));
		if (this.From is DateTime from)
			sb.Append("&from=").Append(Day(from));
		if (this.To is DateTime to)
			sb.Append("&to=").Append(Day(to));
		if (this.Interval != "day")
			sb.Append("&interval=").Append(Encode(this.Interval));
		if (this.GroupBy.Count > 0)
			sb.Append("&groupBy=").Append(string.Join(",", this.GroupBy.Select(Encode)));
		if (this.Filters.Count > 0)
			sb.Append("&filter=").Append(string.Join(",", this.Filters.Select(f => Encode(f.Dimension) + ":" + string.Join("%7C", f.Values.Select(Encode)))));
		return sb.ToString();
	}

	public static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	public static DateTime? ParseDay(string? s)
		=> DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime d)
			? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;

	private static string Encode(string s) => Uri.EscapeDataString(s);
}
