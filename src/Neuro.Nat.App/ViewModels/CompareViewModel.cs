using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.App.Converters;
using Neuro.Nat.Engine.Compare;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Links;
using Neuro.Nat.Engine.Storage;

namespace Neuro.Nat.App.ViewModels;

/// <summary>One side of a comparison: a Neuron now, through an account, or a kept copy of it.</summary>
public sealed record CompareSide(Guid AccountId, string NeuronJid, string? CopyId = null)
{
	public bool IsKept => this.CopyId is not null;
}

/// <summary>Something to pick while choosing what to compare: a Neuron, or a kept copy.</summary>
public sealed record CompareChoice(string Title, string Meta, Func<Task> Pick)
{
	public bool HasMeta => this.Meta.Length > 0;
}

/// <summary>One row on screen.</summary>
public sealed class CompareRowViewModel(CompareRow row)
{
	public CompareRow Row { get; } = row;
	public string Label => this.Row.Label;
	public string Left => this.Row.Left.Length == 0 && this.Row.Kind != RowKind.OnlyRight ? "-" : this.Row.Left;
	public string Right => this.Row.Right.Length == 0 && this.Row.Kind != RowKind.OnlyLeft ? "-" : this.Row.Right;
	public bool IsDifferent => this.Row.Kind == RowKind.Different;
	public bool IsOnlyLeft => this.Row.Kind == RowKind.OnlyLeft;
	public bool IsOnlyRight => this.Row.Kind == RowKind.OnlyRight;
	public bool IsCounted => this.Row.IsDifference;

	/// <summary>Same, state now, or not read: shown quieter than a difference.</summary>
	public bool IsQuiet => !this.Row.IsDifference;

	/// <summary>The mark between the two values.</summary>
	public string Mark => this.Row.Kind switch
	{
		RowKind.Different => "≠",
		RowKind.Same => "=",
		_ => string.Empty,
	};

	/// <summary>A value from the Neuron is machine text; "Same", "Different", "Set" and "Not read" are words.</summary>
	public bool IsMono => !this.Row.IsSecret && this.Row.Kind is not (RowKind.NotRead or RowKind.Background);
}

/// <summary>A heading under a group, such as "Ports", with its rows.</summary>
public sealed record CompareSectionViewModel(string Title, IReadOnlyList<CompareRowViewModel> Rows)
{
	public bool HasTitle => this.Title.Length > 0;
}

/// <summary>Software, Settings or State now, with how many differences it has.</summary>
public sealed record CompareGroupViewModel(string Title, string Meta, IReadOnlyList<CompareSectionViewModel> Sections, bool IsBackground);

/// <summary>
/// Two Neurons side by side, or a Neuron beside a kept copy of how it was. Only the
/// differences show at first, with a count. Passwords and keys are never shown, only whether
/// they are the same. Every comparison keeps a copy of what it read, so a Neuron can later be
/// compared with how it was.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject, IActionSource
{
	private int run;

	public CompareViewModel(INeuronReader reader)
	{
		this.Reader = reader;
	}

	/// <summary>Reads the Neurons. Tests hand in snapshots instead.</summary>
	public INeuronReader Reader { get; set; }

	/// <summary>Where copies are kept. In memory unless the app gives the database.</summary>
	public INeuronCopyStore Store { get; set; } = new MemoryNeuronCopyStore();

	/// <summary>Set by the shell: the Neurons there are, to choose from.</summary>
	public Func<IReadOnlyList<FleetRowViewModel>> Neurons { get; set; } = () => [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasLeft))]
	private CompareSide? left;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasResult))]
	private CompareSide? right;

	public bool HasLeft => this.Left is not null;

	/// <summary>What was compared last.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasResult))]
	private Comparison? result;

	public bool HasResult => this.Result is not null && !this.IsChoosing;

	[ObservableProperty] private bool isBusy;
	[ObservableProperty] private string busyText = string.Empty;

	[ObservableProperty] private bool onlyDifferences = true;

	[ObservableProperty] private string countText = string.Empty;
	[ObservableProperty] private bool hasDifferences;
	[ObservableProperty] private string leftName = string.Empty;
	[ObservableProperty] private string rightName = string.Empty;
	[ObservableProperty] private string leftWhen = string.Empty;
	[ObservableProperty] private string rightWhen = string.Empty;

	public ObservableCollection<CompareGroupViewModel> Groups { get; } = [];
	public ObservableCollection<string> Notes { get; } = [];
	[ObservableProperty] private bool hasNotes;

	/// <summary>True when nothing that is counted shows: no differences, or none left with "Only differences".</summary>
	[ObservableProperty] private bool showsNoDifferences;

	// ----- Choosing what to compare, in place -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasResult))]
	private bool isChoosing;

	[ObservableProperty] private string chooseTitle = string.Empty;
	[ObservableProperty] private string chooseEmpty = string.Empty;
	public ObservableCollection<CompareChoice> Choices { get; } = [];
	[ObservableProperty] private bool hasChoices;

	/// <summary>Shown under an empty list of kept copies: keeps one now.</summary>
	[ObservableProperty] private bool canKeepFromChoice;

	private CompareSide? choosingFor;

	/// <summary>The link for what shows: two Neurons, or a Neuron and a kept copy.</summary>
	public NatLink? Link => this.Left is CompareSide l && this.Right is CompareSide r && this.HasResult
		? r.CopyId is string id ? NatLink.CompareWithKept(l.NeuronJid, id) : NatLink.Compare(l.NeuronJid, r.NeuronJid)
		: null;

	private static string Name(string jid) => NeuronNameConverter.Name(jid);

	/// <summary>Reads both Neurons and lays them side by side. Each read is kept as a copy.</summary>
	public Task CompareAsync(CompareSide a, CompareSide b) => this.RunAsync(a, b);

	/// <summary>Opens the screen to pick the second Neuron for <paramref name="a"/>.</summary>
	public void ChooseOther(CompareSide a)
	{
		this.choosingFor = a;
		this.Left = a;
		this.ChooseTitle = "Compare " + Name(a.NeuronJid) + " with";
		this.ChooseEmpty = "No other Neurons";
		this.CanKeepFromChoice = false;
		this.Choices.Clear();
		foreach (FleetRowViewModel row in this.Neurons().Where(r => !string.Equals(r.BareJid, a.NeuronJid, StringComparison.OrdinalIgnoreCase))
			.GroupBy(r => r.BareJid, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderBy(r => r.State == WatchState.Live ? 0 : 1).First()))
		{
			CompareSide b = new(row.AccountId, row.BareJid);
			this.Choices.Add(new CompareChoice(row.Label, row.StateText, () => this.RunAsync(a, b)));
		}

		this.HasChoices = this.Choices.Count > 0;
		this.IsChoosing = true;
	}

	/// <summary>Opens the screen to pick the first Neuron, when nothing was picked yet.</summary>
	public void ChooseFirst()
	{
		this.choosingFor = null;
		this.ChooseTitle = "Compare which Neuron?";
		this.ChooseEmpty = "No Neurons yet";
		this.CanKeepFromChoice = false;
		this.Choices.Clear();
		foreach (FleetRowViewModel row in this.Neurons().GroupBy(r => r.BareJid, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderBy(r => r.State == WatchState.Live ? 0 : 1).First()))
		{
			CompareSide a = new(row.AccountId, row.BareJid);
			this.Choices.Add(new CompareChoice(row.Label, row.StateText, () =>
			{
				this.ChooseOther(a);
				return Task.CompletedTask;
			}));
		}

		this.HasChoices = this.Choices.Count > 0;
		this.IsChoosing = true;
	}

	/// <summary>Opens the screen to pick a kept copy of <paramref name="a"/> to compare it with.</summary>
	public async Task ChooseEarlierAsync(CompareSide a)
	{
		this.choosingFor = a;
		this.Left = a;
		this.ChooseTitle = "Compare " + Name(a.NeuronJid) + " with a kept copy";
		this.ChooseEmpty = "No copies of " + Name(a.NeuronJid) + " kept yet";
		this.Choices.Clear();
		this.IsChoosing = true;

		IReadOnlyList<KeptCopy> copies = await this.Store.ListAsync(a.NeuronJid);
		DateTime now = DateTime.Now;
		this.Choices.Clear();
		foreach (KeptCopy copy in copies)
		{
			DateTime local = copy.Taken.ToLocalTime();
			string id = copy.CopyId;
			this.Choices.Add(new CompareChoice(local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture), RelativeTime.Format(local, now), () => this.RunAsync(a, a with { CopyId = id })));
		}

		this.HasChoices = this.Choices.Count > 0;
		this.CanKeepFromChoice = !this.HasChoices;
	}

	/// <summary>Reads a Neuron now and keeps the copy. Says so in the note at the bottom.</summary>
	public async Task<NeuronSnapshot?> KeepCopyAsync(CompareSide a)
	{
		string name = Name(a.NeuronJid);
		Ui.Say("Reading " + name);
		NeuronSnapshot s = await this.Reader.ReadAsync(a.AccountId, a.NeuronJid);
		if (s.NoAccess)
		{
			Ui.Say("No access on " + name);
			return null;
		}

		NeuronSnapshot kept = await this.Store.KeepAsync(s);
		Ui.Say("Kept a copy of " + name);
		if (this.IsChoosing && this.choosingFor == a && this.CanKeepFromChoice)
			await this.ChooseEarlierAsync(a);
		return kept;
	}

	[RelayCommand]
	private async Task KeepFromChoice()
	{
		if (this.choosingFor is CompareSide a)
			await this.KeepCopyAsync(a);
	}

	[RelayCommand]
	private async Task Pick(CompareChoice? choice)
	{
		if (choice is not null)
			await choice.Pick();
	}

	/// <summary>Leaves the choice and shows what was compared last, if anything.</summary>
	[RelayCommand]
	private void CancelChoice()
	{
		this.IsChoosing = false;
		if (this.Result is null)
			this.ChooseFirst();
	}

	/// <summary>Reads both sides again. A kept copy stays as it was.</summary>
	[RelayCommand]
	private Task ReadAgain() => this.Left is CompareSide a && this.Right is CompareSide b ? this.RunAsync(a, b) : Task.CompletedTask;

	[RelayCommand]
	private Task CopyText() => Ui.Copy(this.Result?.ToText(this.OnlyDifferences), "the comparison");

	[RelayCommand]
	private Task SaveText()
		=> this.Result is Comparison c
			? Ui.Save(Ui.FileName("Compare " + c.LeftName + " and " + c.RightName), [new SaveFormat("Text", "txt", () => c.ToText(this.OnlyDifferences))])
			: Task.CompletedTask;

	[RelayCommand]
	private Task CopyLink() => Ui.Copy(this.Link?.ToString(), "the link");

	private async Task RunAsync(CompareSide a, CompareSide b)
	{
		int me = Interlocked.Increment(ref this.run);
		this.Left = a;
		this.Right = b;
		this.IsChoosing = false;
		this.IsBusy = true;
		this.BusyText = b.IsKept ? "Reading " + Name(a.NeuronJid) : "Reading " + Name(a.NeuronJid) + " and " + Name(b.NeuronJid);
		try
		{
			Task<NeuronSnapshot> readA = this.ReadSideAsync(a);
			Task<NeuronSnapshot> readB = this.ReadSideAsync(b);
			NeuronSnapshot sa = await readA;
			NeuronSnapshot sb = await readB;
			if (me != Volatile.Read(ref this.run))
				return;

			this.Show(Build(sa, sb));
		}
		catch (Exception ex)
		{
			if (me == Volatile.Read(ref this.run))
				Ui.Say(ex.Message);
		}
		finally
		{
			if (me == Volatile.Read(ref this.run))
				this.IsBusy = false;
		}
	}

	/// <summary>A kept copy from the store, or the Neuron now, which is then kept too.</summary>
	private async Task<NeuronSnapshot> ReadSideAsync(CompareSide side)
	{
		if (side.CopyId is string id)
		{
			return await this.Store.GetAsync(id) is NeuronSnapshot kept
				? kept
				: new NeuronSnapshot
				{
					NeuronJid = side.NeuronJid,
					CopyId = id,
					NotRead = Enum.GetValues<CompareGroup>().ToDictionary(g => g, _ => "That copy is not kept any more"),
				};
		}

		NeuronSnapshot s = await this.Reader.ReadAsync(side.AccountId, side.NeuronJid);
		if (s.NoAccess || s.Values.Count == 0)
			return s;

		try
		{
			return await this.Store.KeepAsync(s) with { CopyId = null };
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not keep a copy of " + side.NeuronJid + ".", ex);
			return s;
		}
	}

	/// <summary>The comparison of two snapshots, with the names they go by.</summary>
	public static Comparison Build(NeuronSnapshot a, NeuronSnapshot b)
	{
		string nameA = Name(a.NeuronJid);
		string nameB = Name(b.NeuronJid);
		if (b.CopyId is not null)
			nameB += ", kept " + b.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
		else if (string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase) && !string.Equals(a.NeuronJid, b.NeuronJid, StringComparison.OrdinalIgnoreCase))
			(nameA, nameB) = (a.NeuronJid, b.NeuronJid);

		return NeuronComparer.Compare(a, b, nameA, nameB);
	}

	/// <summary>Shows a comparison.</summary>
	public void Show(Comparison c)
	{
		this.IsChoosing = false;
		this.Result = c;
		this.LeftName = Name(c.Left.NeuronJid);
		this.RightName = Name(c.Right.NeuronJid);
		this.LeftWhen = When(c.Left);
		this.RightWhen = When(c.Right);
		this.CountText = c.CountText;
		this.HasDifferences = c.Differences > 0;

		this.Notes.Clear();
		foreach (string n in c.Notes)
			this.Notes.Add(n);
		this.HasNotes = this.Notes.Count > 0;

		this.Rebuild();
		this.OnPropertyChanged(nameof(this.Link));
	}

	private static string When(NeuronSnapshot s)
	{
		if (s.ReadAt == default)
			return string.Empty;

		DateTime local = s.ReadAt.ToLocalTime();
		string at = local.Date == DateTime.Today ? local.ToString("HH:mm", CultureInfo.CurrentCulture) : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
		return (s.CopyId is null ? "Read " : "Kept ") + at;
	}

	partial void OnOnlyDifferencesChanged(bool value) => this.Rebuild();

	private void Rebuild()
	{
		this.Groups.Clear();
		if (this.Result is not Comparison c)
			return;

		foreach (IGrouping<CompareGroup, CompareRow> g in c.Rows.GroupBy(r => r.Group))
		{
			bool background = g.Key == CompareGroup.StateNow;
			List<CompareSectionViewModel> sections = [];
			foreach (IGrouping<string, CompareRow> s in g.GroupBy(r => r.Section))
			{
				List<CompareRowViewModel> rows = [.. s.Where(r => background || !this.OnlyDifferences || r.IsDifference).Select(r => new CompareRowViewModel(r))];
				if (rows.Count > 0)
					sections.Add(new CompareSectionViewModel(background || s.Key == Comparison.GroupName(g.Key) ? string.Empty : s.Key, rows));
			}

			if (sections.Count == 0)
				continue;

			int differences = g.Count(r => r.IsDifference);
			string meta = background ? "Not counted" : differences == 0 ? "The same" : Ui.Count(differences, "difference");
			this.Groups.Add(new CompareGroupViewModel(Comparison.GroupName(g.Key), meta, sections, background));
		}

		this.ShowsNoDifferences = this.OnlyDifferences && c.Differences == 0;
	}

	// ----- Right-click and the More button -----

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		ActionList list = [];
		if (items.Count != 1 || items[0] is not CompareViewModel || this.Result is null)
			return list;

		list.Copy("Copy as text", () => this.Result?.ToText(this.OnlyDifferences), "the comparison", "Ctrl+C");
		list.Add(new ItemAction("Save as file…", ActionGroup.Copy, () => this.SaveText()) { Key = "Ctrl+S", Icon = "Save" });
		list.Filter(this.OnlyDifferences ? "Show everything" : "Only differences", () => this.OnlyDifferences = !this.OnlyDifferences);
		list.Do("Read again", () => this.ReadAgain(), icon: "Refresh", key: "F5");
		if (this.Left is CompareSide a)
			list.Do("Keep a copy of " + Name(a.NeuronJid) + " now", () => this.KeepCopyAsync(a), icon: "Save");
		return list;
	}
}
