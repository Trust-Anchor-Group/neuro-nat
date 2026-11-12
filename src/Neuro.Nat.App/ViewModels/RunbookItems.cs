using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.Nat.Engine.Runbooks;

namespace Neuro.Nat.App.ViewModels;

/// <summary>A runbook in the Console's list.</summary>
public sealed partial class RunbookItemViewModel(Runbook runbook) : ObservableObject
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Title), nameof(Summary), nameof(IsReadyMade))]
	private Runbook runbook = runbook;

	[ObservableProperty] private bool isShown = true;

	public string Title => this.Runbook.Title;
	public bool IsReadyMade => this.Runbook.ReadyMade;
	public string Summary => RunbooksViewModel.Describe(this.Runbook);
}

/// <summary>A step of the open runbook.</summary>
public sealed partial class RunbookStepViewModel(int number, RunbookStep step, bool readOnly) : ObservableObject
{
	public int Number { get; } = number;
	public RunbookStep Step { get; } = step;
	public string Text { get; } = step.Describe();
	public bool IsCommand => this.Step.IsCommand;
	public bool IsWait => !this.Step.IsCommand;
	public bool IsMarked => this.Step.Marked;

	/// <summary>A mark can be set or taken off by hand, except where the step changes the Neuron.</summary>
	public bool CanMark => !readOnly && this.Step.IsCommand && !this.Step.ChangesTheNeuron;

	public string MarkTip => this.Step.ChangesTheNeuron ? "Always asks first: it changes the Neuron" : this.IsMarked ? "Stop asking first" : "Ask first";
}

/// <summary>A blank of the open runbook and what to fill it with.</summary>
public sealed partial class RunbookBlankViewModel(string name, string value) : ObservableObject
{
	public string Name { get; } = name;
	public string Label => "{" + this.Name + "}";

	[ObservableProperty] private string value = value;
}

/// <summary>One step on one Neuron in a run or a dry run.</summary>
public sealed partial class RunbookCellViewModel(string neuronLabel, bool dry) : ObservableObject
{
	public string NeuronLabel { get; } = neuronLabel;
	public bool IsDry { get; } = dry;

	[ObservableProperty] private string command = string.Empty;
	[ObservableProperty] private string markdown = string.Empty;
	[ObservableProperty] private string status = string.Empty;
	[ObservableProperty] private bool hasError;
	[ObservableProperty] private bool isWaiting;
	[ObservableProperty] private bool hasAnswer;
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsPending))]
	private RunbookCellState state;

	/// <summary>Not come to yet in a run, or not run at all.</summary>
	public bool IsPending => !this.IsDry && this.State == RunbookCellState.NotRun;

	public void Set(RunbookCell cell, bool isWait)
	{
		this.State = cell.State;
		this.Command = cell.Command;
		this.Markdown = cell.Markdown;
		this.HasAnswer = cell.Markdown.Length > 0;
		this.HasError = cell.State == RunbookCellState.Failed;
		this.IsWaiting = cell.State is RunbookCellState.Running or RunbookCellState.Asking;
		this.Status = cell.State switch
		{
			RunbookCellState.Asking => "Waiting for you",
			RunbookCellState.Running => isWait ? "Waiting" : "Waiting for the Neuron…",
			RunbookCellState.Done => isWait ? "Done" : "Answered",
			RunbookCellState.Failed => cell.Failure ?? "Failed",
			_ => string.Empty,
		};
	}
}

/// <summary>A step in a run, with each Neuron's answer under it.</summary>
public sealed class RunbookRunStepViewModel(int number, RunbookStep step)
{
	public int Number { get; } = number;
	public RunbookStep Step { get; } = step;
	public string Text { get; } = step.Describe();
	public bool IsMarked => this.Step.Marked;
	public bool IsWait => !this.Step.IsCommand;
	public ObservableCollection<RunbookCellViewModel> Cells { get; } = [];
	public int Columns => Math.Clamp(this.Cells.Count, 1, 3);
}

/// <summary>How the run ended on one Neuron.</summary>
public sealed class RunbookOutcomeViewModel(string neuronLabel, string text, bool done)
{
	public string NeuronLabel { get; } = neuronLabel;
	public string Text { get; } = text;
	public bool IsDone { get; } = done;
	public string Tone => this.IsDone ? "ok" : "bad";
}

/// <summary>A run of a runbook, as it goes, or as it was kept.</summary>
public sealed partial class RunbookRunViewModel : ObservableObject
{
	public RunbookRunViewModel(string title, bool dry, IReadOnlyList<RunbookStep> steps, IReadOnlyList<string> neurons)
	{
		this.Title = title;
		this.IsDry = dry;
		for (int i = 0; i < steps.Count; i++)
		{
			RunbookRunStepViewModel s = new(i + 1, steps[i]);
			foreach (string n in neurons)
				s.Cells.Add(new RunbookCellViewModel(n, dry));
			this.Steps.Add(s);
		}
	}

	public string Title { get; }
	public bool IsDry { get; }
	public ObservableCollection<RunbookRunStepViewModel> Steps { get; } = [];
	public ObservableCollection<RunbookOutcomeViewModel> Outcomes { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowOutcomes))]
	private bool isFinished;

	public bool ShowOutcomes => this.IsFinished && this.Outcomes.Count > 0;
	[ObservableProperty] private string summary = string.Empty;
}

/// <summary>An earlier run of the open runbook.</summary>
public sealed class RunbookRecordItemViewModel(RunbookRecord record)
{
	public RunbookRecord Record { get; } = record;

	public string When { get; } = (record.Started.Date == DateTime.Today ? "Today " : record.Started.ToString("d MMM ", CultureInfo.CurrentCulture))
		+ record.Started.ToString("HH:mm", CultureInfo.CurrentCulture);

	public string Summary { get; } = RunbooksViewModel.RecordSummary(record);

	public string Accounts { get; } = string.Join(", ", record.Accounts);
}
