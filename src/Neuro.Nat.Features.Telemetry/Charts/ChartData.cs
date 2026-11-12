using System.Globalization;

namespace Neuro.Nat.Features.Telemetry.Charts;

/// <summary>How a chart draws its numbers.</summary>
public enum ChartKind
{
	/// <summary>Lines over time.</summary>
	Line,

	/// <summary>Bars over time, one series.</summary>
	Bars,

	/// <summary>Bars over time, the series stacked.</summary>
	Stacked,

	/// <summary>One bar per group, across: how a total splits.</summary>
	Across,
}

/// <summary>
/// One series. <see cref="Slot"/> picks its colour: 1 to 6 are the chart colours in order
/// (Chart.1 and on), 0 is the neutral one for "undecided" and "other".
/// </summary>
public sealed record ChartSeries(string Name, int Slot, IReadOnlyList<double?> Values)
{
	/// <summary>For rates: how many cases each value is out of. Shown as "of 3k".</summary>
	public IReadOnlyList<double?>? OutOf { get; init; }
}

/// <summary>What a chart shows. The same data is shown as a table on request.</summary>
public sealed record ChartData(ChartKind Kind, IReadOnlyList<string> Labels, IReadOnlyList<ChartSeries> Series)
{
	/// <summary>The long names of the points (for the hover and the table), such as "Wed 4 Nov".</summary>
	public IReadOnlyList<string>? Tips { get; init; }

	/// <summary>The first point whose numbers can still change; -1 when all are final. Hatched.</summary>
	public int SettleFrom { get; init; } = -1;

	/// <summary>Values are percentages.</summary>
	public bool Percent { get; init; }

	/// <summary>A value marked with a thin line across the bars, such as the average.</summary>
	public double? Mark { get; init; }

	/// <summary>What <see cref="Mark"/> is, in the table and the hover.</summary>
	public string? MarkName { get; init; }

	public int Count => this.Labels.Count;

	public bool IsEmpty => this.Count == 0 || this.Series.Count == 0 || this.Series.All(s => s.Values.All(v => v is null));

	public string TipOf(int i) => this.Tips is { } t && i < t.Count ? t[i] : this.Labels[i];

	public bool Settling(int i) => this.SettleFrom >= 0 && i >= this.SettleFrom;

	/// <summary>A value as it is shown: "87.1%", "8,025", or "None" for a rate on too few cases.</summary>
	public string Format(double? v) => Fmt(v, this.Percent);

	public static string Fmt(double? v, bool percent)
		=> v is not double d ? "None"
			: percent ? d.ToString("0.0", CultureInfo.InvariantCulture) + "%"
			: d.ToString(Math.Abs(d - Math.Round(d)) < 0.001 ? "N0" : "N1", CultureInfo.InvariantCulture);

	/// <summary>A count in few characters: 950, 3.1k, 12k, 1.2M.</summary>
	public static string Short(double v)
		=> v >= 1_000_000 ? (v / 1_000_000).ToString(v >= 10_000_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "M"
			: v >= 1_000 ? (v / 1_000).ToString(v >= 10_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "k"
			: v.ToString("0", CultureInfo.InvariantCulture);

	/// <summary>The same numbers as a table: a row per point or group, a column per series.</summary>
	public ChartTable ToTable(string first)
	{
		bool across = this.Kind == ChartKind.Across;
		List<string> head = [first, .. this.Series.Select(s => s.Name)];
		bool outOf = this.Series.Any(s => s.OutOf is not null);
		if (outOf)
			head.AddRange(this.Series.Where(s => s.OutOf is not null).Select(s => this.Series.Count == 1 ? "Out of" : s.Name + ", out of"));
		bool settling = this.SettleFrom >= 0;
		if (settling)
			head.Add("Final");

		List<IReadOnlyList<string>> rows = [];
		for (int i = 0; i < this.Count; i++)
		{
			List<string> row = [across ? this.Labels[i] : this.TipOf(i)];
			row.AddRange(this.Series.Select(s => i < s.Values.Count ? this.Format(s.Values[i]) : string.Empty));
			if (outOf)
				row.AddRange(this.Series.Where(s => s.OutOf is not null).Select(s => i < s.OutOf!.Count && s.OutOf[i] is double d ? Fmt(d, false) : string.Empty));
			if (settling)
				row.Add(this.Settling(i) ? "May change" : "Yes");
			rows.Add(row);
		}

		return new ChartTable(head, rows);
	}
}

/// <summary>A chart's numbers as rows and columns of text.</summary>
public sealed record ChartTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);
