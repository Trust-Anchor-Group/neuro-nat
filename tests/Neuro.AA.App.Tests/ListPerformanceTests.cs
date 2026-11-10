using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.AA.App.ViewModels;
using Neuro.AA.App.Views;
using Neuro.AA.Engine;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Events;
using Neuro.AA.Engine.Sniffing;
using Xunit;

namespace Neuro.AA.App.Tests;

/// <summary>
/// How fast the Events lists scroll and take new events, with a full stream and many issues.
/// Prints timings; fails only when a frame takes far too long.
/// </summary>
public class ListPerformanceTests(ITestOutputHelper output)
{
	[AvaloniaFact]
	public void BareListBaseline()
	{
		ListBox list = new() { ItemsSource = Enumerable.Range(0, 2000).Select(i => "Row " + i).ToList(),
			ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((o, _) => new TextBlock { Text = o, Height = 26 }) };
		Window w = new() { Width = 800, Height = 600, Content = list };
		w.Show();
		Dispatcher.UIThread.RunJobs();
		w.CaptureRenderedFrame();
		ScrollViewer sv = list.GetVisualDescendants().OfType<ScrollViewer>().First();
		Stopwatch sw = Stopwatch.StartNew();
		int f = 0;
		for (double y = 0; y < 20000; y += 120)
		{
			sv.Offset = new Vector(0, y);
			Dispatcher.UIThread.RunJobs();
			w.CaptureRenderedFrame();
			f++;
		}
		output.WriteLine($"Bare list in a bare window: {sw.ElapsedMilliseconds / f} ms per step");

		w.Content = null;
		Border root = new() { Child = list };
		Avalonia.Styling.Container.SetName(root, "Root");
		Avalonia.Styling.Container.SetSizing(root, Avalonia.Styling.ContainerSizing.Width);
		w.Content = root;
		Dispatcher.UIThread.RunJobs();
		w.CaptureRenderedFrame();
		sw.Restart();
		f = 0;
		for (double y = 0; y < 20000; y += 120)
		{
			sv.Offset = new Vector(0, y);
			Dispatcher.UIThread.RunJobs();
			w.CaptureRenderedFrame();
			f++;
		}
		output.WriteLine($"Same list inside a size container: {sw.ElapsedMilliseconds / f} ms per step");
		w.Close();
	}

	[AvaloniaFact]
	public async Task EventsListsScrollSmoothly()
	{
		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "NeuroAA.Perf." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		MonitorService monitors = new(manager, new EventHub());
		DateTime now = DateTime.Now;
		Random r = new(3);

		// Synthetic sample data: 2000 events over 300 different problems.
		void Add(int i)
		{
			SnifferLine line = new(0, now.AddSeconds(-i * 2), i % 7 == 0 ? SnifferDirection.Warning : SnifferDirection.Error,
				$"Problem {r.Next(300)} in module {r.Next(40)}: something failed on node Meter-{r.Next(500)}\n\nModule: Waher.Things.M{r.Next(40)}\nObject: Meter-{r.Next(500)}");
			if (SniffedEventParser.Parse(line, new EventSource(Guid.Empty, $"n{r.Next(6)}@lab.local")) is NeuronEvent e)
				monitors.Hub.Add(e);
		}
		for (int i = 0; i < 2000; i++)
			Add(i);

		MainWindowViewModel vm = new(manager, new FleetService(monitors));
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Events;
		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();
		for (int i = 0; i < 10; i++)
		{
			vm.Events.Flush();
			Dispatcher.UIThread.RunJobs();
			await Task.Delay(30);
		}
		window.CaptureRenderedFrame();
		output.WriteLine($"Stream {vm.Events.Stream.Count}, issues {vm.Events.Issues.Count}");

		Stopwatch idle = Stopwatch.StartNew();
		for (int i = 0; i < 20; i++)
		{
			Dispatcher.UIThread.RunJobs();
			window.CaptureRenderedFrame();
		}
		output.WriteLine($"Capturing a frame with nothing changed: {idle.ElapsedMilliseconds / 20} ms");

		List<ListBox> lists = [.. window.GetVisualDescendants().OfType<ListBox>().Where(l => l.IsEffectivelyVisible && (l.Classes.Contains("stream") || l.Classes.Contains("rows")) && l.ItemCount > 50)];
		foreach (ListBox list in lists)
		{
			ScrollViewer sv = list.GetVisualDescendants().OfType<ScrollViewer>().First();
			Stopwatch sw = Stopwatch.StartNew();
			double worst = 0;
			int frames = 0;
			for (double y = 0; y < Math.Min(sv.Extent.Height - sv.Viewport.Height, 20000); y += 120)
			{
				long t0 = sw.ElapsedTicks;
				sv.Offset = new Vector(0, y);
				Dispatcher.UIThread.RunJobs();
				window.CaptureRenderedFrame();
				worst = Math.Max(worst, (sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency);
				frames++;
			}

			output.WriteLine($"{list.Classes.FirstOrDefault()} list ({list.ItemCount} items): {frames} scroll steps, {sw.ElapsedMilliseconds} ms, {sw.ElapsedMilliseconds / Math.Max(1, frames)} ms per step, worst {worst:F0} ms, realized {list.GetRealizedContainers().Count()}");

			if (Environment.GetEnvironmentVariable("NEUROAA_PERF_EXPERIMENT") == "1")
			{
				var original = list.ItemTemplate;
				list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((o, _) => new TextBlock { Text = o?.ToString(), Height = 26 });
				sv.Offset = default;
				Dispatcher.UIThread.RunJobs();
				window.CaptureRenderedFrame();
				Stopwatch sw2 = Stopwatch.StartNew();
				int f2 = 0;
				for (double y = 0; y < Math.Min(sv.Extent.Height - sv.Viewport.Height, 20000); y += 120)
				{
					sv.Offset = new Vector(0, y);
					Dispatcher.UIThread.RunJobs();
					window.CaptureRenderedFrame();
					f2++;
				}
				output.WriteLine($"   same list, one plain TextBlock per row: {sw2.ElapsedMilliseconds / Math.Max(1, f2)} ms per step");

				string[][] variants = [[], ["mono"], ["meta"], ["mono", "tertiary"]];
				foreach (string[] classes in variants)
				{
					list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((o, _) =>
					{
						Grid g = new() { ColumnDefinitions = new ColumnDefinitions("76,74,Auto,Auto,*"), Height = 26 };
						for (int c = 0; c < 5; c++)
						{
							TextBlock t = new() { Text = o?.ToString(), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
							t.Classes.AddRange(classes);
							Grid.SetColumn(t, c);
							g.Children.Add(t);
						}
						return g;
					});
					sv.Offset = default;
					Dispatcher.UIThread.RunJobs();
					window.CaptureRenderedFrame();
					sw2.Restart();
					f2 = 0;
					for (double y = 0; y < Math.Min(sv.Extent.Height - sv.Viewport.Height, 20000); y += 120)
					{
						sv.Offset = new Vector(0, y);
						Dispatcher.UIThread.RunJobs();
						window.CaptureRenderedFrame();
						f2++;
					}
					output.WriteLine($"   five TextBlocks per row, classes [{string.Join(",", classes)}]: {sw2.ElapsedMilliseconds / Math.Max(1, f2)} ms per step");
				}
				list.ItemTemplate = original;
			}
		}

		// New events arriving while scrolled down in the stream.
		Stopwatch flush = Stopwatch.StartNew();
		long flushOnly = 0;
		for (int b = 0; b < 10; b++)
		{
			for (int i = 0; i < 50; i++)
				Add(-b * 50 - i);
			await Task.Delay(1);
			long f0 = flush.ElapsedMilliseconds;
			vm.Events.Flush();
			flushOnly += flush.ElapsedMilliseconds - f0;
			Dispatcher.UIThread.RunJobs();
			window.CaptureRenderedFrame();
		}
		output.WriteLine($"Flush itself: {flushOnly / 10} ms per batch");
		output.WriteLine($"10 batches of 50 new events: {flush.ElapsedMilliseconds} ms, {flush.ElapsedMilliseconds / 10} ms per batch");
		window.Close();
	}
}
