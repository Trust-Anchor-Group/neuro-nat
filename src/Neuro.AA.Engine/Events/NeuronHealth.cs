using System.Globalization;
using System.Text.RegularExpressions;
using Waher.Content.Xml;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Concentrator;
using Waher.Networking.XMPP.SoftwareVersion;

namespace Neuro.AA.Engine.Events;

/// <summary>A series of per-minute samples from one of a Neuron's performance counters.</summary>
/// <param name="Latest">Mean of the most recent sample, or null if there is none.</param>
/// <param name="Series">Means of recent samples, oldest first.</param>
public sealed record SampleSeries(double? Latest, IReadOnlyList<double> Series, string Unit)
{
	public static readonly SampleSeries Empty = new(null, [], string.Empty);
}

/// <summary>Health of one Neuron, read from its own performance reports.</summary>
public sealed record NeuronHealth
{
	public required Guid AccountId { get; init; }
	public required string NeuronJid { get; init; }

	/// <summary>Software name and version reported by the Neuron's client (XEP-0092).</summary>
	public string? Software { get; init; }
	public string? OperatingSystem { get; init; }

	public SampleSeries Cpu { get; init; } = SampleSeries.Empty;
	public SampleSeries AllocatedMemory { get; init; } = SampleSeries.Empty;
	public SampleSeries FreeMemory { get; init; } = SampleSeries.Empty;
	public SampleSeries FreeDisk { get; init; } = SampleSeries.Empty;

	/// <summary>When these values were read (local time).</summary>
	public DateTime ReadAt { get; init; }

	/// <summary>Why some values are missing, if they are.</summary>
	public string? Error { get; init; }
}

/// <summary>
/// Reads a Neuron's health through its concentrator: the per-minute samples behind the
/// Reports/Performance nodes (CPU, AllocatedMemory, FreeMemory, FreeHD), and the software
/// version its XMPP client reports.
/// </summary>
public static partial class HealthReader
{
	public const string PerformanceSourceId = "Reports";

	/// <summary>How many minutes of samples to read. The Neuron returns a table for up to 50 records.</summary>
	public static int Minutes { get; set; } = 30;

	[GeneratedRegex(@"^\s*(?<v>[-+]?\d+(?:[.,]\d+)?(?:[eE][-+]?\d+)?)\s*(?<u>.*)$")]
	private static partial Regex QuantityText();

	public static async Task<NeuronHealth> ReadAsync(XmppClient client, ConcentratorClient cc, Guid accountId, string bareJid, string fullJid,
		CancellationToken cancel = default)
	{
		List<string> errors = [];

		async Task<SampleSeries> Series(string nodeId)
		{
			try
			{
				return await ReadSamplesAsync(cc, fullJid, nodeId, cancel);
			}
			catch (Exception ex)
			{
				errors.Add($"{nodeId}: {ex.Message}");
				return SampleSeries.Empty;
			}
		}

		string? software = null;
		string? os = null;
		try
		{
			SoftwareVersionEventArgs v = await client.SoftwareVersionAsync(fullJid);
			if (v.Ok)
			{
				software = string.IsNullOrEmpty(v.Version) ? v.Name : $"{v.Name} {v.Version}";
				os = string.IsNullOrWhiteSpace(v.OS) ? null : v.OS;
			}
		}
		catch (Exception ex)
		{
			errors.Add("Version: " + ex.Message);
		}

		SampleSeries cpu = await Series("CPU");
		SampleSeries alloc = await Series("AllocatedMemory");
		SampleSeries free = await Series("FreeMemory");
		SampleSeries disk = await Series("FreeHD");

		return new NeuronHealth
		{
			AccountId = accountId,
			NeuronJid = bareJid,
			Software = software,
			OperatingSystem = os,
			Cpu = cpu,
			AllocatedMemory = alloc,
			FreeMemory = free,
			FreeDisk = disk,
			ReadAt = DateTime.Now,
			Error = errors.Count == 0 ? null : string.Join("; ", errors),
		};
	}

	private static async Task<SampleSeries> ReadSamplesAsync(ConcentratorClient cc, string fullJid, string nodeId, CancellationToken cancel)
	{
		List<(DateTime Start, double Mean, string Unit)> samples = [];
		DateTime now = DateTime.Now;

		await ReportQuery.RunAsync(cc, fullJid, nodeId, PerformanceSourceId, "Execute",
			async form =>
			{
				await ReportQuery.Set(form, "From", XML.Encode(now.AddMinutes(-Minutes)));
				await ReportQuery.Set(form, "To", XML.Encode(now.AddMinutes(1)));
			},
			(row, columns) =>
			{
				if (!columns.TryGetValue("Start", out int si) || !columns.TryGetValue("Mean", out int mi) || si >= row.Length || mi >= row.Length)
					return;

				DateTime? start = EventHistory.ToLocalTime(row[si]);
				if (start is null || !TryQuantity(row[mi], out double value, out string unit))
					return;

				lock (samples)
					samples.Add((start.Value, value, unit));
			},
			TimeSpan.FromSeconds(30), cancel);

		lock (samples)
		{
			if (samples.Count == 0)
				return SampleSeries.Empty;

			List<(DateTime Start, double Mean, string Unit)> ordered = [.. samples.OrderBy(s => s.Start)];
			return new SampleSeries(ordered[^1].Mean, [.. ordered.Select(s => s.Mean)], ordered[^1].Unit);
		}
	}

	/// <summary>Reads a physical quantity cell ("12.5 %", "812 MB", or a plain number).</summary>
	internal static bool TryQuantity(object? cell, out double value, out string unit)
	{
		value = 0;
		unit = string.Empty;

		switch (cell)
		{
			case null:
				return false;
			case double d:
				value = d;
				return true;
			case float f:
				value = f;
				return true;
			case int i:
				value = i;
				return true;
			case long l:
				value = l;
				return true;
			case decimal m:
				value = (double)m;
				return true;
		}

		// Waher physical quantities expose Magnitude and Unit; fall back to the text form.
		Type t = cell.GetType();
		if (t.GetProperty("Magnitude")?.GetValue(cell) is double magnitude)
		{
			value = magnitude;
			unit = t.GetProperty("Unit")?.GetValue(cell)?.ToString() ?? string.Empty;
			return true;
		}

		Match match = QuantityText().Match(cell.ToString() ?? string.Empty);
		if (!match.Success)
			return false;

		string number = match.Groups["v"].Value.Replace(',', '.');
		if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
			return false;

		unit = match.Groups["u"].Value.Trim();
		return true;
	}
}
