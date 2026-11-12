using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Runbooks;
using Neuro.Nat.Engine.Storage;

namespace Neuro.Nat.App.ViewModels;

/// <summary>
/// Runbooks in the Console: named series of commands, saved once and run again on the Neurons
/// ticked beside them. A list, the one that is open, its editor, and its runs.
/// </summary>
public sealed partial class RunbooksViewModel : ObservableObject
{
	private readonly Func<IReadOnlyList<RunbookTarget>> targets;
	private IRunbookStore store = new InMemoryRunbookStore();
	private CancellationTokenSource? running;

	/// <param name="targets">The Neurons ticked in the Console, which a runbook runs on.</param>
	public RunbooksViewModel(Func<IReadOnlyList<RunbookTarget>> targets, IRunbookSender sender)
	{
		this.targets = targets;
		this.Sender = sender;
		foreach (Runbook r in ReadyMadeRunbooks.All)
			this.Items.Add(new RunbookItemViewModel(r));
		this.Count = this.Items.Count;
	}

	/// <summary>Sends the commands. The console's own sender; tests put stand-in Neurons here.</summary>
	public IRunbookSender Sender { get; set; }

	/// <summary>How runs wait ("wait 30 seconds"). Tests make time pass at once.</summary>
	public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

	public ObservableCollection<RunbookItemViewModel> Items { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowFilter))]
	private int count;

	/// <summary>A filter box over the list once it is long.</summary>
	public bool ShowFilter => this.Count > 6;
	[ObservableProperty] private string filter = string.Empty;

	partial void OnFilterChanged(string value) => this.ApplyFilter();

	private void ApplyFilter()
	{
		string f = this.Filter.Trim();
		foreach (RunbookItemViewModel i in this.Items)
			i.IsShown = f.Length == 0 || i.Title.Contains(f, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Where runbooks are kept. Reads them in.</summary>
	public async Task UseAsync(IRunbookStore runbookStore)
	{
		this.store = runbookStore;
		IReadOnlyList<Runbook> kept;
		try
		{
			kept = await runbookStore.LoadAsync();
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not read the runbooks.", ex);
			return;
		}

		void Add()
		{
			foreach (Runbook r in kept)
				this.Put(r);
		}

		if (Dispatcher.UIThread.CheckAccess())
			Add();
		else
			await Dispatcher.UIThread.InvokeAsync(Add);
	}

	/// <summary>Adds a runbook to the list, or puts the new version in place of the old.</summary>
	private RunbookItemViewModel Put(Runbook r)
	{
		RunbookItemViewModel? item = this.Items.FirstOrDefault(i => i.Runbook.Id == r.Id);
		if (item is not null)
			item.Runbook = r;
		else
		{
			item = new RunbookItemViewModel(r);
			int at = 0;
			while (at < this.Items.Count && (this.Items[at].IsReadyMade || string.Compare(this.Items[at].Title, r.Title, StringComparison.OrdinalIgnoreCase) < 0))
				at++;
			this.Items.Insert(at, item);
		}

		this.Count = this.Items.Count;
		this.ApplyFilter();
		return item;
	}

	// ----- The open runbook -----

	/// <summary>The runbook in the list that is open.</summary>
	[ObservableProperty] private RunbookItemViewModel? selected;

	/// <summary>The runbook that is open: from the list, a file, or a new one.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOpen))]
	[NotifyCanExecuteChangedFor(nameof(DryRunCommand), nameof(RunRunbookCommand))]
	private Runbook? current;

	/// <summary>False for a runbook opened from a file and not kept yet.</summary>
	[ObservableProperty] private bool isKept = true;

	public bool IsOpen => this.Current is not null || this.IsEditing;

	public ObservableCollection<RunbookStepViewModel> Steps { get; } = [];
	public ObservableCollection<RunbookBlankViewModel> Blanks { get; } = [];

	[ObservableProperty] private string title = string.Empty;
	[ObservableProperty] private string stepSummary = string.Empty;
	[ObservableProperty] private bool hasBlanks;
	[ObservableProperty] private bool isReadyMade;

	/// <summary>The Neurons a run would go to, as the Console says it.</summary>
	[ObservableProperty] private string targetSummary = "Pick a Neuron";

	partial void OnSelectedChanged(RunbookItemViewModel? value)
	{
		if (value is not null && !this.IsRunning)
			this.Show(value.Runbook, kept: true);
	}

	/// <summary>"4 steps" or "4 steps, 1 asks first".</summary>
	public static string Describe(Runbook r)
	{
		string steps = Ui.Count(r.Steps.Count, "step");
		int marked = r.MarkedCount;
		return marked == 0 ? steps : $"{steps}, {marked} {(marked == 1 ? "asks" : "ask")} first";
	}

	private void Show(Runbook r, bool kept)
	{
		Guid? previous = this.Current?.Id;
		this.CancelEditing();
		this.Current = r;
		this.IsKept = kept;
		this.IsReadyMade = r.ReadyMade;
		this.Title = r.Title;
		this.StepSummary = Describe(r);

		this.Steps.Clear();
		for (int i = 0; i < r.Steps.Count; i++)
			this.Steps.Add(new RunbookStepViewModel(i + 1, r.Steps[i], r.ReadyMade));

		// The values given last time are there again.
		Dictionary<string, string> had = this.Blanks.ToDictionary(b => b.Name, b => b.Value, StringComparer.OrdinalIgnoreCase);
		this.Blanks.Clear();
		foreach (string b in r.Blanks)
		{
			string value = r.Remembered.FirstOrDefault(p => string.Equals(p.Key, b, StringComparison.OrdinalIgnoreCase)).Value
				?? (previous == r.Id && had.TryGetValue(b, out string? v) ? v : string.Empty);
			RunbookBlankViewModel blank = new(b, value);
			blank.PropertyChanged += (_, _) => this.RunRunbookCommand.NotifyCanExecuteChanged();
			this.Blanks.Add(blank);
		}
		this.HasBlanks = this.Blanks.Count > 0;

		this.Run = null;
		_ = this.LoadRecordsAsync(r.Id);
		this.DryRunCommand.NotifyCanExecuteChanged();
		this.RunRunbookCommand.NotifyCanExecuteChanged();
	}

	[RelayCommand]
	private void Close()
	{
		if (this.IsRunning)
			return;

		this.CancelEditing();
		this.Current = null;
		this.Selected = null;
		this.Run = null;
		this.OnPropertyChanged(nameof(this.IsOpen));
	}

	// ----- Making one -----

	/// <summary>Makes a runbook of commands from the Console's history. The main way to make one.</summary>
	public async Task<Runbook> SaveFromHistoryAsync(string runbookTitle, IEnumerable<string> commands)
	{
		Runbook r = new Runbook
		{
			Title = runbookTitle.Trim().Length > 0 ? runbookTitle.Trim() : "Runbook " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
			Steps = [.. commands.Where(c => c.Trim().Length > 0).Select(c => RunbookStep.Send(c.Trim()))],
		}.WithMarks();

		await this.KeepAsync(r);
		Ui.Say("Saved runbook " + r.Title);
		return r;
	}

	private async Task KeepAsync(Runbook r)
	{
		r = r with { ReadyMade = false, Changed = DateTime.UtcNow };
		try
		{
			await this.store.SaveAsync(r);
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not keep the runbook.", ex);
			Ui.Say("Could not keep the runbook");
			return;
		}

		RunbookItemViewModel item = this.Put(r);
		this.Selected = null;
		this.Selected = item;
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOpen))]
	private bool isEditing;

	[ObservableProperty] private string editTitle = string.Empty;
	[ObservableProperty] private string editText = string.Empty;
	[ObservableProperty] private string? editError;

	/// <summary>A runbook from nothing: a name and one command per line.</summary>
	[RelayCommand]
	private void New()
	{
		if (this.IsRunning)
			return;

		this.Selected = null;
		this.Current = null;
		this.Run = null;
		this.EditTitle = string.Empty;
		this.EditText = string.Empty;
		this.EditError = null;
		this.IsEditing = true;
	}

	[RelayCommand]
	private void Edit()
	{
		if (this.Current is not Runbook r || this.IsRunning)
			return;

		this.EditTitle = r.ReadyMade ? r.Title + " (mine)" : r.Title;
		this.EditText = RunbookFile.WriteSteps(r.Steps);
		this.EditError = null;
		this.IsEditing = true;
	}

	[RelayCommand]
	private async Task SaveEdit()
	{
		IReadOnlyList<RunbookStep> steps = RunbookFile.ReadSteps(this.EditText);
		if (this.EditTitle.Trim().Length == 0)
		{
			this.EditError = "Give it a name";
			return;
		}
		if (steps.Count == 0)
		{
			this.EditError = "Add a command";
			return;
		}

		// A ready-made runbook is not changed in place: the edit becomes one of your own.
		Runbook? was = this.Current;
		Runbook r = was is not null && !was.ReadyMade
			? was with { Title = this.EditTitle.Trim(), Steps = steps }
			: new Runbook { Title = this.EditTitle.Trim(), Steps = steps };

		this.IsEditing = false;
		await this.KeepAsync(r.WithMarks());
	}

	[RelayCommand]
	private void CancelEdit()
	{
		this.CancelEditing();
		this.OnPropertyChanged(nameof(this.IsOpen));
	}

	private void CancelEditing()
	{
		this.IsEditing = false;
		this.EditError = null;
	}

	/// <summary>Sets or takes off the mark of a step by hand, and keeps the change.</summary>
	[RelayCommand]
	private async Task ToggleMark(RunbookStepViewModel? step)
	{
		if (step is null || !step.CanMark || this.Current is not Runbook r || this.IsRunning)
			return;

		List<RunbookStep> steps = [.. r.Steps];
		steps[step.Number - 1] = steps[step.Number - 1] with { Marked = !step.IsMarked };
		Runbook changed = (r with { Steps = steps }).WithMarks();
		if (this.IsKept)
			await this.KeepAsync(changed);
		else
			this.Show(changed, kept: false);
	}

	// ----- Files -----

	/// <summary>Opens a runbook file: shows its steps and offers to keep it. Nothing is sent until Run.</summary>
	[RelayCommand]
	private async Task OpenFile()
	{
		if (Ui.OpenTextFile is not Func<string, Task<(string Name, string Text)?>> open || this.IsRunning)
			return;

		(string Name, string Text)? file = await open("Runbook");
		if (file is (string name, string text))
			this.OpenText(text, name);
	}

	/// <summary>Shows a runbook from the text of a file, not kept yet. False when the text is not a runbook.</summary>
	public bool OpenText(string text, string? fileName = null)
	{
		Runbook r;
		try
		{
			r = RunbookFile.Read(text);
		}
		catch (FormatException ex)
		{
			Ui.Say(fileName is null ? ex.Message : $"{fileName}: {ex.Message}");
			return false;
		}

		this.Selected = null;
		this.Run = null;
		this.Show(r, kept: false);
		return true;
	}

	[RelayCommand]
	private async Task Keep()
	{
		if (this.Current is Runbook r && !this.IsKept)
		{
			await this.KeepAsync(r);
			Ui.Say("Kept runbook " + r.Title);
		}
	}

	[RelayCommand]
	private Task SaveToFile()
		=> this.Current is Runbook r
			? Ui.Save(Ui.FileName(r.Title), [new SaveFormat("Runbook (text)", RunbookFile.Extension, () => RunbookFile.Write(r))])
			: Task.CompletedTask;

	[RelayCommand]
	private async Task Delete()
	{
		if (this.Current is not Runbook r || r.ReadyMade || this.IsRunning)
			return;

		if (this.IsKept)
		{
			try
			{
				await this.store.DeleteAsync(r.Id);
			}
			catch (Exception ex)
			{
				AppLog.Error("Could not delete the runbook.", ex);
				Ui.Say("Could not delete the runbook");
				return;
			}

			if (this.Items.FirstOrDefault(i => i.Runbook.Id == r.Id) is RunbookItemViewModel item)
				this.Items.Remove(item);
			this.Count = this.Items.Count;
		}

		this.Close();
		if (this.IsKept)
			Ui.Say("Deleted runbook " + r.Title, () => _ = this.KeepAsync(r));
	}

	/// <summary>What a runbook's list row offers on its right-click menu.</summary>
	public IReadOnlyList<ItemAction> ActionsFor(RunbookItemViewModel item)
	{
		ActionList list = [];
		list.Open("Open", () => this.Selected = item);
		list.Save("Save as a file…", Ui.FileName(item.Title), new SaveFormat("Runbook (text)", RunbookFile.Extension, () => RunbookFile.Write(item.Runbook)));
		list.Do("Dry run", () =>
		{
			this.Selected = item;
			this.DryRunCommand.Execute(null);
		}, icon: "Eye");
		if (!item.IsReadyMade)
		{
			list.Do("Delete", () =>
			{
				this.Selected = item;
				_ = this.DeleteCommand.ExecuteAsync(null);
			}, danger: true, icon: "Trash");
		}

		return list;
	}
}
