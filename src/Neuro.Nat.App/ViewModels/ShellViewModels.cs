using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.ViewModels;

/// <summary>The places in the rail. Each holds one or more of the screens in <see cref="WorkspaceKind"/>.</summary>
public enum PlaceKind
{
	Inbox,
	Neurons,
	Events,
	Chat,
	Console,
	Accounts,
	Settings,
}

/// <summary>One Neuron in the top bar: its state, its errors this hour, and whether the screens are filtered to it.</summary>
public sealed partial class NeuronChipViewModel(string bareJid) : ObservableObject
{
	public string BareJid { get; } = bareJid;

	[ObservableProperty] private string label = string.Empty;
	[ObservableProperty] private bool isOk;
	[ObservableProperty] private bool isBusy;
	[ObservableProperty] private bool isBad;
	[ObservableProperty] private int errorsLastHour;
	[ObservableProperty] private bool hasErrors;
	[ObservableProperty] private string tip = string.Empty;
	[ObservableProperty] private bool isSelected;

	internal void Apply(IReadOnlyList<FleetRowViewModel> rows)
	{
		// The same Neuron can be reached through several accounts: show the best connection.
		FleetRowViewModel best = rows.OrderBy(r => Rank(r.State)).First();
		this.Label = best.Label;
		this.IsOk = best.State == WatchState.Live;
		this.IsBusy = best.State is WatchState.Waiting or WatchState.Discovering or WatchState.Subscribing;
		this.IsBad = best.State is WatchState.Forbidden or WatchState.Offline or WatchState.Failed;
		this.ErrorsLastHour = rows.Max(r => r.ErrorsLastHour);
		this.HasErrors = this.ErrorsLastHour > 0;

		string state = best.State switch
		{
			WatchState.Live => "Listening for events",
			WatchState.Waiting => "Waiting for it to come online",
			WatchState.Discovering => "Checking what it is",
			WatchState.Subscribing => "Starting to listen",
			WatchState.Forbidden => "No access: this account is not an administrator there",
			WatchState.Offline => "Offline",
			WatchState.Failed => "Could not listen",
			_ => string.Empty,
		};
		string errors = this.ErrorsLastHour switch
		{
			0 => "No errors in the last hour",
			1 => "1 error in the last hour",
			_ => $"{this.ErrorsLastHour:N0} errors in the last hour",
		};
		this.Tip = $"{state}. {errors}. Click to show only this Neuron.";
	}

	private static int Rank(WatchState s) => s switch
	{
		WatchState.Live => 0,
		WatchState.Subscribing or WatchState.Discovering or WatchState.Waiting => 1,
		WatchState.Offline => 2,
		WatchState.Failed => 3,
		_ => 4,
	};
}

/// <summary>One line in the search box's list: somewhere to go, or something to do.</summary>
public sealed class SearchItem(string title, string hint, Geometry? icon, Action run)
{
	public string Title { get; } = title;

	/// <summary>What choosing it does, or what it belongs to, in a few words.</summary>
	public string Hint { get; } = hint;

	public Geometry? Icon { get; } = icon;

	public Action Run { get; } = run;

	/// <summary>What kind of thing it is, shown at the end of the line: Screen, Neuron, Person, Room, Problem, Node, Account, Do.</summary>
	public string Kind { get; init; } = "Screen";

	/// <summary>Shown in red: it changes something that cannot be taken back. Choosing it still asks first.</summary>
	public bool IsDanger { get; init; }

	/// <summary>More to find it by that is not shown, such as an address.</summary>
	public string? Words { get; init; }

	internal bool Has(string word)
		=> this.Title.Contains(word, StringComparison.OrdinalIgnoreCase) || this.Hint.Contains(word, StringComparison.OrdinalIgnoreCase)
			|| this.Kind.Contains(word, StringComparison.OrdinalIgnoreCase) || (this.Words?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false);

	/// <summary>How well it answers what was typed: lower is better. Names beat descriptions, and the start of a name beats its middle.</summary>
	internal int Rank(string query, string[] words)
	{
		if (words.Length == 0)
			return 0;
		if (this.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
			return 0;
		if (this.Title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase))
			return 1;
		if (words.All(w => this.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
			return 2;
		if (this.Title.Contains(words[0], StringComparison.OrdinalIgnoreCase))
			return 3;
		return 4;
	}
}

/// <summary>
/// The search box in the top bar (Ctrl+K). It finds screens, Neurons, people and rooms,
/// problems, nodes and accounts, and it runs what the right-click menus offer. Typing narrows
/// the list; Up, Down and Enter choose.
/// </summary>
public sealed partial class SearchViewModel(Func<IEnumerable<SearchItem>> source) : ObservableObject
{
	private const int MaxResults = 40;
	private const int MaxUntyped = 12;

	[ObservableProperty] private string query = string.Empty;
	[ObservableProperty] private bool isOpen;
	[ObservableProperty] private SearchItem? selected;
	[ObservableProperty] private bool hasResults;

	/// <summary>"12 of 57": said under the list when there is more than fits. Empty otherwise.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasMore))]
	private string moreText = string.Empty;

	public bool HasMore => this.MoreText.Length > 0;

	public ObservableCollection<SearchItem> Results { get; } = [];

	/// <summary>Set by the shell: brings what the search looks through up to date, once, as the box opens.</summary>
	public Action? Opening { get; set; }

	partial void OnQueryChanged(string value)
	{
		if (this.IsOpen)
			this.Rebuild();
	}

	public void Open()
	{
		this.Opening?.Invoke();
		this.Rebuild();
		this.IsOpen = true;
	}

	public void Close()
	{
		this.IsOpen = false;
		this.Query = string.Empty;
	}

	/// <summary>Moves the highlight up or down, wrapping at the ends.</summary>
	public void Move(int delta)
	{
		if (this.Results.Count == 0)
			return;

		int i = this.Selected is null ? -1 : this.Results.IndexOf(this.Selected);
		this.Selected = this.Results[((i + delta) % this.Results.Count + this.Results.Count) % this.Results.Count];
	}

	/// <summary>Goes to the highlighted result.</summary>
	public void Run()
	{
		SearchItem? item = this.Selected ?? this.Results.FirstOrDefault();
		this.Close();
		item?.Run();
	}

	private void Rebuild()
	{
		string query = this.Query.Trim();
		string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		// With nothing typed the list is the places to go. Everything else shows up as soon as it is asked for.
		List<SearchItem> all = [.. source().Where(i => words.Length == 0 ? i.Kind == "Screen" : words.All(i.Has)).OrderBy(i => i.Rank(query, words))];
		int max = words.Length == 0 ? MaxUntyped : MaxResults;

		this.Results.Clear();
		foreach (SearchItem i in all.Take(max))
			this.Results.Add(i);

		this.Selected = this.Results.FirstOrDefault();
		this.HasResults = this.Results.Count > 0;
		this.MoreText = all.Count > max ? $"The first {max} of {all.Count}. Type more to narrow it down." : string.Empty;
	}
}
