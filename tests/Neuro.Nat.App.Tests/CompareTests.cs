using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine.Compare;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Links;
using Neuro.Nat.Engine.Software;
using Sample = Neuro.Nat.App.Tests.GettingAroundTests.Sample;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// Comparing two Neurons on screen: started from two Neurons selected in the list, from
/// "Compare with" on a Neuron's page, from the search box and from a link; with a kept copy;
/// with no access on one side. Renders the comparison light and dark at 1440 and 760 wide.
/// </summary>
public class CompareTests
{
	private const string Lab = "lab.tagroot.io";
	private const string Prod = "prod.example.com";
	private const string LabSecret = "Lab-mqtt-pw-8812-kiwi";
	private const string ProdSecret = "Prod-mqtt-pw-4471-fig";

	/// <summary>Hands out made-up readings in place of the Neurons.</summary>
	private sealed class StandIn : INeuronReader
	{
		public HashSet<string> NoAccess { get; } = new(StringComparer.OrdinalIgnoreCase);

		public int? AutoMinutes { get; set; }

		public Task<NeuronSnapshot> ReadAsync(Guid accountId, string neuronJid, CancellationToken cancel = default)
			=> Task.FromResult(this.NoAccess.Contains(neuronJid)
				? new NeuronSnapshot { NeuronJid = neuronJid, ReadAt = DateTime.UtcNow, NoAccess = true }
				: Snapshot(neuronJid, neuronJid == Prod, this.AutoMinutes));
	}

	/// <summary>Lab and Production differ in eight things, as in the engine's tests; any other Neuron reads as Lab does.</summary>
	private static NeuronSnapshot Snapshot(string jid, bool production, int? autoMinutes = null)
	{
		List<SnapshotValue> v = [];
		v.AddRange(LiveNeuronReader.SoftwareValues(new NeuronSoftware
		{
			AccountId = Guid.Empty,
			NeuronJid = jid,
			BuildTime = production ? "2026-11-10, 08:12:40" : "2026-10-28, 09:14:10",
			AutoDelayMinutes = autoMinutes ?? (production ? null : 15),
			ContentAutoInstall = false,
			Packages =
			[
				new SoftwarePackage(production ? "TAG.Neuron.3.11.2.package" : "TAG.Neuron.3.11.1.package", "48 MB", "2026-10-28", null, "2026-10-28", "2026-10-28 14:46", Lab),
				new SoftwarePackage("TAG.Payments.2.4.0.package", "1 MB", "2026-10-02", null, "2026-10-02", "2026-10-02 10:05", Lab),
				.. production ? new[] { new SoftwarePackage("TAG.Identity.1.2.0.package", "2 MB", "2026-09-01", null, "2026-09-01", "2026-09-01 10:05", Lab) } : [],
			],
		}, production ? "Waher.IoTGateway 3.11.2.0" : "Waher.IoTGateway 3.11.1.0"));

		foreach ((int n, string protocol) in production ? new[] { (5222, "XMPP.C2S"), (5269, "XMPP.S2S"), (443, "HTTPS") } : [(5222, "XMPP.C2S"), (5269, "XMPP.S2S"), (8080, "HTTP")])
		{
			string port = n.ToString(CultureInfo.InvariantCulture);
			v.AddRange(LiveNeuronReader.SettingValues("Ports", "Ports", "Ports/" + port, port, [("Protocol", "Protocol:", protocol, false)]));
		}

		v.AddRange(LiveNeuronReader.SettingValues("WebServer", LiveNeuronReader.DomainsSection, "WebServer/www.example.com", "www.example.com",
			[("RemoteDomain", "Remote Domain:", production ? "10.0.0.9" : "10.0.0.5", false)]));
		v.AddRange(LiveNeuronReader.SettingValues("LoginAuditor", "Sign-in guard", "LoginAuditor/1", "1", [("Attempts", "Number of attempts:", "5", false)]));
		v.AddRange(LiveNeuronReader.SettingValues("EventSinks", "Where events are sent", "EventSinks/MqttSink", "MqttSink",
			[("Broker", "Broker:", "mqtt.example.com", false), ("Password", "Password:", production ? ProdSecret : LabSecret, false)]));
		v.AddRange(LiveNeuronReader.StateValues(new NeuronHealth
		{
			AccountId = Guid.Empty,
			NeuronJid = jid,
			Cpu = new SampleSeries(production ? 37 : 14, [], "%"),
			AllocatedMemory = new SampleSeries(production ? 1800 : 640, [], "MB"),
			FreeMemory = new SampleSeries(5210, [], "MB"),
			FreeDisk = new SampleSeries(production ? 40.2 : 182.4, [], "GB"),
		}, production ? "312" : "18"));

		return new NeuronSnapshot { NeuronJid = jid, ReadAt = DateTime.UtcNow, Values = v };
	}

	private static async Task<(Sample S, StandIn Reader)> StartAsync()
	{
		Sample s = await GettingAroundTests.SampleAsync();
		StandIn reader = new();
		s.Vm.Compare.Reader = reader;
		return (s, reader);
	}

	private static async Task Settle(CompareViewModel c)
	{
		for (int i = 0; i < 100 && c.IsBusy; i++)
		{
			await Task.Delay(10);
			Dispatcher.UIThread.RunJobs();
		}

		Dispatcher.UIThread.RunJobs();
	}

	private static FleetRowViewModel Row(Sample s, string jid) => s.Vm.Fleet.Rows.First(r => r.BareJid == jid);

	// ----- Where it starts -----

	[AvaloniaFact]
	public async Task TwoNeuronsSelectedInTheListCompare()
	{
		(Sample s, _) = await StartAsync();
		using Sample _s = s;

		IReadOnlyList<ItemAction> one = s.Vm.Fleet.ActionsFor([Row(s, Lab)]);
		Assert.DoesNotContain(one, a => a.Text == "Compare");
		Assert.Contains(one, a => a.Text == "Compare with…");

		IReadOnlyList<ItemAction> two = s.Vm.Fleet.ActionsFor([Row(s, Lab), Row(s, Prod)]);
		ItemAction compare = two.Single(a => a.Text == "Compare");
		Assert.True(compare.InBar);
		Assert.DoesNotContain(s.Vm.Fleet.ActionsFor([Row(s, Lab), Row(s, Prod), Row(s, "gbg.lab.tagroot.io")]), a => a.Text == "Compare");

		await compare.RunAsync();
		await Settle(s.Vm.Compare);

		Assert.True(s.Vm.IsCompare);
		Assert.Equal("Compare", s.Vm.WorkspaceTitle);
		Assert.True(s.Vm.HasCrumb);
		CompareViewModel c = s.Vm.Compare;
		Assert.True(c.HasResult);
		Assert.Equal(("Lab", "Production"), (c.LeftName, c.RightName));
		Assert.Equal("9 differences in 5 groups", c.CountText);

		// Only the differences show at first; the state now always shows, and is not counted.
		Assert.True(c.OnlyDifferences);
		Assert.Equal(["Software", "Settings", "State now"], c.Groups.Select(g => g.Title));
		Assert.Equal("Not counted", c.Groups[2].Meta);
		Assert.DoesNotContain(c.Groups.SelectMany(g => g.Sections).SelectMany(x => x.Rows), r => r.Label == "Port 5222");
		c.OnlyDifferences = false;
		Assert.Contains(c.Groups.SelectMany(g => g.Sections).SelectMany(x => x.Rows), r => r.Label == "Port 5222");

		// Both Neurons were kept as they were read.
		Assert.Single(await c.Store.ListAsync(Lab));
		Assert.Single(await c.Store.ListAsync(Prod));
	}

	[AvaloniaFact]
	public async Task CompareWithOnANeuronsPageAsksForTheOtherInPlace()
	{
		(Sample s, _) = await StartAsync();
		using Sample _s = s;

		s.Vm.OpenNeuron(Lab);
		Assert.Contains(s.Vm.ActionsFor([s.Vm.Page!]), a => a.Text == "Compare with…");
		s.Vm.ComparePageCommand.Execute(null);

		CompareViewModel c = s.Vm.Compare;
		Assert.True(s.Vm.IsCompare);
		Assert.True(c.IsChoosing);
		Assert.Equal("Compare Lab with", c.ChooseTitle);
		Assert.DoesNotContain(c.Choices, x => x.Title == "Lab");
		CompareChoice production = c.Choices.Single(x => x.Title == "Production");

		await c.PickCommand.ExecuteAsync(production);
		await Settle(c);
		Assert.False(c.IsChoosing);
		Assert.Equal(("Lab", "Production"), (c.LeftName, c.RightName));
	}

	[AvaloniaFact]
	public async Task CompareAndTwoNamesInTheSearchBox()
	{
		(Sample s, _) = await StartAsync();
		using Sample _s = s;

		s.Vm.Search.Open();
		s.Vm.Search.Query = "compare lab prod";
		SearchItem item = s.Vm.Search.Results.First(r => r.Title == "Compare Lab and Production");
		Assert.Equal("Do", item.Kind);
		Assert.Equal(item, s.Vm.Search.Results.First(r => r.Title.StartsWith("Compare Lab", StringComparison.Ordinal)));

		s.Vm.Search.Selected = item;
		s.Vm.Search.Run();
		await Settle(s.Vm.Compare);
		Assert.True(s.Vm.IsCompare);
		Assert.Equal(("Lab", "Production"), (s.Vm.Compare.LeftName, s.Vm.Compare.RightName));

		// "compare" alone offers to pick them.
		s.Vm.Search.Open();
		s.Vm.Search.Query = "compare";
		Assert.Contains(s.Vm.Search.Results, r => r.Title == "Compare two Neurons");
		s.Vm.Search.Close();
	}

	// ----- The link -----

	[AvaloniaFact]
	public async Task TheLinkCopiesAndOpensTheSameComparison()
	{
		(Sample s, _) = await StartAsync();
		using Sample _s = s;

		s.Vm.CompareNeurons((s.Ops, Lab), (s.Admin, Prod));
		await Settle(s.Vm.Compare);

		NatLink link = Assert.IsType<NatLink>(Links.For(s.Vm.Compare));
		Assert.Equal("neuro-nat://compare/lab.tagroot.io/prod.example.com", link.ToString());

		// "Copy link" is on the menu, as on every other, from the one shared place.
		IReadOnlyList<ItemAction> menu = Links.WithLink(s.Vm.Compare.ActionsFor([s.Vm.Compare]), [s.Vm.Compare]);
		Assert.Contains(menu, a => a.Text == "Copy link");
		Assert.Contains(menu, a => a.Text == "Copy as text");
		await s.Vm.Compare.CopyLinkCommand.ExecuteAsync(null);
		Assert.Equal(link.ToString(), s.Copied[^1]);

		// Opened elsewhere, from the screen of all Neurons, it shows the same two.
		s.Vm.ShowFleetCommand.Execute(null);
		s.Vm.Compare.Show(NeuronComparer.Compare(Snapshot(Lab, false), Snapshot(Lab, false), "x", "y"));
		Assert.Equal(LinkOutcome.Opened, await s.Vm.OpenLinkAsync(link.ToString()));
		await Settle(s.Vm.Compare);
		Assert.True(s.Vm.IsCompare);
		Assert.Equal(("Lab", "Production"), (s.Vm.Compare.LeftName, s.Vm.Compare.RightName));
		Assert.Equal("Lab and Production", s.Vm.DescribeLink(link).Title);

		Assert.Equal(LinkOutcome.NotThere, await s.Vm.OpenLinkAsync("neuro-nat://compare/lab.tagroot.io/elsewhere.example.org"));
	}

	// ----- Kept copies -----

	[AvaloniaFact]
	public async Task KeepACopyAndCompareWithEarlier()
	{
		(Sample s, StandIn reader) = await StartAsync();
		using Sample _s = s;

		IReadOnlyList<ItemAction> actions = s.Vm.NeuronActions(Lab);
		Assert.Contains(actions, a => a.Text == "Compare with earlier…");

		// Nothing kept yet: the screen says so, and offers to keep one.
		s.Vm.CompareWithEarlier(Lab);
		await Settle(s.Vm.Compare);
		CompareViewModel c = s.Vm.Compare;
		Assert.True(c.IsChoosing);
		Assert.False(c.HasChoices);
		Assert.Equal("No copies of Lab kept yet", c.ChooseEmpty);
		Assert.True(c.CanKeepFromChoice);

		await actions.Single(a => a.Text == "Keep a copy now").RunAsync();
		Assert.Contains("Kept a copy of Lab", s.Said);
		CompareChoice kept = Assert.Single(c.Choices);

		// Since then, automatic updates were set to an hour.
		reader.AutoMinutes = 60;
		await c.PickCommand.ExecuteAsync(kept);
		await Settle(c);
		Assert.True(c.Right!.IsKept);
		Assert.Equal("1 difference in 1 group", c.CountText);
		Assert.StartsWith("Kept ", c.RightWhen, StringComparison.Ordinal);
		CompareRowViewModel row = c.Groups[0].Sections[0].Rows.Single();
		Assert.Equal(("After 60 min", "After 15 min", "≠"), (row.Left, row.Right, row.Mark));

		// Its link names the copy, and opens it again.
		NatLink link = Assert.IsType<NatLink>(Links.For(c));
		Assert.Equal(c.Right.CopyId, link.CopyId);
		s.Vm.ShowFleetCommand.Execute(null);
		Assert.Equal(LinkOutcome.Opened, await s.Vm.OpenLinkAsync(link.ToString()));
		await Settle(c);
		Assert.True(s.Vm.IsCompare);
		Assert.Equal(link, Links.For(c));

		// A copy that is not kept any more: the copies there are, and a note.
		Assert.Equal(LinkOutcome.Gone, await s.Vm.OpenLinkAsync(NatLink.CompareWithKept(Lab, Guid.NewGuid().ToString("N")).ToString()));
		await Settle(c);
		Assert.Contains(MainWindowViewModel.CopyGone, s.Said);
		Assert.True(c.IsChoosing);
	}

	// ----- Secrets and rights -----

	[AvaloniaFact]
	public async Task APasswordIsNeverOnScreenOrInTheText()
	{
		(Sample s, _) = await StartAsync();
		using Sample _s = s;

		s.Vm.CompareNeurons((s.Ops, Lab), (s.Admin, Prod));
		await Settle(s.Vm.Compare);
		CompareViewModel c = s.Vm.Compare;
		CompareRowViewModel pw = c.Groups.SelectMany(g => g.Sections).SelectMany(x => x.Rows).Single(r => r.Label == "MqttSink, Password");
		Assert.Equal(("Different", "Different"), (pw.Left, pw.Right));
		Assert.False(pw.IsMono);

		c.OnlyDifferences = false;
		await c.CopyTextCommand.ExecuteAsync(null);
		string text = s.Copied[^1];
		Assert.Contains("MqttSink, Password: Different | Different", text, StringComparison.Ordinal);

		MainWindow window = GettingAroundTests.Window(s, "Light");
		string shown = string.Join("\n", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
		foreach (string secret in new[] { LabSecret, ProdSecret })
		{
			Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
			Assert.DoesNotContain(secret, shown, StringComparison.Ordinal);
		}

		window.Close();
	}

	[AvaloniaFact]
	public async Task NoAccessOnOneSideIsANoteAndTheOtherSideStillShows()
	{
		(Sample s, StandIn reader) = await StartAsync();
		using Sample _s = s;
		reader.NoAccess.Add(Prod);

		s.Vm.CompareNeurons((s.Ops, Lab), (s.Admin, Prod));
		await Settle(s.Vm.Compare);
		CompareViewModel c = s.Vm.Compare;

		Assert.Equal(["No access on Production. Only the other side shows."], c.Notes);
		Assert.True(c.HasNotes);
		Assert.Equal("No differences", c.CountText);

		// Nothing counts, so the list shows only the state now, until everything is asked for.
		c.OnlyDifferences = false;
		CompareRowViewModel version = c.Groups[0].Sections[0].Rows.First(r => r.Label == "Version");
		Assert.Equal(("Waher.IoTGateway 3.11.1.0", "Not read"), (version.Left, version.Right));

		// A Neuron that could not be read is not kept.
		Assert.Empty(await c.Store.ListAsync(Prod));
		Assert.Single(await c.Store.ListAsync(Lab));
	}

	// ----- For review -----

	[AvaloniaTheory]
	[InlineData("Light", 1440)]
	[InlineData("Dark", 1440)]
	[InlineData("Light", 760)]
	[InlineData("Dark", 760)]
	public async Task RendersTheComparison(string theme, int width)
	{
		(Sample s, _) = await StartAsync();
		using Sample _s = s;

		s.Vm.CompareNeurons((s.Ops, Lab), (s.Admin, Prod));
		await Settle(s.Vm.Compare);

		MainWindow window = GettingAroundTests.Window(s, theme, width, 900);
		await Task.Delay(200);
		Dispatcher.UIThread.RunJobs();
		Capture(window, $"compare-{theme.ToLowerInvariant()}-{width}");

		// Nothing in the bar over the comparison is cut off.
		CompareView view = window.GetVisualDescendants().OfType<CompareView>().Single(v => v.IsEffectivelyVisible);
		TextBlock count = view.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == "CountText");
		Control tools = view.GetVisualDescendants().OfType<StackPanel>().Single(x => x.Name == "Tools");
		double countRight = count.TranslatePoint(new Point(count.Bounds.Width, 0), view)!.Value.X;
		double toolsLeft = tools.TranslatePoint(new Point(0, 0), view)!.Value.X;
		Assert.True(countRight <= toolsLeft, $"The count runs under the buttons at {width}: {countRight} > {toolsLeft}.");

		foreach (string name in new[] { "CountText", "LeftName", "RightName" })
		{
			TextBlock t = view.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == name);
			t.Measure(Size.Infinity);
			Assert.True(t.Bounds.Width + 0.5 >= t.DesiredSize.Width, $"{name} is cut off at {width}: {t.Bounds.Width} < {t.DesiredSize.Width}.");
		}

		string shown = string.Join("\n", view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
		Assert.Contains("9 differences in 5 groups", shown, StringComparison.Ordinal);
		Assert.Contains("Only here", shown, StringComparison.Ordinal);
		Assert.DoesNotContain("·", shown, StringComparison.Ordinal);
		window.Close();
	}

	private static void Capture(TopLevel window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		using WriteableBitmap? frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);

		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;
		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		using FileStream file = File.Create(Path.Combine(folder, name + ".png"));
		frame!.Save(file, new PngBitmapEncoderOptions());
	}
}
