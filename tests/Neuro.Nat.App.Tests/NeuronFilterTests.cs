using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.Controls;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Alerts;
using Neuro.Nat.Engine.Events;
using Sample = Neuro.Nat.App.Tests.GettingAroundTests.Sample;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The Neuron filter in the top bar: one button and a list to tick Neurons in, instead of a chip
/// per Neuron. The ticked Neurons are a set that Events, the Inbox and the Neurons list follow.
/// </summary>
public class NeuronFilterTests(ITestOutputHelper output)
{
	private const string Lab = "lab.tagroot.io";
	private const string Gbg = "gbg.lab.tagroot.io";
	private const string Sto = "sto.lab.tagroot.io";
	private const string Prod = "prod.example.com";
	private const string Backup = "backup.prod.example.com";

	private static void Flush(MainWindowViewModel vm)
	{
		vm.Events.SeverityFilter = SeverityFilter.All;
		vm.Events.Flush();
		Dispatcher.UIThread.RunJobs();
	}

	[AvaloniaFact]
	public async Task NoneOneSeveralAndAllFilterEventsInboxAndNeurons()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;
		Flush(vm);
		int events = vm.Events.Stream.Count;
		int inbox = vm.Inbox.Rows.Count;

		// None ticked: everything.
		Assert.True(vm.IsAllNeurons);
		Assert.Empty(vm.LensJids);
		Assert.Equal(5, vm.Fleet.Shown.Count);
		Assert.Equal("All Neurons", vm.Picker.Text);
		Assert.False(vm.Picker.IsFiltered);

		// One: its name on the button, and only it everywhere.
		vm.SetLens([Gbg]);
		Flush(vm);
		Assert.Equal(Gbg, vm.LensJid);
		Assert.Equal("Gothenburg edge", vm.Picker.Text);
		Assert.True(vm.Picker.IsFiltered);
		Assert.Equal([Gbg], vm.Fleet.Shown.Select(r => r.BareJid));
		Assert.NotEmpty(vm.Events.Stream);
		Assert.All(vm.Events.Stream, e => Assert.Equal(Gbg, e.Source.NeuronJid));
		Assert.All(vm.Inbox.Rows, r => Assert.EndsWith(Gbg, r.Jid));
		Assert.True(vm.Inbox.Rows.Count < inbox);

		// Several: "2 of 5 Neurons", and what is from either.
		vm.SetLens([Gbg, Prod]);
		Flush(vm);
		Assert.Null(vm.LensJid);
		Assert.Equal("2 of 5 Neurons", vm.Picker.Text);
		Assert.Equal([Gbg, Prod], vm.Fleet.Shown.Select(r => r.BareJid).Order());
		Assert.Equal([Gbg, Prod], vm.Events.Stream.Select(e => e.Source.NeuronJid).Distinct().Order());
		Assert.True(vm.Events.Stream.Count < events);
		Assert.Contains(vm.Inbox.Rows, r => r.Jid == "robin@gbg.lab.tagroot.io");
		Assert.Contains(vm.Inbox.Rows, r => r.Jid == "lead@prod.example.com");
		Assert.DoesNotContain(vm.Inbox.Rows, r => r.Jid == "kim@lab.tagroot.io");
		Assert.Equal(2, vm.NeuronChips.Count(c => c.IsSelected));

		// All of them ticked is no filter at all, so nothing from outside the Neurons is lost either.
		vm.SetLens([Lab, Gbg, Sto, Prod, Backup]);
		Flush(vm);
		Assert.True(vm.IsAllNeurons);
		Assert.Equal("All Neurons", vm.Picker.Text);
		Assert.Equal("5 of 5 ticked", vm.Picker.CountText);
		Assert.Equal(5, vm.Fleet.Shown.Count);
		Assert.Equal(events, vm.Events.Stream.Count);
		Assert.Equal(inbox, vm.Inbox.Rows.Count);

		// A Neuron that is not there is not a filter until it is.
		vm.SetLens(["gone.example.com"]);
		Flush(vm);
		Assert.True(vm.IsAllNeurons);
		Assert.Equal(5, vm.Fleet.Shown.Count);
		Assert.Equal(["gone.example.com"], vm.LensJids);

		// The empty Inbox under several says so.
		vm.SetLens([Sto, Backup]);
		Assert.Empty(vm.Inbox.Rows);
		Assert.Equal("Nothing from 2 of 5 Neurons here", vm.Inbox.EmptyTitle);
		Assert.StartsWith("The list shows 2 Neurons only. ", vm.Inbox.EmptyText);
	}

	[AvaloniaFact]
	public async Task TheMapAndTheConsoleFollowOnlyExactlyOne()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;

		vm.ShowConsoleCommand.Execute(null);
		List<string> before = [.. vm.Console.Targets.Where(t => t.IsChecked).Select(t => t.BareJid)];
		vm.SetLens([Gbg, Sto]);
		Assert.Equal(before, vm.Console.Targets.Where(t => t.IsChecked).Select(t => t.BareJid));

		vm.SetLens([Sto]);
		Assert.Equal([Sto], vm.Console.Targets.Where(t => t.IsChecked).Select(t => t.BareJid));
	}

	[AvaloniaFact]
	public async Task SearchNarrowsTheListAndAllAndNoneWorkOnWhatShows()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		NeuronPickerViewModel picker = s.Vm.Picker;

		picker.IsOpen = true;
		Assert.Equal(5, picker.Shown.Count);
		Assert.Equal("5 Neurons", picker.CountText);
		Assert.Same(picker.Shown[0], picker.Highlighted);

		// By name, by address, and by several words.
		picker.Query = "edge";
		Assert.Equal([Gbg, Sto], picker.Shown.Select(n => n.BareJid));
		picker.Query = "prod.example";
		Assert.Equal([Backup, Prod], picker.Shown.Select(n => n.BareJid).Order());
		picker.Query = "stockholm edge";
		Assert.Equal([Sto], picker.Shown.Select(n => n.BareJid));
		picker.Query = "nothing like it";
		Assert.True(picker.IsEmpty);

		// All ticks what shows; None takes those ticks away and keeps the others.
		picker.Query = string.Empty;
		picker.ToggleCommand.Execute(picker.Shown.Single(n => n.BareJid == Lab));
		picker.Query = "edge";
		picker.AllCommand.Execute(null);
		Assert.Equal([Gbg, Lab, Sto], s.Vm.LensJids.Order());
		Assert.Equal("3 of 5 Neurons", picker.Text);
		picker.NoneCommand.Execute(null);
		Assert.Equal([Lab], s.Vm.LensJids);

		// The highlighted one ticks with Space or Enter; closing forgets what was typed.
		picker.Query = "goth";
		picker.ToggleCommand.Execute(null);
		Assert.Equal([Gbg, Lab], s.Vm.LensJids.Order());
		picker.CloseCommand.Execute(null);
		Assert.Equal(string.Empty, picker.Query);

		// None with nothing typed shows every Neuron.
		picker.IsOpen = true;
		picker.NoneCommand.Execute(null);
		Assert.True(s.Vm.IsAllNeurons);
		Assert.Equal("All Neurons", picker.Text);

		// All with nothing typed is no filter at all, so a Neuron that turns up later shows too.
		picker.ToggleCommand.Execute(picker.Shown.Single(n => n.BareJid == Lab));
		picker.AllCommand.Execute(null);
		Assert.True(s.Vm.IsAllNeurons);
		Assert.Empty(s.Vm.LensJids);
	}

	[AvaloniaFact]
	public async Task OnlyThisNeuronOnAnEvent()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;
		vm.ShowEventsCommand.Execute(null);
		vm.SetLens([Gbg, Lab]);
		Flush(vm);

		NeuronEvent onLab = vm.Events.Stream.First(e => e.Source.NeuronJid == Lab);
		IReadOnlyList<ItemAction> actions = vm.Events.ActionsFor([onLab]);
		Assert.Contains(actions, a => a.Text == "Go to " + Converters.NeuronNameConverter.Name(Lab));
		await actions.Single(a => a.Text == "Only this Neuron").RunAsync();
		Dispatcher.UIThread.RunJobs();
		Assert.Equal([Lab], vm.LensJids);
		Assert.Equal(Lab, vm.LensJid);
		Assert.True(vm.IsEvents);
		Flush(vm);
		Assert.All(vm.Events.Stream, e => Assert.Equal(Lab, e.Source.NeuronJid));

		// Already only that Neuron: nothing to offer, but Go to stays.
		actions = vm.Events.ActionsFor([vm.Events.Stream[0]]);
		Assert.DoesNotContain(actions, a => a.Text == "Only this Neuron");
		Assert.Contains(actions, a => a.Text == "Go to " + Converters.NeuronNameConverter.Name(Lab));

		// The same on a Neuron in the filter's own list.
		NeuronChipViewModel gbg = vm.NeuronChips.Single(c => c.BareJid == Gbg);
		await vm.ActionsFor([gbg]).Single(a => a.Text == "Only this Neuron").RunAsync();
		Assert.Equal([Gbg], vm.LensJids);
		await vm.ActionsFor([gbg]).Single(a => a.Text == "Show all Neurons").RunAsync();
		Assert.True(vm.IsAllNeurons);
	}

	[AvaloniaFact]
	public async Task AnOldRememberedNeuronComesBackAsASetOfOne()
	{
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Filter." + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		string file = Path.Combine(dir, "ui.json");
		new JsonUiSettingsStore(file).Save(new UiSettings { Neuron = Gbg });

		using Sample s = await GettingAroundTests.SampleAsync(dir);
		using AlertService alerts = new(s.Monitors, new AlertSettings());
		s.Vm.Settings.Attach(alerts, new JsonAlertSettingsStore(Path.Combine(dir, "alerts.json")), new JsonUiSettingsStore(file));
		Dispatcher.UIThread.RunJobs();
		Assert.Equal([Gbg], s.Vm.LensJids);
		Assert.Equal("Gothenburg edge", s.Vm.Picker.Text);
		Assert.Equal([Gbg], s.Vm.Fleet.Shown.Select(r => r.BareJid));

		// Ticking another keeps both, in the new way.
		s.Vm.ToggleNeuronCommand.Execute(s.Vm.NeuronChips.Single(c => c.BareJid == Sto));
		UiSettings saved = new JsonUiSettingsStore(file).Load();
		Assert.Equal([Gbg, Sto], saved.Neurons!);
		Assert.Null(saved.Neuron);

		// A window's address holds the set between commas, and an old address with one Neuron still reads.
		Assert.EndsWith("|" + Gbg + "," + Sto, s.Vm.Address);
		MainWindowViewModel other = new(s.Manager, s.Fleet, s.Chat, null, null, s.PubSub, s.Software, primary: s.Vm);
		await other.StartAsync();
		other.Fleet.Refresh();
		other.GoTo("Events|||" + Sto);
		Assert.Equal([Sto], other.LensJids);
		Assert.Equal(Sto, other.LensJid);
		other.GoTo("Events|||" + NeuronSet.ToText(s.Vm.LensJids));
		Assert.Equal([Gbg, Sto], other.LensJids.Order());

		// The other window's choice is its own: Settings still have the main window's.
		other.SetLens([Prod]);
		Assert.Equal([Gbg, Sto], new JsonUiSettingsStore(file).Load().Neurons!);
	}

	[AvaloniaFact]
	public async Task TheKeyboardOpensSearchesTicksAndCloses()
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;
		vm.ShowEventsCommand.Execute(null);
		MainWindow window = GettingAroundTests.Window(s, "Light");

		Button button = GettingAroundTests.Shown<Button>(window, "PickerButton");
		Assert.Equal("All Neurons", GettingAroundTests.Shown<TextBlock>(window, "PickerText").Text);
		button.Focus();

		// Down opens it, with the keyboard in the search box.
		window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.Picker.IsOpen);
		TextBox search = GettingAroundTests.Shown<TextBox>(window, "PickerSearch");
		Assert.True(search.IsFocused);

		// Typing narrows; Down goes into the list; Space ticks.
		window.KeyTextInput("edge");
		Dispatcher.UIThread.RunJobs();
		Assert.Equal([Gbg, Sto], vm.Picker.Shown.Select(n => n.BareJid));
		window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
		window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(Sto, vm.Picker.Highlighted?.BareJid);
		Assert.False(search.IsFocused);
		window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal([Sto], vm.LensJids);
		Assert.True(vm.Picker.IsOpen);

		// Up past the top goes back to the search box; typing in the list does too.
		window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
		window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.True(search.IsFocused);
		Assert.Equal(Gbg, vm.Picker.Highlighted?.BareJid);

		// Enter in the search box ticks the highlighted one.
		window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal([Gbg, Sto], vm.LensJids.Order());
		Assert.Equal("2 of 5 Neurons", GettingAroundTests.Shown<TextBlock>(window, "PickerText").Text);

		// Escape closes it and gives the keyboard back to the button.
		window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.False(vm.Picker.IsOpen);
		Assert.True(button.IsFocused);

		// Enter on the button opens it again, and a click on a row ticks it.
		window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.Picker.IsOpen);
		ListBox list = GettingAroundTests.Shown<ListBox>(window, "PickerList");
		Control lab = list.ContainerFromItem(vm.NeuronChips.Single(c => c.BareJid == Lab))!;
		Point at = lab.TranslatePoint(new Point(lab.Bounds.Width / 2, lab.Bounds.Height / 2), window)!.Value;
		window.MouseDown(at, MouseButton.Left);
		window.MouseUp(at, MouseButton.Left);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal([Gbg, Lab, Sto], vm.LensJids.Order());

		window.Close();
	}

	[AvaloniaFact]
	public async Task ThreeHundredNeuronsBuildFast()
	{
		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Filter." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		MonitorService monitors = new(manager, new EventHub());
		Guid account = manager.Sessions[0].Profile.Id;

		// Synthetic sample data: 300 Neurons.
		for (int i = 0; i < 300; i++)
		{
			monitors.For(account)!.Preview(new WatchedNeuron
			{
				AccountId = account,
				BareJid = $"n{i:000}.lab.local",
				Name = $"Neuron {i:000}",
				State = i % 17 == 0 ? WatchState.Offline : WatchState.Live,
			});
		}

		Stopwatch sw = Stopwatch.StartNew();
		MainWindowViewModel vm = new(manager, new FleetService(monitors));
		await vm.StartAsync();
		vm.Fleet.Refresh();
		long build = sw.ElapsedMilliseconds;
		Assert.Equal(300, vm.NeuronChips.Count);

		sw.Restart();
		vm.SetLens(vm.NeuronChips.Take(42).Select(c => c.BareJid));
		vm.Fleet.Refresh();
		long tick = sw.ElapsedMilliseconds;
		Assert.Equal("42 of 300 Neurons", vm.Picker.Text);

		vm.Workspace = WorkspaceKind.Events;
		Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();
		Dispatcher.UIThread.RunJobs();

		sw.Restart();
		vm.Picker.IsOpen = true;
		Dispatcher.UIThread.RunJobs();
		window.CaptureRenderedFrame();
		long open = sw.ElapsedMilliseconds;

		sw.Restart();
		vm.Picker.Query = "neuron 2";
		Dispatcher.UIThread.RunJobs();
		window.CaptureRenderedFrame();
		long search = sw.ElapsedMilliseconds;
		Assert.Equal(Enumerable.Range(0, 300).Count(i => i.ToString("000", System.Globalization.CultureInfo.InvariantCulture).Contains('2')), vm.Picker.Shown.Count);

		// The list is virtualised: only the rows in sight are made.
		ListBox list = GettingAroundTests.Shown<ListBox>(window, "PickerList");
		int made = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
		output.WriteLine($"300 Neurons: build {build} ms, tick 42 {tick} ms, open {open} ms, search {search} ms, {made} rows made");
		Assert.InRange(made, 1, 40);
		Assert.True(build < 1500, $"Building 300 took {build} ms");
		Assert.True(tick < 1000, $"Ticking 42 of 300 took {tick} ms");
		Assert.True(open < 1500, $"Opening the list of 300 took {open} ms");
		Assert.True(search < 1000, $"Searching 300 took {search} ms");
		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task RendersTheOpenDropDown(string theme)
	{
		using Sample s = await GettingAroundTests.SampleAsync();
		MainWindowViewModel vm = s.Vm;
		vm.ShowEventsCommand.Execute(null);
		vm.SetLens([Gbg, Lab]);
		MainWindow window = GettingAroundTests.Window(s, theme);
		Assert.Equal("2 of 5 Neurons", GettingAroundTests.Shown<TextBlock>(window, "PickerText").Text);

		vm.Picker.IsOpen = true;
		Dispatcher.UIThread.RunJobs();
		vm.Picker.Highlighted = vm.NeuronChips.Single(c => c.BareJid == Sto);
		Dispatcher.UIThread.RunJobs();

		Border panel = GettingAroundTests.Shown<Border>(window, "DropPanel");
		Assert.True(panel.Bounds.Height > 100);
		Assert.Equal(5, GettingAroundTests.Shown<ListBox>(window, "PickerList").GetVisualDescendants().OfType<ListBoxItem>().Count());

		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;
		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		using (FileStream stream = File.Create(Path.Combine(folder, $"neuron-filter-open-{theme.ToLowerInvariant()}.png")))
			frame!.Save(stream, new PngBitmapEncoderOptions());

		window.Close();
	}
}
