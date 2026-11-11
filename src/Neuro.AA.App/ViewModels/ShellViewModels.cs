using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.ViewModels;

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

/// <summary>One line in the search box's list: somewhere to go.</summary>
public sealed class SearchItem(string title, string hint, Geometry? icon, Action run)
{
	public string Title { get; } = title;

	/// <summary>What choosing it does, in a few words.</summary>
	public string Hint { get; } = hint;

	public Geometry? Icon { get; } = icon;

	public Action Run { get; } = run;
}

/// <summary>
/// The search box in the top bar (Ctrl+K). For now it goes places: screens, Neurons and
/// accounts. Typing narrows the list; Up, Down and Enter choose.
/// </summary>
public sealed partial class SearchViewModel(Func<IEnumerable<SearchItem>> source) : ObservableObject
{
	private const int MaxResults = 9;

	[ObservableProperty] private string query = string.Empty;
	[ObservableProperty] private bool isOpen;
	[ObservableProperty] private SearchItem? selected;
	[ObservableProperty] private bool hasResults;

	public ObservableCollection<SearchItem> Results { get; } = [];

	partial void OnQueryChanged(string value)
	{
		if (this.IsOpen)
			this.Rebuild();
	}

	public void Open()
	{
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
		string[] words = this.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		List<SearchItem> matches = [.. source()
			.Where(i => words.All(w => i.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Hint.Contains(w, StringComparison.OrdinalIgnoreCase)))
			.OrderBy(i => words.Length > 0 && i.Title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
			.Take(MaxResults)];

		this.Results.Clear();
		foreach (SearchItem i in matches)
			this.Results.Add(i);

		this.Selected = this.Results.FirstOrDefault();
		this.HasResults = this.Results.Count > 0;
	}
}
