using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Neuro.Nat.App.ViewModels;

/// <summary>
/// A set of Neurons, by bare JID, that the screens are narrowed to. Empty means no filter:
/// every Neuron shows. Letters are compared without regard to case.
/// </summary>
public static class NeuronSet
{
	/// <summary>No Neurons: no filter.</summary>
	public static readonly FrozenSet<string> All = FrozenSet<string>.Empty.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	public static FrozenSet<string> Of(params IEnumerable<string> jids) => jids.Where(j => j.Length > 0).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	/// <summary>True when the set lets this Neuron through: it is in it, or the set is empty.</summary>
	public static bool Admits(this IReadOnlySet<string> set, string? jid) => set.Count == 0 || (jid is not null && set.Contains(jid));

	/// <summary>The same Neurons in both.</summary>
	public static bool SameAs(this IReadOnlySet<string> set, IReadOnlySet<string> other) => set.Count == other.Count && set.SetEquals(other);

	/// <summary>The set as text for a window's address: the JIDs, sorted, between commas.</summary>
	public static string ToText(IEnumerable<string> set) => string.Join(',', set.Order(StringComparer.OrdinalIgnoreCase));

	/// <summary>Reads <see cref="ToText"/>, and the single JID that was written before there were sets.</summary>
	public static FrozenSet<string> Parse(string? text) => Of((text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

/// <summary>
/// The Neuron filter in the top bar: one button that says what the screens show, and a list
/// to tick Neurons in, with a search box over it. Works the same for 3 Neurons and for 300.
/// The ticks are the shell's; this only shows them and asks the shell to change them.
/// </summary>
public sealed partial class NeuronPickerViewModel : ObservableObject
{
	private readonly ObservableCollection<NeuronChipViewModel> neurons;
	private readonly Action<IEnumerable<string>> choose;

	public NeuronPickerViewModel(ObservableCollection<NeuronChipViewModel> neurons, Action<IEnumerable<string>> choose)
	{
		this.neurons = neurons;
		this.choose = choose;
		neurons.CollectionChanged += this.Neurons_CollectionChanged;
	}

	/// <summary>What a right-click on a Neuron in the list offers. Set by the shell.</summary>
	public IActionSource? Actions { get; init; }

	/// <summary>The Neurons that match what is typed in the search box, in the order of the Neurons list.</summary>
	public ObservableCollection<NeuronChipViewModel> Shown { get; } = [];

	[ObservableProperty] private string query = string.Empty;
	[ObservableProperty] private bool isOpen;

	/// <summary>The row the keyboard is on.</summary>
	[ObservableProperty] private NeuronChipViewModel? highlighted;

	/// <summary>What the button says: "All Neurons", a Neuron's name, or "3 of 42 Neurons".</summary>
	[ObservableProperty] private string text = "All Neurons";

	/// <summary>Said beside All and None: how many are ticked.</summary>
	[ObservableProperty] private string countText = string.Empty;

	/// <summary>True when the screens show only some of the Neurons.</summary>
	[ObservableProperty] private bool isFiltered;

	/// <summary>Nothing matches what is typed.</summary>
	[ObservableProperty] private bool isEmpty;

	/// <summary>What the button tells a screen reader and the pointer.</summary>
	[ObservableProperty] private string tip = "Choose which Neurons the screens show";

	partial void OnQueryChanged(string value) => this.Rebuild();

	partial void OnIsOpenChanged(bool value)
	{
		if (value)
		{
			this.Rebuild();
			this.Highlighted = this.Shown.FirstOrDefault();
		}
		else
		{
			this.Query = string.Empty;
		}
	}

	[RelayCommand]
	private void Open() => this.IsOpen = true;

	[RelayCommand]
	private void Close() => this.IsOpen = false;

	[RelayCommand]
	private void Toggle(NeuronChipViewModel? neuron)
	{
		neuron ??= this.Highlighted;
		if (neuron is null)
			return;

		this.Highlighted = neuron;
		string jid = neuron.BareJid;
		List<string> ticked = [.. this.neurons.Where(n => n.IsSelected).Select(n => n.BareJid)];
		if (neuron.IsSelected)
			ticked.RemoveAll(j => string.Equals(j, jid, StringComparison.OrdinalIgnoreCase));
		else
			ticked.Add(jid);

		this.choose(ticked);
	}

	/// <summary>
	/// Shows every Neuron (no filter, so Neurons that turn up later are shown too), or ticks every
	/// one that matches what is typed.
	/// </summary>
	[RelayCommand]
	private void All()
		=> this.choose(this.Query.Trim().Length == 0
			? []
			: this.neurons.Where(n => n.IsSelected).Concat(this.Shown).Select(n => n.BareJid).Distinct(StringComparer.OrdinalIgnoreCase));

	/// <summary>Takes every tick away, or the ticks of the Neurons that match what is typed. No ticks show every Neuron.</summary>
	[RelayCommand]
	private void None()
	{
		if (this.Query.Trim().Length == 0)
		{
			this.choose([]);
			return;
		}

		HashSet<string> shown = new(this.Shown.Select(n => n.BareJid), StringComparer.OrdinalIgnoreCase);
		this.choose(this.neurons.Where(n => n.IsSelected && !shown.Contains(n.BareJid)).Select(n => n.BareJid));
	}

	/// <summary>Moves the highlight up or down. Stops at the ends.</summary>
	public void Move(int delta)
	{
		if (this.Shown.Count == 0)
			return;

		int i = this.Highlighted is null ? -1 : this.Shown.IndexOf(this.Highlighted);
		this.Highlighted = this.Shown[Math.Clamp(i + delta, 0, this.Shown.Count - 1)];
	}

	/// <summary>Called by the shell after the ticks or the Neurons changed.</summary>
	internal void Refresh()
	{
		int total = this.neurons.Count;
		NeuronChipViewModel? only = null;
		int ticked = 0;
		foreach (NeuronChipViewModel n in this.neurons)
		{
			if (n.IsSelected)
			{
				ticked++;
				only = n;
			}
		}

		bool all = ticked == 0 || ticked == total;
		this.IsFiltered = !all;
		this.Text = all ? "All Neurons" : ticked == 1 ? only!.Label : $"{ticked} of {total} Neurons";
		this.Tip = "Choose Neurons";
		this.CountText = ticked == 0 ? Ui.Count(total, "Neuron") : $"{ticked} of {total} ticked";
	}

	private void Neurons_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (this.IsOpen)
			this.Rebuild();
	}

	private void Rebuild()
	{
		string[] words = this.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		List<NeuronChipViewModel> want = [.. this.neurons.Where(n => words.All(w => n.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || n.BareJid.Contains(w, StringComparison.OrdinalIgnoreCase)))];

		NeuronChipViewModel? keep = this.Highlighted;
		this.Shown.Clear();
		foreach (NeuronChipViewModel n in want)
			this.Shown.Add(n);

		this.Highlighted = keep is not null && want.Contains(keep) ? keep : want.FirstOrDefault();
		this.IsEmpty = want.Count == 0;
	}
}
