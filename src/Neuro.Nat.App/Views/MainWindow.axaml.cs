using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

public partial class MainWindow : Window
{
	/// <summary>
	/// Width steps the views lay out for. The root gets a class "maxwN" for each step the window
	/// is at or below, and views style themselves with ".maxwN Selector". (Container queries did
	/// the same, but cost every list row a width subscription, which made scrolling slow.)
	/// </summary>
	private static readonly int[] steps = [780, 860, 900, 960, 1000, 1100, 1180, 1280];

	/// <summary>From this width the layout is the large one.</summary>
	public const double WideFrom = 1800;

	public MainWindow()
	{
		this.InitializeComponent();
		this.Root.SizeChanged += (_, e) => this.ApplyWidth(e.NewSize.Width);

		// Search box: Ctrl+K from anywhere; Up, Down and Enter choose; Escape or clicking away closes.
		this.AddHandler(KeyDownEvent, this.Window_KeyDown, RoutingStrategies.Tunnel);
		this.AddHandler(PointerPressedEvent, this.Window_PointerPressed, RoutingStrategies.Tunnel);
		this.SearchBox.GotFocus += (_, _) => this.Search?.Open();
		this.SearchBox.LostFocus += (_, _) => this.Search?.Close();
		this.SearchBox.KeyDown += this.SearchBox_KeyDown;
		this.SearchResults.Tapped += (_, _) => this.RunSearch();
	}

	private SearchViewModel? Search => (this.DataContext as MainWindowViewModel)?.Search;

	private SettingsViewModel? settings;

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (this.settings is not null)
			this.settings.Changed -= this.Settings_Changed;

		this.settings = (this.DataContext as MainWindowViewModel)?.Settings;
		if (this.settings is not null)
			this.settings.Changed += this.Settings_Changed;

		this.ApplySizes();
	}

	private void Settings_Changed(object? sender, EventArgs e) => this.ApplySizes();

	/// <summary>Text size and row height, as Settings has them. The whole window scales, so nothing is cut off.</summary>
	private void ApplySizes()
	{
		double zoom = this.settings?.Zoom ?? 1;
		this.Zoom.LayoutTransform = zoom == 1 ? null : new Avalonia.Media.ScaleTransform(zoom, zoom);
		this.Root.Classes.Set("compact", this.settings?.CompactRows == true);
	}

	private void Window_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
		{
			this.SearchBox.Focus();
			this.SearchBox.SelectAll();
			e.Handled = true;
			return;
		}

		if (this.DataContext is not MainWindowViewModel vm)
			return;

		// Alt+Left and Alt+Right, and the keys some keyboards have for it: back and forward.
		if ((e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Left) || e.Key == Key.BrowserBack)
		{
			vm.GoBackCommand.Execute(null);
			e.Handled = true;
		}
		else if ((e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Right) || e.Key == Key.BrowserForward)
		{
			vm.GoForwardCommand.Execute(null);
			e.Handled = true;
		}

		// Ctrl+Z while a note offers Undo, and no text box has the keyboard: take it back.
		else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z && vm.HasFlashUndo && this.FocusManager?.GetFocusedElement() is not TextBox)
		{
			vm.UndoFlashCommand.Execute(null);
			e.Handled = true;
		}

		// Ctrl+Shift+N: what is showing, in a window of its own. F11: the Wall view.
		else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.N)
		{
			vm.NewWindowCommand.Execute(null);
			e.Handled = true;
		}
		else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F11)
		{
			vm.OpenWallCommand.Execute(null);
			e.Handled = true;
		}

		// Ctrl+plus, Ctrl+minus and Ctrl+0: the text size.
		else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract)
		{
			vm.Settings.StepTextSize(e.Key is Key.OemPlus or Key.Add ? 1 : -1);
			e.Handled = true;
		}
		else if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.D0 or Key.NumPad0)
		{
			vm.Settings.SetTextSize(100);
			e.Handled = true;
		}

		// Ctrl+1 to Ctrl+5: the five places, in the order of the rail.
		else if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.D1 and <= Key.D9)
		{
			e.Handled = vm.GoToPlace(e.Key - Key.D0);
		}
		else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.OemComma)
		{
			vm.ShowSettingsCommand.Execute(null);
			e.Handled = true;
		}
		else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F)
		{
			// Ctrl+F: the filter or find box of the screen in front.
			e.Handled = UiHost.FocusFind(this);
		}
		else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F5)
		{
			vm.RefreshCurrent();
			e.Handled = true;
		}
	}

	/// <summary>The back and forward buttons on the side of a mouse.</summary>
	private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (this.DataContext is not MainWindowViewModel vm)
			return;

		switch (e.GetCurrentPoint(this).Properties.PointerUpdateKind)
		{
			case PointerUpdateKind.XButton1Pressed:
				vm.GoBackCommand.Execute(null);
				e.Handled = true;
				break;

			case PointerUpdateKind.XButton2Pressed:
				vm.GoForwardCommand.Execute(null);
				e.Handled = true;
				break;
		}
	}

	private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
	{
		switch (e.Key)
		{
			case Key.Down:
				this.Search?.Move(1);
				e.Handled = true;
				break;

			case Key.Up:
				this.Search?.Move(-1);
				e.Handled = true;
				break;

			case Key.Enter:
				this.RunSearch();
				e.Handled = true;
				break;

			case Key.Escape:
				this.LeaveSearch();
				e.Handled = true;
				break;
		}
	}

	private void RunSearch()
	{
		this.Search?.Run();
		this.LeaveSearch();
	}

	/// <summary>Moves keyboard focus out of the search box, which also closes its list.</summary>
	private void LeaveSearch()
	{
		this.Search?.Close();
		this.Root.Focusable = true;
		this.Root.Focus();
		this.Root.Focusable = false;
	}

	private void ApplyWidth(double width)
	{
		foreach (int step in steps)
			this.Root.Classes.Set("maxw" + step, width <= step);

		// A very wide window: lists get some of the room instead of everything else stretching.
		this.Root.Classes.Set("wide", width >= WideFrom);
	}
}
