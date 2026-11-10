using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Chat;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.ViewModels;

/// <summary>A Neuron the console can talk to, with a check box for sending to several at once.</summary>
public sealed partial class ConsoleTargetViewModel(Guid accountId, string bareJid) : ObservableObject
{
	public Guid AccountId { get; } = accountId;
	public string BareJid { get; } = bareJid;

	[ObservableProperty] private string label = bareJid;
	[ObservableProperty] private string accountLabel = string.Empty;
	[ObservableProperty] private int accountColorIndex;
	[ObservableProperty] private bool isChecked;
	[ObservableProperty] private bool isOnline;
}

/// <summary>What one Neuron answered to a command.</summary>
public sealed partial class ConsoleResultViewModel(ConsoleEntry entry, string neuronLabel) : ObservableObject
{
	public ConsoleEntry Entry { get; } = entry;
	public string NeuronLabel { get; } = neuronLabel;

	[ObservableProperty] private string markdown = string.Empty;
	[ObservableProperty] private string status = "Waiting for the Neuron…";
	[ObservableProperty] private bool isWaiting = true;
	[ObservableProperty] private bool hasError;

	public void Refresh()
	{
		IReadOnlyList<ConsoleResponse> r = this.Entry.Responses;
		this.Markdown = string.Join("\n\n", r.Select(x => x.Markdown));
		this.HasError = this.Entry.Error is not null;
		this.IsWaiting = r.Count == 0 && !this.HasError;
		this.Status = this.HasError ? this.Entry.Error! :
			r.Count == 0 ? "Waiting for the Neuron…" :
			r.Any(x => x.Corrected is not null) ? "Updated " + r.Max(x => x.Corrected ?? x.Timestamp).ToString("HH:mm:ss", CultureInfo.CurrentCulture) :
			"Answered " + r[^1].Timestamp.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
	}
}

/// <summary>A command as run: once, on one or several Neurons.</summary>
public sealed class ConsoleRunViewModel(string command, DateTime time)
{
	public string Command { get; } = command;
	public string Time { get; } = time.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
	public ObservableCollection<ConsoleResultViewModel> Results { get; } = [];
	public bool IsFanOut => this.Results.Count > 1;
	public int Columns => Math.Clamp(this.Results.Count, 1, 3);
}

/// <summary>
/// The chat admin console: commands and script to one Neuron or several at once, with the
/// answers rendered as Markdown and, for several Neurons, side by side.
/// </summary>
public sealed partial class ConsoleViewModel : ObservableObject
{
	private readonly ChatService chat;
	private readonly MonitorService monitors;
	private readonly AccountManager accounts;
	private readonly List<string> history = [];
	private readonly DispatcherTimer timer;
	private int historyIndex = -1;
	private int dirty;

	public ConsoleViewModel(ChatService chat, MonitorService monitors, AccountManager accounts)
	{
		this.chat = chat;
		this.monitors = monitors;
		this.accounts = accounts;
		monitors.NeuronsChanged += (_, _) => Dispatcher.UIThread.Post(this.SyncTargets);
		this.Targets.CollectionChanged += (_, e) =>
		{
			foreach (ConsoleTargetViewModel t in e.NewItems?.OfType<ConsoleTargetViewModel>() ?? [])
			{
				t.PropertyChanged += (_, p) =>
				{
					if (p.PropertyName == nameof(ConsoleTargetViewModel.IsChecked))
						this.TargetsChanged();
				};
			}
			this.TargetsChanged();
		};
		this.timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => this.Tick());
		this.timer.Start();
	}

	public ObservableCollection<ConsoleTargetViewModel> Targets { get; } = [];
	public ObservableCollection<ConsoleRunViewModel> Runs { get; } = [];
	public ObservableCollection<string> Suggestions { get; } = [];

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RunCommand))]
	private string commandText = string.Empty;

	[ObservableProperty] private string targetSummary = "Pick a Neuron";
	[ObservableProperty] private bool hasTargets;
	[ObservableProperty] private bool hasRuns;
	[ObservableProperty] private bool hasSuggestions;

	/// <summary>Set while a command that changes the Neuron waits for its typed confirmation.</summary>
	[ObservableProperty] private string? confirmPrompt;
	[ObservableProperty] private string confirmText = string.Empty;
	private string? confirmExpected;
	private string? confirmCommand;

	public bool IsConfirming => this.ConfirmPrompt is not null;

	partial void OnConfirmPromptChanged(string? value) => this.OnPropertyChanged(nameof(this.IsConfirming));

	/// <summary>Selects one Neuron as the only target. Used when opening the console from elsewhere.</summary>
	public void Focus(Guid accountId, string bareJid)
	{
		this.SyncTargets();
		foreach (ConsoleTargetViewModel t in this.Targets)
			t.IsChecked = t.AccountId == accountId && string.Equals(t.BareJid, bareJid, StringComparison.OrdinalIgnoreCase);
		this.TargetsChanged();
	}

	/// <summary>Updates the list of Neurons. UI thread.</summary>
	public void SyncTargets()
	{
		Dictionary<Guid, AccountProfile> profiles = this.accounts.Sessions.ToDictionary(s => s.Profile.Id, s => s.Profile);

		foreach (WatchedNeuron n in this.monitors.Neurons.OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase))
		{
			ConsoleTargetViewModel? t = this.Targets.FirstOrDefault(x => x.AccountId == n.AccountId && string.Equals(x.BareJid, n.BareJid, StringComparison.OrdinalIgnoreCase));
			if (t is null)
			{
				t = new ConsoleTargetViewModel(n.AccountId, n.BareJid);
				this.Targets.Add(t);
			}

			t.Label = n.Label;
			t.IsOnline = n.State == WatchState.Live;
			AccountProfile? p = profiles.TryGetValue(n.AccountId, out AccountProfile? x) ? x : null;
			t.AccountLabel = p?.Label ?? string.Empty;
			t.AccountColorIndex = p?.ColorIndex ?? 0;
		}

		if (!this.Targets.Any(t => t.IsChecked) && this.Targets.FirstOrDefault(t => t.IsOnline) is ConsoleTargetViewModel first)
			first.IsChecked = true;

		this.TargetsChanged();
	}

	private List<ConsoleTargetViewModel> Checked => [.. this.Targets.Where(t => t.IsChecked)];

	private void TargetsChanged()
	{
		List<ConsoleTargetViewModel> c = this.Checked;
		this.HasTargets = c.Count > 0;
		this.TargetSummary = c.Count switch
		{
			0 => "Pick a Neuron",
			1 => c[0].Label,
			_ => $"{c.Count} Neurons: " + string.Join(", ", c.Select(t => t.Label)),
		};

		foreach (ConsoleTargetViewModel t in c)
		{
			ConsoleSession s = this.chat.Console(t.AccountId, t.BareJid);
			s.CommandsLoaded -= this.Session_CommandsLoaded;
			s.CommandsLoaded += this.Session_CommandsLoaded;
			_ = s.LoadCommandsAsync();
		}

		this.RunCommand.NotifyCanExecuteChanged();
		this.UpdateSuggestions();
	}

	private void Session_CommandsLoaded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(this.UpdateSuggestions);

	partial void OnCommandTextChanged(string value)
	{
		this.UpdateSuggestions();
		if (this.ConfirmPrompt is not null && value != this.confirmCommand)
			this.CancelConfirm();
	}

	private void UpdateSuggestions()
	{
		this.Suggestions.Clear();
		string text = this.CommandText.TrimStart();
		if (text.Contains('\n') || text.Contains(' '))
		{
			this.HasSuggestions = false;
			return;
		}

		IEnumerable<string> all = this.Checked
			.SelectMany(t => this.chat.Console(t.AccountId, t.BareJid).Commands)
			.Where(c => c.Length > 1 && char.IsLetter(c[0]))
			.Distinct(StringComparer.OrdinalIgnoreCase);

		foreach (string c in all.Where(c => text.Length == 0 || (c.StartsWith(text, StringComparison.OrdinalIgnoreCase) && !c.Equals(text, StringComparison.OrdinalIgnoreCase))).Take(text.Length == 0 ? 14 : 10))
			this.Suggestions.Add(c);

		this.HasSuggestions = this.Suggestions.Count > 0;
	}

	[RelayCommand]
	private void UseSuggestion(string? s)
	{
		if (!string.IsNullOrEmpty(s))
			this.CommandText = s + " ";
	}

	/// <summary>Tab: completes to the first suggestion. Returns true when it did.</summary>
	public bool Complete()
	{
		if (this.Suggestions.Count == 0)
			return false;

		this.CommandText = this.Suggestions[0] + " ";
		return true;
	}

	/// <summary>Steps through earlier commands. <paramref name="step"/> is -1 for older, +1 for newer.</summary>
	public void History(int step)
	{
		if (this.history.Count == 0)
			return;

		this.historyIndex = this.historyIndex < 0 ? this.history.Count : this.historyIndex;
		this.historyIndex = Math.Clamp(this.historyIndex + step, 0, this.history.Count);
		this.CommandText = this.historyIndex == this.history.Count ? string.Empty : this.history[this.historyIndex];
	}

	private bool CanRun() => !string.IsNullOrWhiteSpace(this.CommandText) && this.Targets.Any(t => t.IsChecked);

	[RelayCommand(CanExecute = nameof(CanRun))]
	private async Task Run()
	{
		string command = this.CommandText.Trim();
		List<ConsoleTargetViewModel> targets = this.Checked;
		if (command.Length == 0 || targets.Count == 0)
			return;

		if (ConsoleSession.ChangesTheNeuron(command))
		{
			string expected = targets.Count == 1 ? targets[0].Label : targets.Count.ToString(CultureInfo.InvariantCulture);

			if (this.confirmCommand != command)
			{
				this.confirmCommand = command;
				this.confirmExpected = expected;
				this.ConfirmText = string.Empty;
				string verb = command.Split(' ', 2)[0];
				this.ConfirmPrompt = targets.Count == 1
					? $"{verb} changes {targets[0].Label}. Type its name to go ahead."
					: $"{verb} changes {targets.Count} Neurons at once. Type {targets.Count} to go ahead.";
				return;
			}

			if (!string.Equals(this.ConfirmText.Trim(), this.confirmExpected, StringComparison.OrdinalIgnoreCase))
				return;
		}

		this.CancelConfirm();
		this.history.Remove(command);
		this.history.Add(command);
		this.historyIndex = -1;
		this.CommandText = string.Empty;

		ConsoleRunViewModel run = new(command, DateTime.Now);
		string? broadcast = targets.Count > 1 ? Guid.NewGuid().ToString("N") : null;

		foreach (ConsoleTargetViewModel t in targets)
		{
			ConsoleSession s = this.chat.Console(t.AccountId, t.BareJid);
			s.Changed -= this.Session_Changed;
			s.Changed += this.Session_Changed;
			ConsoleEntry e = await s.SendAsync(command, broadcast);
			ConsoleResultViewModel r = new(e, t.Label);
			r.Refresh();
			run.Results.Add(r);
		}

		this.Runs.Add(run);
		this.HasRuns = true;
	}

	[RelayCommand]
	private void CancelConfirm()
	{
		this.ConfirmPrompt = null;
		this.confirmCommand = null;
		this.confirmExpected = null;
		this.ConfirmText = string.Empty;
	}

	[RelayCommand]
	private void Clear()
	{
		this.Runs.Clear();
		this.HasRuns = false;
	}

	private void Session_Changed(object? sender, ConsoleEntry e) => Interlocked.Exchange(ref this.dirty, 1);

	private void Tick()
	{
		if (Interlocked.Exchange(ref this.dirty, 0) == 0)
			return;

		foreach (ConsoleRunViewModel run in this.Runs.Skip(Math.Max(0, this.Runs.Count - 30)))
		{
			foreach (ConsoleResultViewModel r in run.Results)
				r.Refresh();
		}
	}
}
