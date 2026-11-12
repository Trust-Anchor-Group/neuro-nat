using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.App.Features;

namespace Neuro.Nat.App.ViewModels;

/// <summary>A place a feature adds to the rail.</summary>
public sealed partial class FeaturePlaceItem(string key, string title, Geometry? icon) : ObservableObject
{
	public string Key { get; } = key;
	public string Title { get; } = title;
	public Geometry? Icon { get; } = icon;

	[ObservableProperty] private bool isCurrent;
}

/// <summary>A tab a feature adds to a Neuron's page.</summary>
public sealed partial class FeatureTabItem(string key, string title) : ObservableObject
{
	public string Key { get; } = key;
	public string Title { get; } = title;

	[ObservableProperty] private bool isCurrent;
}

/// <summary>
/// What the registered features add to the shell: places in the rail, tabs on a Neuron's page and
/// lines in the search box. With no features, the lists are empty and nothing shows.
/// </summary>
public sealed partial class MainWindowViewModel
{
	private readonly Dictionary<string, object> featureContents = [];
	private string? featurePlaceKey;
	private string? featureTabKey;
	private bool featuresQueued;

	/// <summary>The features the host registered. None in tests that do not give any.</summary>
	public FeatureSet Features { get; }

	/// <summary>Places the features add, under the rail's own.</summary>
	public ObservableCollection<FeaturePlaceItem> FeaturePlaces { get; } = [];

	/// <summary>Tabs the features add to the open Neuron's page, after its own.</summary>
	public ObservableCollection<FeatureTabItem> PageFeatureTabs { get; } = [];

	/// <summary>What the feature place or tab on screen shows.</summary>
	[ObservableProperty] private object? featureContent;

	public bool IsFeature => this.Workspace == WorkspaceKind.Feature;
	public bool IsNeuronFeature => this.Workspace == WorkspaceKind.NeuronFeature;

	/// <summary>True when a feature's place or tab is on screen.</summary>
	public bool ShowsFeature => this.IsFeature || this.IsNeuronFeature;

	/// <summary>The name of the feature place on screen, for the top bar.</summary>
	private string FeatureTitle => this.FeaturePlaces.FirstOrDefault(p => p.Key == this.featurePlaceKey)?.Title ?? string.Empty;

	private string FeatureTabTitle => this.PageFeatureTabs.FirstOrDefault(t => t.Key == this.featureTabKey)?.Title ?? string.Empty;

	private void StartFeatures()
	{
		this.Features.Changed += (_, _) => this.QueueFeatures();
		this.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(this.Workspace))
			{
				this.OnPropertyChanged(nameof(this.IsFeature));
				this.OnPropertyChanged(nameof(this.IsNeuronFeature));
				this.OnPropertyChanged(nameof(this.ShowsFeature));
				this.MarkFeatures();
			}
			else if (e.PropertyName == nameof(this.Page))
			{
				this.SyncFeatures();
			}
		};
		this.SyncFeatures();
	}

	private void QueueFeatures()
	{
		if (this.featuresQueued)
			return;

		this.featuresQueued = true;
		Dispatcher.UIThread.Post(() =>
		{
			this.featuresQueued = false;
			this.SyncFeatures();
		});
	}

	/// <summary>Brings the feature places and the page's feature tabs up to date. UI thread.</summary>
	private void SyncFeatures()
	{
		List<FeaturePlace> places = [.. this.Features.Places()];
		if (!places.Select(p => (p.Key, p.Title)).SequenceEqual(this.FeaturePlaces.Select(p => (p.Key, p.Title))))
		{
			this.FeaturePlaces.Clear();
			foreach (FeaturePlace p in places)
				this.FeaturePlaces.Add(new FeaturePlaceItem(p.Key, p.Title, Icon(p.IconKey)));
		}

		List<FeatureTab> tabs = this.Page is NeuronPageViewModel page ? [.. this.Features.NeuronTabs(page.AccountId, page.BareJid)] : [];
		if (!tabs.Select(t => (t.Key, t.Title)).SequenceEqual(this.PageFeatureTabs.Select(t => (t.Key, t.Title))))
		{
			this.PageFeatureTabs.Clear();
			foreach (FeatureTab t in tabs)
				this.PageFeatureTabs.Add(new FeatureTabItem(t.Key, t.Title));
		}

		// What is on screen went away: back to where it belongs.
		if (this.IsFeature && !places.Any(p => p.Key == this.featurePlaceKey))
			this.ShowFleet();
		else if (this.IsNeuronFeature && !tabs.Any(t => t.Key == this.featureTabKey))
			this.ShowPageTab(WorkspaceKind.Neuron);

		this.MarkFeatures();
	}

	private void MarkFeatures()
	{
		foreach (FeaturePlaceItem p in this.FeaturePlaces)
			p.IsCurrent = this.IsFeature && p.Key == this.featurePlaceKey;
		foreach (FeatureTabItem t in this.PageFeatureTabs)
			t.IsCurrent = this.IsNeuronFeature && t.Key == this.featureTabKey;
		this.OnPropertyChanged(nameof(this.WorkspaceTitle));
	}

	private static Geometry? Icon(string key)
		=> Avalonia.Application.Current is Avalonia.Application app && Avalonia.Controls.ResourceNodeExtensions.TryFindResource(app, "Icon." + key, out object? g) ? g as Geometry : null;

	/// <summary>Shows a place a feature adds to the rail. False when no feature adds it (any more).</summary>
	[RelayCommand]
	private void ShowFeaturePlace(string? key) => this.OpenFeaturePlace(key);

	private bool OpenFeaturePlace(string? key)
	{
		FeaturePlace? place = key is null ? null : this.Features.Places().FirstOrDefault(p => p.Key == key);
		if (place is null)
			return false;

		if (!this.featureContents.TryGetValue("place|" + place.Key, out object? content))
			this.featureContents["place|" + place.Key] = content = place.Content();

		this.featurePlaceKey = place.Key;
		this.FeatureContent = content;
		if (this.Workspace == WorkspaceKind.Feature)
			this.MarkFeatures();
		this.Workspace = WorkspaceKind.Feature;
		this.NoteStop();
		return true;
	}

	/// <summary>Shows a tab a feature adds to the open Neuron's page.</summary>
	[RelayCommand]
	private void ShowFeatureTab(string? key) => this.OpenFeatureTab(key);

	private bool OpenFeatureTab(string? key)
	{
		if (this.Page is not NeuronPageViewModel page || key is null)
			return false;

		FeatureTab? tab = this.Features.NeuronTabs(page.AccountId, page.BareJid).FirstOrDefault(t => t.Key == key);
		if (tab is null)
			return false;

		string contentKey = "tab|" + PageKey(page.AccountId, page.BareJid) + "|" + tab.Key;
		if (!this.featureContents.TryGetValue(contentKey, out object? content))
			this.featureContents[contentKey] = content = tab.Content();

		this.featureTabKey = tab.Key;
		this.FeatureContent = content;
		if (this.Workspace == WorkspaceKind.NeuronFeature)
			this.MarkFeatures();
		this.Workspace = WorkspaceKind.NeuronFeature;
		this.NoteStop();
		return true;
	}

	/// <summary>Goes where something a feature added leads: a tab on a Neuron's page, or a place in the rail.</summary>
	public void OpenFeature(FeatureTarget target)
	{
		if (target.NeuronJid is string jid)
		{
			this.OpenNeuron(jid, target.AccountId);
			if (!this.OpenFeatureTab(target.ViewKey))
				Ui.Say("Not on this Neuron");
			return;
		}

		if (!this.OpenFeaturePlace(target.ViewKey))
			Ui.Say("Not there any more");
	}

	/// <summary>Lines for the search box from the features.</summary>
	private IEnumerable<SearchItem> FeatureSearchItems()
	{
		foreach (FeaturePlaceItem place in this.FeaturePlaces.ToList())
		{
			string key = place.Key;
			yield return new SearchItem(place.Title, string.Empty, place.Icon, () => this.OpenFeaturePlace(key));
		}

		foreach (FeatureSearchHit hit in this.Features.SearchHits())
		{
			FeatureTarget target = hit.Opens;
			yield return new SearchItem(hit.Title, hit.Hint, null, () => this.OpenFeature(target)) { Kind = hit.Kind, Words = hit.Words };
		}
	}
}
