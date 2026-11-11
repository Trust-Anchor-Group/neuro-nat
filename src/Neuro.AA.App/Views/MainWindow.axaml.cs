using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Neuro.AA.App.ViewModels;

namespace Neuro.AA.App.Views;

public partial class MainWindow : Window
{
	/// <summary>
	/// Width steps the views lay out for. The root gets a class "maxwN" for each step the window
	/// is at or below, and views style themselves with ".maxwN Selector". (Container queries did
	/// the same, but cost every list row a width subscription, which made scrolling slow.)
	/// </summary>
	private static readonly int[] steps = [780, 860, 900, 960, 1000, 1100, 1180, 1280];

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
	}
}
