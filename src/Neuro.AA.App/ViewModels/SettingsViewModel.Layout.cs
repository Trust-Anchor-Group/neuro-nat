using CommunityToolkit.Mvvm.Input;

namespace Neuro.AA.App.ViewModels;

/// <summary>Where a window was, how large, and what it showed.</summary>
public sealed record WindowPlace
{
	public int X { get; init; }
	public int Y { get; init; }
	public double Width { get; init; }
	public double Height { get; init; }
	public bool Maximized { get; init; }

	/// <summary>The screen it showed, as <see cref="MainWindowViewModel.Address"/> writes it.</summary>
	public string? Showing { get; init; }
}

/// <summary>Remembers how wide the panes were dragged. The panes ask for their width by a name.</summary>
public interface ILayoutStore
{
	double? Width(string key);

	/// <summary>Keeps a width, or forgets it when null so the pane goes back to its own.</summary>
	void SetWidth(string key, double? width);
}

/// <summary>
/// The part of Settings that is about sizes: how large the text is, how tall rows are, how wide
/// the panes were dragged, how the Neurons list is sorted, and where the windows were.
/// </summary>
public sealed partial class SettingsViewModel : ILayoutStore
{
	/// <summary>The text sizes on offer, in percent. Everything scales with the text, so nothing is cut off.</summary>
	public static IReadOnlyList<int> TextSizes { get; } = [90, 100, 115, 130];

	private void SaveUi(string what)
	{
		try
		{
			this.uiStore?.Save(this.ui);
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not save " + what + ".", ex);
		}
	}

	// ----- Text size -----

	/// <summary>The text size in percent: one of <see cref="TextSizes"/>.</summary>
	public int TextSize => TextSizes.Contains(this.ui.TextSize) ? this.ui.TextSize : 100;

	/// <summary>What the windows scale by: 1 for normal.</summary>
	public double Zoom => this.TextSize / 100.0;

	public bool IsTextSmall => this.TextSize == 90;
	public bool IsTextNormal => this.TextSize == 100;
	public bool IsTextLarge => this.TextSize == 115;
	public bool IsTextLarger => this.TextSize == 130;

	[RelayCommand]
	public void SetTextSize(int percent)
	{
		if (!TextSizes.Contains(percent) || percent == this.TextSize)
			return;

		this.ui = this.ui with { TextSize = percent };
		this.SaveUi("the text size");
		this.RaiseAll();
	}

	/// <summary>Ctrl+plus and Ctrl+minus: one size up or down. Ctrl+0 is <c>SetTextSize(100)</c>.</summary>
	public void StepTextSize(int direction)
	{
		List<int> sizes = [.. TextSizes];
		int at = Math.Clamp(sizes.IndexOf(this.TextSize) + Math.Sign(direction), 0, sizes.Count - 1);
		this.SetTextSize(sizes[at]);
		Ui.Say("Text size " + this.TextSize + " %");
	}

	// ----- Rows -----

	/// <summary>Rows in lists take less height, so more of them fit.</summary>
	public bool CompactRows
	{
		get => this.ui.CompactRows;
		set
		{
			if (this.ui.CompactRows == value)
				return;

			this.ui = this.ui with { CompactRows = value };
			this.SaveUi("the row height");
			this.RaiseAll();
		}
	}

	// ----- Motion -----

	/// <summary>
	/// Nothing fades or slides. Motion is also off when the system asks for it, whatever this says.
	/// </summary>
	public bool LessMotion
	{
		get => this.ui.LessMotion;
		set
		{
			if (this.ui.LessMotion == value)
				return;

			this.ui = this.ui with { LessMotion = value };
			this.SaveUi("the motion setting");
			this.RaiseAll();
		}
	}

	// ----- Panes -----

	public double? Width(string key) => this.ui.Widths.TryGetValue(key, out double w) ? w : null;

	public void SetWidth(string key, double? width)
	{
		Dictionary<string, double> widths = new(this.ui.Widths);
		if (width is double w)
			widths[key] = Math.Round(w);
		else if (!widths.Remove(key))
			return;

		this.ui = this.ui with { Widths = widths };
		this.SaveUi("the layout");
		this.OnPropertyChanged(nameof(this.HasOwnLayout));
	}

	/// <summary>True when any pane was dragged away from its own width.</summary>
	public bool HasOwnLayout => this.ui.Widths.Count > 0 || this.ui.NeuronSort is not null;

	/// <summary>Raised when the panes should go back to their own widths.</summary>
	public event EventHandler? LayoutReset;

	/// <summary>Every pane back to its own width, and the Neurons list back to its own order.</summary>
	[RelayCommand]
	private void ResetLayout()
	{
		this.ui = this.ui with { Widths = [], NeuronSort = null };
		this.SaveUi("the layout");
		this.LayoutReset?.Invoke(this, EventArgs.Empty);
		this.RaiseAll();
		Ui.Say("The layout is back to how it came");
	}

	// ----- The Neurons list -----

	/// <summary>How the Neurons list is sorted: a column's name, with "-" after it for largest first. Null for by name.</summary>
	public string? NeuronSort => this.ui.NeuronSort;

	public void RememberNeuronSort(string? sort)
	{
		if (this.ui.NeuronSort == sort)
			return;

		this.ui = this.ui with { NeuronSort = sort };
		this.SaveUi("how the Neurons are sorted");
		this.OnPropertyChanged(nameof(this.HasOwnLayout));
	}

	// ----- Windows -----

	/// <summary>True when Settings reads from and writes to a file, as in the app. Not in a preview.</summary>
	public bool HasStore => this.uiStore is not null;

	/// <summary>Where the main window was when it was last closed, and what it showed.</summary>
	public WindowPlace? MainWindow => this.ui.Window;

	/// <summary>The other windows that were open, and what each showed.</summary>
	public IReadOnlyList<WindowPlace> MoreWindows => this.ui.MoreWindows;

	public void RememberWindows(WindowPlace? main, IReadOnlyList<WindowPlace> more)
	{
		this.ui = this.ui with { Window = main ?? this.ui.Window, MoreWindows = [.. more] };
		this.SaveUi("where the windows were");
	}
}
