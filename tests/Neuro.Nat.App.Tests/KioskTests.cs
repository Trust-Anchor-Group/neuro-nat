using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Desktop.Kiosk;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// A kiosk: the big screen and nothing else, which the keyboard cannot leave; and presence
/// subscriptions answered without a person: listed Neurons in, anything else declined.
/// </summary>
public class KioskTests
{
	[AvaloniaFact]
	public async Task TheBigScreenIsAllThereIsAndEscAndF11DoNotLeaveIt()
	{
		using GettingAroundTests.Sample s = await GettingAroundTests.SampleAsync();
		MainWindow main = new() { DataContext = s.Vm };
		ShellWindows.Attach(main, s.Vm, () => new MainWindowViewModel(s.Manager, s.Fleet, s.Chat, null, null, s.PubSub, s.Software, primary: s.Vm));
		try
		{
			Window window = Assert.IsAssignableFrom<Window>(ShellWindows.OpenKiosk());
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(WindowState.FullScreen, window.WindowState);
			Assert.False(main.IsVisible);

			window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
			window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
			s.Vm.OpenBigScreenCommand.Execute(null);
			Dispatcher.UIThread.RunJobs();
			Assert.Same(window, ShellWindows.BigScreen);
			Assert.True(window.IsVisible);

			// Turning pages still works, and no hint says how to leave.
			window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
			Assert.False(s.Vm.BigScreen.IsNeuronsPage);
			Assert.False(s.Vm.BigScreen.ShowsLeaveHint);

			// A lost connection keeps the last state on the screen, marked as old, until it is back.
			BigScreenViewModel big = s.Vm.BigScreen;
			big.Refresh();
			string[] before = [.. big.Tiles.Select(x => x.StateText)];
			Assert.NotEmpty(before);
			big.AnyConnected = () => false;
			big.Refresh();
			Assert.StartsWith("Not connected since ", big.StaleText);
			Assert.Equal(before, big.Tiles.Select(x => x.StateText));
			big.AnyConnected = () => true;
			big.Refresh();
			Assert.Null(big.StaleText);
		}
		finally
		{
			ShellWindows.IsKiosk = false;
			ShellWindows.BigScreen?.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	[AvaloniaFact]
	public async Task ANeuronThatSubscribesWhileTheKioskRunsShowsAtOnce()
	{
		using GettingAroundTests.Sample s = await GettingAroundTests.SampleAsync();
		KioskConfig config = new([new KioskAccount("lab.local", 5222, "kiosk", "x", null, null)], ["new.example.com", "other.example.com"], null, 20, LookChoice.Dark);
		s.Monitors.SubscriptionRule = KioskSetup.Rule(config);
		s.Monitors.DeclineOthers = true;
		NeuronMonitor monitor = s.Monitors.For(s.Ops)!;
		List<string> asked = [];
		monitor.Discover = jid =>
		{
			asked.Add(jid);
			return Task.FromResult<bool?>(null);   // It does not answer what it is.
		};

		s.Vm.BigScreen.Refresh();
		int before = s.Vm.BigScreen.Tiles.Count;

		// Added to the Neuron's notify list: it asks to subscribe, and is listed in the config.
		List<string> said = [];
		NeuronMonitor.SubscriptionAnswer answer = await monitor.SubscribeAsync("new.example.com/server", "new.example.com",
			() => { said.Add("accept"); return Task.CompletedTask; },
			() => { said.Add("decline"); return Task.CompletedTask; },
			() => said.Add("ask"));

		Assert.Equal(NeuronMonitor.SubscriptionAnswer.Accept, answer);
		Assert.Equal(["accept"], said);
		Assert.Empty(asked);   // Accepted by the list, without asking what it is.

		// Its tile is on the big screen without a restart.
		s.Vm.Fleet.Refresh();
		s.Vm.BigScreen.Refresh();
		Assert.Equal(before + 1, s.Vm.BigScreen.Tiles.Count);
		Assert.Contains(s.Vm.BigScreen.Tiles, t => t.BareJid == "new.example.com");
	}

	[AvaloniaFact]
	public async Task AnythingNotListedIsDeclinedQuietly()
	{
		using GettingAroundTests.Sample s = await GettingAroundTests.SampleAsync();
		NeuronMonitor monitor = s.Monitors.For(s.Ops)!;
		monitor.Discover = _ => Task.FromResult<bool?>(true);

		async Task<NeuronMonitor.SubscriptionAnswer> Ask(string bare)
			=> await monitor.SubscribeAsync(bare + "/x", bare, () => Task.CompletedTask, () => Task.CompletedTask, () => { });

		// A kiosk that lists its Neurons: even a real Neuron not on the list is declined.
		KioskConfig listed = new([new KioskAccount("lab.local", 5222, "kiosk", "x", null, null)], ["lab.example.com"], null, 20, LookChoice.Dark);
		monitor.SubscriptionRule = KioskSetup.Rule(listed);
		monitor.DeclineOthers = true;
		Assert.Equal(NeuronMonitor.SubscriptionAnswer.Accept, await Ask("lab.example.com"));
		Assert.Equal(NeuronMonitor.SubscriptionAnswer.Decline, await Ask("other.example.com"));
		Assert.Equal(NeuronMonitor.SubscriptionAnswer.Decline, await Ask("kim@lab.example.org"));

		// A kiosk that lists none: real Neurons are accepted, people are declined, never left waiting.
		KioskConfig none = listed with { Neurons = [] };
		monitor.SubscriptionRule = KioskSetup.Rule(none);
		Assert.Equal(NeuronMonitor.SubscriptionAnswer.Accept, await Ask("any.example.com"));
		monitor.Discover = _ => Task.FromResult<bool?>(false);
		Assert.Equal(NeuronMonitor.SubscriptionAnswer.Decline, await Ask("kim@lab.example.org"));

		// A normal start leaves a person for the user to answer.
		monitor.SubscriptionRule = null;
		monitor.DeclineOthers = false;
		Assert.Equal(NeuronMonitor.SubscriptionAnswer.Ask, await Ask("sam@lab.example.org"));
	}
	[AvaloniaFact]
	public async Task ANeuronItMayNotReadShowsItsStateWithoutErrorText()
	{
		using GettingAroundTests.Sample s = await GettingAroundTests.SampleAsync();
		s.Monitors.For(s.Ops)!.Preview(new WatchedNeuron { AccountId = s.Ops, BareJid = "closed.example.com", State = WatchState.Forbidden });
		s.Vm.Fleet.Refresh();
		BigScreenViewModel big = s.Vm.BigScreen;

		big.Refresh();
		Assert.Equal("No access", big.Tiles.Single(x => x.BareJid == "closed.example.com").StateText);

		big.IsKiosk = true;
		big.Refresh();
		BigScreenTileViewModel tile = big.Tiles.Single(x => x.BareJid == "closed.example.com");
		Assert.Equal("Online", tile.StateText);
		Assert.Equal("ok", tile.Tone);
	}
}
