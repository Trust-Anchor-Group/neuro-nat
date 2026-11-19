using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Waher.Script;
using Waher.Script.Abstraction.Elements;
using Waher.Script.Graphs;
using Waher.Script.Graphs.Functions.Colors;
using Waher.Script.Graphs.Functions.Plots;
using Waher.Script.Objects;
using Waher.Script.Objects.VectorSpaces;

namespace Neuro.Nat.App.Controls;

/// <summary>Reads the literal numeric series emitted by a broker without evaluating reply text as script.</summary>
internal static partial class BrokerGraph
{
	private static readonly XNamespace Ns = Graph.GraphNamespace;

	public static byte[]? Render(string xml)
	{
		if (xml.Length > 2_000_000)
			return null;
		using StringReader text = new(xml);
		using XmlReader reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
		XElement root = XElement.Load(reader);
		if (root.Name != Ns + "Graph" || (string?)root.Attribute("type") != typeof(Graph2D).FullName ||
			root.Element(Ns + "Graph2D") is not { } data)
			return null;

		// The broker writes each distinct vector once. Later series can reference it by name,
		// including a Y vector that reuses an earlier X vector.
		Dictionary<string, string> definitions = [];
		string[] Series(string name) => data.Elements(Ns + name).Select(e =>
		{
			string value = e.Value.Trim();
			int assignment = value.IndexOf(":=", StringComparison.Ordinal);
			if (assignment < 0)
				return definitions.GetValueOrDefault(value, value);
			string literal = value[(assignment + 2)..].Trim();
			definitions.Add(value[..assignment], literal);
			return literal;
		}).ToArray();

		string[] x = Series("X");
		string[] y = Series("Y");
		string[] styles = Series("Parameters");
		XElement[] painters = data.Elements(Ns + "Painter").ToArray();
		if (painters.Length is 0 or > 64 || painters.Length != x.Length || x.Length != y.Length || x.Length != styles.Length)
			return null;

		Graph2D? graph = null;
		int remainingPoints = 100_000;
		Variables variables = new();
		for (int i = 0; i < painters.Length; i++)
		{
			IPainter2D? painter = painters[i].Value switch
			{
				"Waher.Script.Graphs.Functions.Plots.Plot2DLinePainter" => new Plot2DLinePainter(),
				"Waher.Script.Graphs.Functions.Plots.Plot2DCurvePainter" => new Plot2DCurvePainter(),
				"Waher.Script.Graphs.Functions.Plots.Scatter2DPainter" => new Scatter2DPainter(),
				_ => null,
			};
			Match style = PenStyle().Match(styles[i]);
			if (painter is null || !style.Success || !Color.TryParse(style.Groups["color"].Value, out var color) ||
				!double.TryParse(style.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double size) ||
				size is <= 0 or > 128)
				return null;

			double[]? xs = JsonSerializer.Deserialize<double[]>(x[i]);
			double[]? ys = JsonSerializer.Deserialize<double[]>(y[i]);
			if (xs is not { Length: > 0 } || ys?.Length != xs.Length || xs.Length > remainingPoints ||
				!xs.All(double.IsFinite) || !ys.All(double.IsFinite))
				return null;
			remainingPoints -= xs.Length;
			Graph2D series = new(variables, new DoubleVector(xs), new DoubleVector(ys), painter, false, false, null, color, size);
			graph = graph is null ? series : (Graph2D)graph.AddRight(series);
		}

		graph!.Title = (string?)data.Attribute("title") ?? "";
		graph.LabelX = (string?)data.Attribute("labelX") ?? "";
		graph.LabelY = (string?)data.Attribute("labelY") ?? "";
		graph.ShowXAxis = (bool?)data.Attribute("showXAxis") ?? true;
		graph.ShowYAxis = (bool?)data.Attribute("showYAxis") ?? true;
		graph.ShowGrid = (bool?)data.Attribute("showGrid") ?? true;
		graph.SameScale = (bool?)root.Attribute("sameScale") ?? false;
		graph.MinX = AxisLimit(data, "minX", graph.MinX);
		graph.MaxX = AxisLimit(data, "maxX", graph.MaxX);
		graph.MinY = AxisLimit(data, "minY", graph.MinY);
		graph.MaxY = AxisLimit(data, "maxY", graph.MaxY);

		GraphSettings settings = new();
		if (root.Element(Ns + "Settings") is { } s)
		{
			settings.Width = Math.Clamp((int?)s.Attribute("width") ?? settings.Width, 160, 1600);
			settings.Height = Math.Clamp((int?)s.Attribute("height") ?? settings.Height, 120, 1200);
			if (Color.TryParse((string?)s.Attribute("backgroundColor") ?? "", out var background))
				settings.BackgroundColor = background;
			if (Color.TryParse((string?)s.Attribute("axisColor") ?? "", out var axis))
				settings.AxisColor = axis;
			if (Color.TryParse((string?)s.Attribute("gridColor") ?? "", out var grid))
				settings.GridColor = grid;
		}
		return graph.CreatePixels(settings).EncodeAsPng();
	}

	private static IElement AxisLimit(XElement data, string name, IElement fallback)
		=> double.TryParse((string?)data.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n)
			? new DoubleNumber(n) : fallback;

	[GeneratedRegex("""^\[\s*(?:Color\("(?<color>[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)"\)|"(?<color>[#a-zA-Z0-9]+)")\s*,\s*(?<size>[0-9.eE+-]+)\s*\]$""")]
	private static partial Regex PenStyle();
}
