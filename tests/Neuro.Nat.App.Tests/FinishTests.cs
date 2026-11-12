using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.Controls;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using static Neuro.Nat.App.Tests.GettingAroundTests;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The finish: the plan's words on every screen, a filter on every list, Settings that start
/// with a few plain choices, motion that can be turned off, and grey bars where rows are on
/// their way. Renders every screen at three sizes in both looks for review.
/// </summary>
public class FinishTests
{
	private const string Gbg = "gbg.lab.tagroot.io";

	private static void Step() => Dispatcher.UIThread.RunJobs();

	private static IEnumerable<string> Words(Visual root)
		=> root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text!.Trim());

	/// <summary>Every screen, by a short name, and how to get to it.</summary>
	private static IEnumerable<(string Name, Action<Sample> Go)> Screens()
	{
		yield return ("inbox", s => s.Vm.ShowAttentionCommand.Execute(null));
		yield return ("neurons", s => s.Vm.ShowFleetCommand.Execute(null));
		yield return ("map", s => s.Vm.ShowTopologyCommand.Execute(null));
		yield return ("events", s => s.Vm.Workspace = WorkspaceKind.Events);
		yield return ("chat", s =>
		{
			s.Vm.Workspace = WorkspaceKind.Chat;
			s.Vm.Chat.Open(s.Ops, "kim@lab.tagroot.io");
		});
		yield return ("console", s => s.Vm.Workspace = WorkspaceKind.Console);
		yield return ("accounts", s => s.Vm.Workspace = WorkspaceKind.Accounts);
		yield return ("settings", s => s.Vm.ShowSettingsCommand.Execute(null));
		yield return ("neuron-overview", s => s.Vm.OpenNeuron(Gbg, s.Ops));
		yield return ("neuron-events", s => s.Vm.ShowPageTabCommand.Execute(WorkspaceKind.NeuronEvents));
		yield return ("neuron-console", s => s.Vm.ShowPageTabCommand.Execute(WorkspaceKind.NeuronConsole));
		yield return ("neuron-sources", s => s.Vm.ShowPageTabCommand.Execute(WorkspaceKind.Sources));
		yield return ("neuron-updates", s => s.Vm.ShowPageTabCommand.Execute(WorkspaceKind.Updates));
		yield return ("neuron-identities", s => s.Vm.ShowPageTabCommand.Execute(WorkspaceKind.Identities));
		yield return ("problem", s =>
		{
			s.Vm.Workspace = WorkspaceKind.Events;
			s.Vm.Events.SelectedIssue = s.Vm.Events.Issues[0];
		});
		yield return ("account-devices", s =>
		{
			s.Vm.Events.SelectedIssue = null;
			s.Vm.Workspace = WorkspaceKind.Accounts;
			s.Vm.SelectedAccount = s.Vm.Accounts[0];
			s.Vm.Workspace = WorkspaceKind.Devices;
		});
		yield return ("account-identities", s => s.Vm.Workspace = WorkspaceKind.AccountIdentities);
		yield return ("account-storage", s => s.Vm.Workspace = WorkspaceKind.AccountStorage);
	}

	// ----- Every screen, three sizes, both looks -----

	[AvaloniaTheory]
	[InlineData("Light", 760, 600)]
	[InlineData("Dark", 760, 600)]
	[InlineData("Light", 1440, 900)]
	[InlineData("Dark", 1440, 900)]
	[InlineData("Light", 2400, 1300)]
	[InlineData("Dark", 2400, 1300)]
	public async Task EveryScreenUsesThePlansWordsAtEverySize(string theme, int width, int height)
	{
		using Sample s = await SampleAsync();
		MainWindow window = Window(s, theme, width, height);
		string t = theme.ToLowerInvariant();

		// Words the plan replaced. None of them may be left where a person reads them.
		string[] old = ["Issues", "Open issues", "Resolved", "Resolve", "Needs you", "Live", "Not admin", "Fleet", "Attention", "Topology"];

		foreach ((string name, Action<Sample> go) in Screens())
		{
			go(s);
			Step();
			await Task.Delay(30);
			Step();
			Capture(window, $"s9-{name}-{t}-{width}");

			List<string> words = [.. Words(window)];
			Assert.NotEmpty(words);
			foreach (string word in old)
				Assert.False(words.Contains(word), $"\"{word}\" is still on {name}");
		}

		window.Close();
	}

	[AvaloniaTheory]
	[InlineData(760)]
	[InlineData(880)]
	[InlineData(980)]
	[InlineData(1120)]
	[InlineData(1300)]
	[InlineData(1500)]
	public async Task TheBarOverTheEventsGivesWayInsteadOfBeingCutOff(int width)
	{
		using Sample s = await SampleAsync();
		s.Vm.Workspace = WorkspaceKind.Events;
		MainWindow window = Window(s, "Light", width, 700);
		Step();

		EventsView events = window.GetVisualDescendants().OfType<EventsView>().First(v => v.IsEffectivelyVisible);
		Border list = events.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("issuesPane"));
		double left = list.TranslatePoint(new Point(list.Bounds.Width, 0), window)!.Value.X;

		// Everything in the bar that shows is wholly inside the window, to the right of the list of problems.
		TextBox filter = events.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("streamFilter") && t.IsEffectivelyVisible);
		Visual bar = filter.GetVisualParent()!;
		foreach (Control c in bar.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is Button or TextBox or ToggleButton))
		{
			Point at = c.TranslatePoint(default, window)!.Value;
			Assert.True(at.X >= left - 0.5, $"{c.GetType().Name} starts at {at.X:F0}, under the list that ends at {left:F0}");
			Assert.True(at.X + c.Bounds.Width <= width + 0.5, $"{c.GetType().Name} ends at {at.X + c.Bounds.Width:F0} in a window {width} wide");
		}

		// The title is there whole, or not at all.
		TextBlock? title = events.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("streamTitle") && t.IsEffectivelyVisible);
		if (title is not null)
		{
			Assert.Equal("All events", title.Text);
			Assert.False(title.TextLayout.TextLines.Any(l => l.HasCollapsed), "the title is cut off");
			Assert.True(title.TranslatePoint(new Point(title.Bounds.Width, 0), window)!.Value.X <= filter.GetVisualParent()!.TranslatePoint(default, window)!.Value.X + 0.5);
		}

		window.Close();
	}

	// ----- A filter on every list -----

	[AvaloniaFact]
	public async Task TheConsoleFiltersItsNeuronsButNeverHidesATickedOne()
	{
		using Sample s = await SampleAsync();
		s.Vm.Workspace = WorkspaceKind.Console;
		MainWindow window = Window(s, "Light");
		ConsoleViewModel console = s.Vm.Console;
		Assert.True(console.Targets.Count >= 4);
		Assert.All(console.Targets, x => Assert.True(x.IsShown));

		TextBox box = Shown<TextBox>(window, "TargetFilterBox");
		Assert.Contains("find", box.Classes);

		ConsoleTargetViewModel lab = console.Targets.First(x => x.BareJid == "lab.tagroot.io");
		lab.IsChecked = true;
		console.TargetFilter = "prod";
		Step();
		Assert.All(console.Targets.Where(x => x.IsShown), x => Assert.True(x.IsChecked || x.BareJid.Contains("prod", StringComparison.OrdinalIgnoreCase) || x.Label.Contains("prod", StringComparison.OrdinalIgnoreCase)));
		Assert.Contains(console.Targets, x => !x.IsShown);
		Assert.True(lab.IsShown);
		Capture(window, "s9-console-filter-light-1440");

		console.TargetFilter = string.Empty;
		Step();
		Assert.All(console.Targets, x => Assert.True(x.IsShown));
		window.Close();
	}

	// ----- Settings start with a few plain choices -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task SettingsStartWithPlainChoicesAndKeepTheSwitchesBehindOne(string theme)
	{
		using Sample s = await SampleAsync();
		SettingsViewModel settings = s.Vm.Settings;
		s.Vm.ShowSettingsCommand.Execute(null);
		MainWindow window = Window(s, theme);
		string t = theme.ToLowerInvariant();

		// As the app comes: what needs someone, and no table of switches.
		Assert.True(settings.IsAlertsNeedsMe);
		Assert.False(settings.IsAlertsOwn);
		Grid kinds = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "AlertKinds");
		Assert.False(kinds.IsVisible);
		Capture(window, $"s9-settings-plain-{t}-1440");

		settings.SetAlertsCommand.Execute("everything");
		Step();
		Assert.True(settings.IsAlertsEverything);
		Assert.True(settings.PopInformation && settings.SoundInformation && settings.PopMessages && settings.SoundErrors);
		Assert.False(kinds.IsVisible);

		settings.SetAlertsCommand.Execute("nothing");
		Step();
		Assert.True(settings.IsAlertsNothing);
		Assert.False(settings.PopMessages || settings.PopErrors || settings.PopNeuronDown || settings.SoundMessages || settings.SoundErrors || settings.PopInformation);

		// "My own choice" shows the switches, as they are.
		settings.SetAlertsCommand.Execute("own");
		Step();
		Assert.True(settings.IsAlertsOwn);
		Assert.True(kinds.IsVisible);
		settings.PopErrors = true;
		Step();
		Assert.True(settings.IsAlertsOwn);
		Assert.True(kinds.IsVisible);
		Capture(window, $"s9-settings-own-{t}-1440");

		// A mix that is none of the plain choices stays "My own choice" when Settings opens again.
		settings.SetAlertsCommand.Execute("needsMe");
		Step();
		Assert.True(settings.IsAlertsNeedsMe);
		Assert.False(kinds.IsVisible);
		settings.SoundMessages = false;
		Step();
		Assert.True(settings.IsAlertsOwn);
		Assert.True(kinds.IsVisible);

		settings.SetAlertsCommand.Execute("needsMe");
		window.Close();
	}

	// ----- Motion -----

	[AvaloniaFact]
	public async Task MotionIsOffUntilTheAppTurnsItOnAndGoesOffAgain()
	{
		Assert.False(Motion.Enabled);
		int before = Application.Current!.Styles.Count;

		using Sample s = await SampleAsync();
		MainWindow window = Window(s, "Light");
		try
		{
			Motion.Set(true);
			Assert.Equal(before + 1, Application.Current.Styles.Count);

			// Screens change, a note shows, rows come: nothing breaks while things move.
			foreach ((string _, Action<Sample> go) in Screens().Take(8))
			{
				go(s);
				Step();
			}

			Ui.Say("Copied");
			Step();
			await Task.Delay(250);
			Step();
			Assert.True(s.Vm.IsSettings);
		}
		finally
		{
			Motion.Set(false);
		}

		Assert.Equal(before, Application.Current.Styles.Count);
		window.Close();
	}

	[AvaloniaFact]
	public void LessMotionIsASettingThatIsKept()
	{
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Finish." + Guid.NewGuid().ToString("N"));
		JsonUiSettingsStore store = new(Path.Combine(dir, "ui.json"));
		Assert.False(store.Load().LessMotion);
		store.Save(store.Load() with { LessMotion = true });
		Assert.True(store.Load().LessMotion);
		Directory.Delete(dir, true);
	}

	// ----- Rows on their way -----

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public void GreyBarsStandWhereRowsWillBe(string theme)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
		Placeholder bars = new() { Rows = 4, RowHeight = 34, Margin = new Thickness(20) };
		Window window = new() { Width = 420, Height = 200, Content = new StackPanel { Children = { bars } } };
		window.Show();
		Step();

		Assert.Equal(4 * 34, bars.Bounds.Height);
		Assert.True(bars.Bounds.Width > 200);
		Assert.NotNull(bars.Bar);
		Capture(window, $"s9-placeholder-{theme.ToLowerInvariant()}");
		window.Close();
	}
}
