using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Neuro.Nat.App.ViewModels;

/// <summary>One page of the big screen that can be turned off in Settings.</summary>
public sealed partial class BigScreenPageChoice(SettingsViewModel settings, string key, string title) : ObservableObject
{
	public string Key { get; } = key;
	public string Title { get; } = title;

	public bool IsOn
	{
		get => settings.IsBigScreenPageOn(this.Key);
		set
		{
			settings.SetBigScreenPage(this.Key, value);
			this.OnPropertyChanged();
		}
	}
}

/// <summary>The part of Settings about the big screen (F11): which pages it shows and how long each stays.</summary>
public sealed partial class SettingsViewModel
{
	/// <summary>How long a page can stay, in seconds.</summary>
	public static IReadOnlyList<int> BigScreenTimes { get; } = [10, 20, 30, 60];

	private List<BigScreenPageChoice>? bigScreenChoices;

	/// <summary>Seconds on each page: one of <see cref="BigScreenTimes"/>.</summary>
	public int BigScreenSeconds => BigScreenTimes.Contains(this.ui.BigScreenSeconds) ? this.ui.BigScreenSeconds : 20;

	public bool IsBigScreen10 => this.BigScreenSeconds == 10;
	public bool IsBigScreen20 => this.BigScreenSeconds == 20;
	public bool IsBigScreen30 => this.BigScreenSeconds == 30;
	public bool IsBigScreen60 => this.BigScreenSeconds == 60;

	[RelayCommand]
	public void SetBigScreenSeconds(int seconds)
	{
		if (!BigScreenTimes.Contains(seconds) || seconds == this.BigScreenSeconds)
			return;

		this.ui = this.ui with { BigScreenSeconds = seconds };
		this.SaveUi("how long the big screen stays on a page");
		this.RaiseAll();
	}

	/// <summary>The pages that can be turned off: every page but Neurons, which is always there.</summary>
	public IReadOnlyList<BigScreenPageChoice> BigScreenChoices
	{
		get
		{
			List<(string Key, string Title)> pages = [(BigScreenPages.Problems, "Problems"), .. BigScreenPages.Added.Select(p => (p.Key, p.Title))];
			if (this.bigScreenChoices is null || !this.bigScreenChoices.Select(c => c.Key).SequenceEqual(pages.Select(p => p.Key)))
				this.bigScreenChoices = [.. pages.Select(p => new BigScreenPageChoice(this, p.Key, p.Title))];

			return this.bigScreenChoices;
		}
	}

	/// <summary>True when the big screen shows the page. Neurons is always on.</summary>
	public bool IsBigScreenPageOn(string key)
		=> key == BigScreenPages.Neurons || !this.ui.BigScreenOff.Contains(key, StringComparer.Ordinal);

	public void SetBigScreenPage(string key, bool on)
	{
		if (key == BigScreenPages.Neurons || on == this.IsBigScreenPageOn(key))
			return;

		List<string> off = [.. this.ui.BigScreenOff.Where(k => k != key)];
		if (!on)
			off.Add(key);

		this.ui = this.ui with { BigScreenOff = off };
		this.SaveUi("the big screen's pages");
		this.RaiseAll();
	}
}
