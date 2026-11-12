using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Runbooks;

namespace Neuro.Nat.App.ViewModels;

/// <summary>Running a runbook: the dry run, the run step by step, the questions a marked step asks, and the records.</summary>
public sealed partial class RunbooksViewModel
{
	/// <summary>The run on screen: going now, just finished, a dry run, or an earlier one.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasRun))]
	private RunbookRunViewModel? run;

	public bool HasRun => this.Run is not null;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(DryRunCommand), nameof(RunRunbookCommand), nameof(StopCommand))]
	private bool isRunning;

	public ObservableCollection<RunbookRecordItemViewModel> Records { get; } = [];

	[ObservableProperty] private bool hasRecords;

	/// <summary>The Console calls this when the ticked Neurons change.</summary>
	public void TargetsChanged(string summary)
	{
		this.TargetSummary = summary;
		this.DryRunCommand.NotifyCanExecuteChanged();
		this.RunRunbookCommand.NotifyCanExecuteChanged();
	}

	private Dictionary<string, string> Values()
		=> this.Blanks.ToDictionary(b => b.Name, b => b.Value.Trim(), StringComparer.OrdinalIgnoreCase);

	private bool CanRun() => this.Current is not null && !this.IsRunning && this.targets().Count > 0 && this.Blanks.All(b => b.Value.Trim().Length > 0);

	/// <summary>Shows exactly which command goes to which Neuron. Sends nothing, asks nothing.</summary>
	[RelayCommand(CanExecute = nameof(CanRun))]
	private async Task DryRun()
	{
		if (this.Current is not Runbook r)
			return;

		IReadOnlyList<RunbookTarget> on = this.targets();
		Dictionary<string, string> values = this.Values();
		IReadOnlyList<IReadOnlyList<string>> plan = RunbookRunner.Plan(r, on, values);

		RunbookRunViewModel dry = new("Dry run", true, r.Steps, [.. on.Select(t => t.Name)]);
		for (int i = 0; i < plan.Count; i++)
		{
			for (int k = 0; k < on.Count; k++)
				dry.Steps[i].Cells[k].Set(new RunbookCell(RunbookCellState.NotRun, plan[i][k]), dry.Steps[i].IsWait);
		}

		dry.Summary = "Nothing sent. " + Ui.Count(r.Steps.Count, "step") + " on " + Ui.Count(on.Count, "Neuron");
		dry.IsFinished = true;
		this.Run = dry;
		await this.RememberAsync(values);
	}

	/// <summary>Runs the runbook on the ticked Neurons, step by step.</summary>
	[RelayCommand(CanExecute = nameof(CanRun))]
	private async Task RunRunbook()
	{
		if (this.Current is not Runbook r)
			return;

		IReadOnlyList<RunbookTarget> on = this.targets();
		Dictionary<string, string> values = this.Values();
		await this.RememberAsync(values);

		RunbookRunViewModel live = new("Run at " + DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture), false, r.Steps, [.. on.Select(t => t.Name)]);
		this.Run = live;
		this.IsRunning = true;
		using CancellationTokenSource stop = new();
		this.running = stop;

		RunbookRunner runner = new(this.Sender) { Ask = this.AskAsync, Delay = this.Delay };
		runner.CellChanged += (s, k, cell) => OnUi(() =>
		{
			live.Steps[s].Cells[k].Set(cell, live.Steps[s].IsWait);

			// Stopped there: what comes after is not run on that Neuron.
			if (cell.State == RunbookCellState.Failed)
			{
				foreach (RunbookRunStepViewModel later in live.Steps.Skip(s + 1))
					later.Cells[k].Status = "Not run";
			}
		});

		RunbookRecord record;
		try
		{
			record = await runner.RunAsync(r, on, values, stop.Token);
		}
		finally
		{
			this.running = null;
			this.IsRunning = false;
			this.EndAsk(false);
		}

		foreach (RunbookNeuronRecord n in record.Neurons)
			live.Outcomes.Add(new RunbookOutcomeViewModel(n.Name, n.Outcome, n.Done));
		foreach (RunbookCellViewModel c in live.Steps.SelectMany(s => s.Cells).Where(c => c.State == RunbookCellState.NotRun))
			c.Status = "Not run";
		live.Summary = RecordSummary(record);
		live.IsFinished = true;

		try
		{
			await this.store.AddRecordAsync(record);
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not keep the record of the run.", ex);
		}

		await this.LoadRecordsAsync(r.Id);
	}

	[RelayCommand(CanExecute = nameof(IsRunning))]
	private void Stop()
	{
		this.running?.Cancel();
		this.EndAsk(false);
	}

	[RelayCommand]
	private void CloseRun()
	{
		if (!this.IsRunning)
			this.Run = null;
	}

	/// <summary>Keeps what was filled in, so the blanks hold it next time.</summary>
	private async Task RememberAsync(Dictionary<string, string> values)
	{
		if (this.Current is not Runbook r || values.Count == 0 || r.ReadyMade)
			return;
		if (values.Count == r.Remembered.Count && values.All(v => r.Remembered.TryGetValue(v.Key, out string? had) && had == v.Value))
			return;

		Runbook changed = r with { Remembered = values };
		this.Current = changed;
		if (!this.IsKept)
			return;

		if (this.Items.FirstOrDefault(i => i.Runbook.Id == r.Id) is RunbookItemViewModel item)
			item.Runbook = changed;
		try
		{
			await this.store.SaveAsync(changed);
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not keep the runbook's blanks.", ex);
		}
	}

	// ----- A marked step asks first -----

	/// <summary>Set while a marked step waits for a typed confirmation.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAsking))]
	private string? askPrompt;

	[ObservableProperty] private string askText = string.Empty;

	public bool IsAsking => this.AskPrompt is not null;

	private string? askExpected;
	private TaskCompletionSource<bool>? asking;

	private async Task<bool> AskAsync(RunbookAsk ask, CancellationToken cancel)
	{
		TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
		OnUi(() =>
		{
			string verb = ask.Commands.Count > 0 ? ask.Commands[0].TrimStart().Split([' ', '\n'], 2)[0] : ask.Step.Describe();
			if (ask.Neurons.Count == 1)
			{
				this.askExpected = ask.Neurons[0].Name;
				this.AskPrompt = $"Step {ask.Number}: {verb} goes to {ask.Neurons[0].Name}. Type its name to go ahead.";
			}
			else
			{
				this.askExpected = ask.Neurons.Count.ToString(CultureInfo.InvariantCulture);
				this.AskPrompt = $"Step {ask.Number}: {verb} goes to {ask.Neurons.Count} Neurons: {string.Join(", ", ask.Neurons.Select(n => n.Name))}. Type {ask.Neurons.Count} to go ahead.";
			}

			this.AskText = string.Empty;
			this.asking = answer;
		});

		using CancellationTokenRegistration _ = cancel.Register(() => answer.TrySetResult(false));
		return await answer.Task;
	}

	[RelayCommand]
	private void Confirm()
	{
		if (this.asking is not null && string.Equals(this.AskText.Trim(), this.askExpected, StringComparison.OrdinalIgnoreCase))
			this.EndAsk(true);
	}

	[RelayCommand]
	private void Decline() => this.EndAsk(false);

	private void EndAsk(bool yes)
	{
		TaskCompletionSource<bool>? a = this.asking;
		this.asking = null;
		this.askExpected = null;
		this.AskPrompt = null;
		this.AskText = string.Empty;
		a?.TrySetResult(yes);
	}

	// ----- Records -----

	/// <summary>"3 Neurons, 2 done, 1 stopped".</summary>
	public static string RecordSummary(RunbookRecord r)
	{
		int done = r.Neurons.Count(n => n.Done), stopped = r.Neurons.Count - done;
		string who = Ui.Count(r.Neurons.Count, "Neuron");
		return stopped == 0 ? who + ", done" : done == 0 ? who + ", stopped" : $"{who}, {done} done, {stopped} stopped";
	}

	private async Task LoadRecordsAsync(Guid runbookId)
	{
		IReadOnlyList<RunbookRecord> found;
		try
		{
			found = await this.store.RecordsAsync(runbookId);
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not read the records of the runbook.", ex);
			found = [];
		}

		OnUi(() =>
		{
			if (this.Current?.Id != runbookId)
				return;

			this.Records.Clear();
			foreach (RunbookRecord r in found.Take(10))
				this.Records.Add(new RunbookRecordItemViewModel(r));
			this.HasRecords = this.Records.Count > 0;
		});
	}

	/// <summary>Shows an earlier run as it was kept.</summary>
	[RelayCommand]
	private void ShowRecord(RunbookRecordItemViewModel? item)
	{
		if (item is null || this.IsRunning)
			return;

		RunbookRecord r = item.Record;
		RunbookRunViewModel kept = new("Run of " + item.When, false, r.Steps, [.. r.Neurons.Select(n => n.Name)]);
		for (int k = 0; k < r.Neurons.Count; k++)
		{
			foreach (RunbookStepRecord s in r.Neurons[k].Steps)
			{
				if (s.Number >= 1 && s.Number <= kept.Steps.Count)
				{
					RunbookRunStepViewModel step = kept.Steps[s.Number - 1];
					step.Cells[k].Set(new RunbookCell(s.Failure is null ? RunbookCellState.Done : RunbookCellState.Failed, s.Command, s.Answer, s.Failure), step.IsWait);
				}
			}

			kept.Outcomes.Add(new RunbookOutcomeViewModel(r.Neurons[k].Name, r.Neurons[k].Outcome, r.Neurons[k].Done));
		}

		foreach (RunbookCellViewModel c in kept.Steps.SelectMany(s => s.Cells).Where(c => c.State == RunbookCellState.NotRun))
			c.Status = "Not run";
		kept.Summary = RecordSummary(r) + (r.Accounts.Count > 0 ? ", through " + string.Join(", ", r.Accounts) : string.Empty);
		kept.IsFinished = true;
		this.Run = kept;
	}

	private static void OnUi(Action a)
	{
		if (Dispatcher.UIThread.CheckAccess())
			a();
		else
			Dispatcher.UIThread.Post(a);
	}
}
