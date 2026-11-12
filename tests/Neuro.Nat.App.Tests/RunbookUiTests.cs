using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Runbooks;
using Neuro.Nat.Engine.Storage;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// Runbooks in the Console, on stand-in Neurons: made from the transcript, a dry run that sends
/// nothing, blanks that are remembered, a marked step that asks mid-run and names the Neurons, the
/// record of the run, and a picture of the list and a run in progress.
/// </summary>
public class RunbookUiTests
{
	/// <summary>Stand-in Neurons: they note what they are sent; a command can be held until the test lets it go.</summary>
	private sealed class StandIns : IRunbookSender
	{
		public ConcurrentQueue<(string Jid, string Command)> Sent { get; } = new();
		public HashSet<string> Offline { get; } = new(StringComparer.OrdinalIgnoreCase);
		public Func<string, string, string> Answer { get; set; } = (_, c) => "`" + c + "`";

		public bool IsOnline(RunbookTarget target) => !this.Offline.Contains(target.Jid);

		public Task<RunbookAnswer> SendAsync(RunbookTarget target, string command, CancellationToken cancel)
		{
			this.Sent.Enqueue((target.Jid, command));
			return Task.FromResult(new RunbookAnswer(this.Answer(target.Jid, command)));
		}
	}

	private static readonly string[] Neurons = ["neuron@alpha.lab.local", "neuron@beta.lab.local", "neuron@gamma.lab.local"];

	private static async Task<(MainWindowViewModel Vm, ConsoleViewModel Console, StandIns Fake, InMemoryRunbookStore Store)> Console(bool offlineGamma = false)
	{
		EngineRuntime.Initialize();
		string file = Path.Combine(Path.GetTempPath(), "Nat.Runbooks." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(file), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		MainWindowViewModel vm = new(manager, new FleetService(new MonitorService(manager, new EventHub())));
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Console;
		Dispatcher.UIThread.RunJobs();

		ConsoleViewModel console = vm.Console;
		foreach (string n in Neurons)
			console.Targets.Add(new ConsoleTargetViewModel(ops.Profile.Id, n) { Label = NeuronNames.Domain(n), AccountLabel = "ops", IsOnline = true, IsChecked = n != "neuron@gamma.lab.local" || offlineGamma });

		StandIns fake = new();
		if (offlineGamma)
			fake.Offline.Add("neuron@gamma.lab.local");
		console.Runbooks.Sender = fake;
		console.Runbooks.Delay = (_, _) => Task.CompletedTask;
		InMemoryRunbookStore store = new();
		await console.Runbooks.UseAsync(store);
		return (vm, console, fake, store);
	}

	private static ConsoleRunViewModel Ran(ConsoleViewModel console, string command)
	{
		ConsoleRunViewModel run = new(command, DateTime.Now);
		console.Runs.Add(run);
		console.HasRuns = true;
		return run;
	}

	[AvaloniaFact]
	public async Task MadeFromTheTranscriptThenDryRunRunAndKept()
	{
		(_, ConsoleViewModel console, StandIns fake, InMemoryRunbookStore store) = await Console();
		RunbooksViewModel runbooks = console.Runbooks;
		Assert.Equal(ReadyMadeRunbooks.All.Count, runbooks.Items.Count);

		// "Save as runbook" ticks every command; one is unticked.
		Ran(console, "Versions");
		ConsoleRunViewModel noise = Ran(console, "6*7");
		Ran(console, "echo {account}");
		Ran(console, "Reboot");
		console.StartPickingCommand.Execute(null);
		Assert.True(console.IsPicking);
		Assert.Equal("4 commands ticked", console.PickSummary);
		noise.IsPicked = false;
		Assert.Equal("3 commands ticked", console.PickSummary);
		console.PickTitle = "Reset and restart";
		await console.SavePickedCommand.ExecuteAsync(null);

		Assert.False(console.IsPicking);
		Runbook saved = Assert.Single(await store.LoadAsync());
		Assert.Equal("Reset and restart", saved.Title);
		Assert.Equal(["Versions", "echo {account}", "Reboot"], saved.Steps.Select(s => s.Command));
		Assert.Equal([false, false, true], saved.Steps.Select(s => s.Marked));
		Assert.True(runbooks.IsOpen);
		Assert.Equal("Reset and restart", runbooks.Title);
		Assert.Equal("3 steps, 1 asks first", runbooks.StepSummary);
		Assert.Equal("{account}", Assert.Single(runbooks.Blanks).Label);

		// Not before the blank is filled. A dry run then shows what goes where and sends nothing.
		Assert.False(runbooks.DryRunCommand.CanExecute(null));
		runbooks.Blanks[0].Value = "kim";
		await runbooks.DryRunCommand.ExecuteAsync(null);
		Assert.Empty(fake.Sent);
		Assert.False(runbooks.IsAsking);
		Assert.True(runbooks.Run!.IsDry);
		Assert.Equal(["echo kim", "echo kim"], runbooks.Run.Steps[1].Cells.Select(c => c.Command));
		Assert.Equal(["alpha.lab.local", "beta.lab.local"], runbooks.Run.Steps[1].Cells.Select(c => c.NeuronLabel));

		// The blank is remembered for this runbook.
		Assert.Equal("kim", (await store.LoadAsync())[0].Remembered["account"]);
		runbooks.CloseCommand.Execute(null);
		Assert.False(runbooks.IsOpen);
		runbooks.Selected = runbooks.Items.First(i => i.Title == "Reset and restart");
		Assert.Equal("kim", runbooks.Blanks[0].Value);

		// The run asks before Reboot, in the middle, and names both Neurons.
		Task running = runbooks.RunRunbookCommand.ExecuteAsync(null);
		Dispatcher.UIThread.RunJobs();
		Assert.True(runbooks.IsAsking);
		Assert.Equal("Step 3: Reboot goes to 2 Neurons: alpha.lab.local, beta.lab.local. Type 2 to go ahead.", runbooks.AskPrompt);
		Assert.Equal(["Versions", "echo kim"], fake.Sent.Where(s => s.Jid == "neuron@alpha.lab.local").Select(s => s.Command));
		runbooks.AskText = "alpha";
		runbooks.ConfirmCommand.Execute(null);
		Assert.True(runbooks.IsAsking, "Only the count goes ahead.");
		runbooks.AskText = "2";
		runbooks.ConfirmCommand.Execute(null);
		await running;

		Assert.False(runbooks.IsAsking);
		Assert.Contains(("neuron@beta.lab.local", "Reboot"), fake.Sent);
		Assert.Equal(["Done", "Done"], runbooks.Run!.Outcomes.Select(o => o.Text));

		// Run again: it asks again.
		running = runbooks.RunRunbookCommand.ExecuteAsync(null);
		Dispatcher.UIThread.RunJobs();
		Assert.True(runbooks.IsAsking);
		runbooks.DeclineCommand.Execute(null);
		await running;
		Assert.Equal(["Stopped at step 3: Not confirmed", "Stopped at step 3: Not confirmed"], runbooks.Run!.Outcomes.Select(o => o.Text));

		// Each run left a record, latest first.
		IReadOnlyList<RunbookRecord> records = await store.RecordsAsync(saved.Id);
		Assert.Equal(2, records.Count);
		Assert.Equal(2, runbooks.Records.Count);
		Assert.Equal("2 Neurons, stopped", runbooks.Records[0].Summary);
		Assert.Equal("2 Neurons, done", runbooks.Records[1].Summary);
		Assert.Equal(["ops"], records[1].Accounts);
	}

	[AvaloniaFact]
	public async Task AFileOpensWithItsStepsAndSendsNothingUntilRun()
	{
		(_, ConsoleViewModel console, StandIns fake, InMemoryRunbookStore store) = await Console();
		RunbooksViewModel runbooks = console.Runbooks;
		string text = RunbookFile.Write(new Runbook { Title = "From Kim", Steps = RunbookFile.ReadSteps("Versions\n! NrClients\nwait 30 seconds\nReboot") });

		Assert.True(runbooks.OpenText(text, "From Kim.txt"));
		Assert.False(runbooks.IsKept);
		Assert.Equal(["Versions", "NrClients", "Wait 30 seconds", "Reboot"], runbooks.Steps.Select(s => s.Text));
		Assert.Equal([false, true, false, true], runbooks.Steps.Select(s => s.IsMarked));
		Assert.Empty(fake.Sent);
		Assert.Empty(await store.LoadAsync());

		await runbooks.KeepCommand.ExecuteAsync(null);
		Assert.True(runbooks.IsKept);
		Assert.Equal("From Kim", Assert.Single(await store.LoadAsync()).Title);
		Assert.Empty(fake.Sent);

		Assert.False(runbooks.OpenText("Versions\nReboot"));
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task RendersTheListAndARunInProgress(string theme)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		(MainWindowViewModel vm, ConsoleViewModel console, StandIns fake, InMemoryRunbookStore store) = await Console(offlineGamma: true);
		RunbooksViewModel runbooks = console.Runbooks;

		// Synthetic sample data: answers as the Neuron's chat admin writes them.
		fake.Answer = (jid, c) => c switch
		{
			"Versions" => "| Assembly | Version |\n|:---|:---|\n| `Waher.IoTGateway` | `3.11.1.0` |\n| `Waher.Service.IoTBroker` | `" + (jid.Contains("alpha", StringComparison.Ordinal) ? "3.11.1.0" : "3.10.4.0") + "` |",
			"NrClients" => jid.Contains("alpha", StringComparison.Ordinal) ? "1284" : "977",
			_ => "`" + c + "`",
		};
		await store.SaveAsync(new Runbook { Title = "Restart and check", Steps = RunbookFile.ReadSteps("Versions\nNrClients\nReboot\nwait until back\nNrClients") }.WithMarks());
		await store.SaveAsync(new Runbook { Title = "Reset a password", Steps = RunbookFile.ReadSteps("reset password {account}") }.WithMarks());
		await runbooks.UseAsync(store);

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();

		// The runbook open: its steps, before it runs.
		runbooks.Selected = runbooks.Items.First(i => i.Title == "Restart and check");
		Dispatcher.UIThread.RunJobs();
		Save(window, $"console-runbook-steps-{theme.ToLowerInvariant()}-1440");

		Task running = runbooks.RunRunbookCommand.ExecuteAsync(null);
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(300);
		Dispatcher.UIThread.RunJobs();

		Assert.True(runbooks.IsRunning);
		Assert.True(runbooks.IsAsking);
		Assert.Equal("Not online", runbooks.Run!.Steps[0].Cells[2].Status);

		Save(window, $"console-runbook-{theme.ToLowerInvariant()}-1440");


		runbooks.StopCommand.Execute(null);
		await running;
		Assert.Equal(["Stopped at step 3: Stopped by you", "Stopped at step 3: Stopped by you", "Stopped at step 1: Not online"], runbooks.Run!.Outcomes.Select(o => o.Text));
		window.Close();
	}

	private static void Save(MainWindow window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		using FileStream png = File.Create(Path.Combine(ReviewFolder(), name + ".png"));
		frame!.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
	}

	private static string ReviewFolder()
	{
		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;

		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		return folder;
	}
}
