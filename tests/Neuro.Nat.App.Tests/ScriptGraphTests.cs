using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Neuro.Nat.App.Controls;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Engine;
using SkiaSharp;
using Waher.Script;
using Waher.Script.Graphs;
using Waher.Script.Graphs.Functions.Plots;
using Waher.Script.Objects.VectorSpaces;

namespace Neuro.Nat.App.Tests;

public class ScriptGraphTests
{
	public ScriptGraphTests() => EngineRuntime.Initialize();

	[AvaloniaTheory]
	[InlineData("line")]
	[InlineData("curve")]
	[InlineData("scatter")]
	public async Task BrokerPlotAppearsInlineAndScalesToTheReply(string kind)
	{
		double[] x = [.. Enumerable.Range(0, 121).Select(i => (double)i)];
		DoubleVector axis = new(x);
		IPainter2D painter = kind switch
		{
			"curve" => new Plot2DCurvePainter(),
			"scatter" => new Scatter2DPainter(),
			_ => new Plot2DLinePainter(),
		};
		Graph2D Wave(Func<double, double> fn, SKColor color) => new(new Variables(), axis,
			new DoubleVector(x.Select(i => fn(i / 10)).ToArray()), painter, false, false, null, color, 2d);
		Graph2D graph = (Graph2D)Wave(Math.Sin, SKColors.Red).AddRight(Wave(Math.Cos, SKColors.Blue));
		string xml = graph.ToXml();
		ScriptReplyView view = new() { Reply = new ScriptReply($"```Graph\n{xml}\n```") };
		Window window = new() { Width = 320, Height = 300, Content = view };
		window.Show();
		try
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
			while (!view.GetVisualDescendants().OfType<Image>().Any())
				await Task.Delay(10, timeout.Token);
			Image image = Assert.Single(view.GetVisualDescendants().OfType<Image>());
			window.UpdateLayout();
			Assert.InRange(image.Bounds.Width, 1, 320);
			Bitmap chart = Assert.IsType<Bitmap>(image.Source);
			using MemoryStream stream = new();
			chart.Save(stream, new PngBitmapEncoderOptions());
			stream.Position = 0;
			using SKBitmap pixels = SKBitmap.Decode(stream);
			Assert.Contains(pixels.Pixels, p => p.Red > 200 && p.Blue < 64);
			Assert.Contains(pixels.Pixels, p => p.Blue > 200 && p.Red < 64);
		}
		finally { window.Close(); }
	}

	[AvaloniaTheory]
	[InlineData("Graph", "<Graph>incomplete")]
	[InlineData("Graph", "<FutureGraph />")]
	[InlineData("xml", "<Graph />")]
	public async Task UnrenderedBlocksKeepTheirSelectableSource(string language, string source)
	{
		ScriptReplyView view = new() { Reply = new ScriptReply($"```{language}\n{source}\n```") };
		Window window = new() { Content = view };
		window.Show();
		try
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
			while (!view.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.Text == source))
				await Task.Delay(10, timeout.Token);
		}
		finally { window.Close(); }
	}
}
