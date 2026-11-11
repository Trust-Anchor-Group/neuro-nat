using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Neuro.AA.App.ViewModels;

namespace Neuro.AA.App.Views;

/// <summary>
/// The windows beside the main one: any screen in a window of its own, and the Wall view.
/// Each of these windows is a whole shell, so everything works there as it does in the main
/// window. Where the windows were, how large, and what each showed is remembered, and they
/// come back at start.
/// </summary>
public static class ShellWindows
{
	private static readonly List<MainWindow> extras = [];
	private static readonly Stack<MainWindowViewModel> spare = new();
	private static readonly Dictionary<Window, Rect> normal = [];
	private static MainWindow? main;
	private static MainWindowViewModel? mainVm;
	private static Func<MainWindowViewModel>? make;
	private static Window? wall;
	private static bool quitting;
	private static bool restored;

	/// <summary>The windows open beside the main one.</summary>
	public static IReadOnlyList<MainWindow> Extras => extras;

	/// <summary>The Wall view's window, while it is open.</summary>
	public static Window? Wall => wall;

	/// <summary>
	/// Plugs the windows in. <paramref name="makeShell"/> makes the view model of one more
	/// window over the same accounts. Call it once Settings has read what was remembered.
	/// </summary>
	public static void Attach(MainWindow window, MainWindowViewModel vm, Func<MainWindowViewModel> makeShell)
	{
		main = window;
		mainVm = vm;
		make = makeShell;
		quitting = false;
		restored = false;
		extras.Clear();
		spare.Clear();
		normal.Clear();

		Ui.NewWindow = address => Open(address, null);
		Ui.OpenWall = ToggleWall;
		vm.IsLookedAtElsewhere = alert => extras.Any(w => w.DataContext is MainWindowViewModel x && x.LooksAt(alert));
		vm.IsAnotherWindowInFront = () => extras.Any(w => w.IsActive);

		Place(window, vm.Settings.MainWindow);
		Follow(window);

		// The other windows come back when the main one first shows: not while it waits in the tray.
		window.Opened += (_, _) => Restore();
		window.Closing += (_, _) => Save();
		window.Closed += (_, _) =>
		{
			quitting = true;
			foreach (Window w in extras.ToList())
				w.Close();
			wall?.Close();
		};

		if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.ShutdownRequested += (_, _) =>
			{
				Save();
				quitting = true;
			};
		}
	}

	/// <summary>Opens the windows that were open last time. Once.</summary>
	public static void Restore()
	{
		if (restored || mainVm is null)
			return;

		restored = true;
		foreach (WindowPlace place in mainVm.Settings.MoreWindows.Take(8))
			Open(place.Showing, place);
	}

	/// <summary>Opens a screen in a window of its own, and returns the window.</summary>
	public static MainWindow? Open(string? address, WindowPlace? place)
	{
		if (main is null || make is null)
			return null;

		// A window that was closed left its view model behind: it follows the accounts for good, so it is used again.
		MainWindowViewModel vm = spare.Count > 0 ? spare.Pop() : make();
		vm.Join();
		vm.GoTo(address);
		vm.ForgetHistory();

		MainWindow window = new() { DataContext = vm, Icon = main.Icon };
		if (place is not null)
			Place(window, place);
		else
		{
			// Beside the window it was opened from, a little down and to the right, and not larger than it.
			Window from = extras.FirstOrDefault(w => w.IsActive) ?? (Window)main;
			if (from.WindowState == WindowState.Normal && from.IsVisible)
			{
				window.Width = Math.Max(window.MinWidth, Math.Min(from.Width, 1280));
				window.Height = Math.Max(window.MinHeight, Math.Min(from.Height, 820));
				window.WindowStartupLocation = WindowStartupLocation.Manual;
				window.Position = new PixelPoint(from.Position.X + 36, from.Position.Y + 36);
			}
		}

		Follow(window);
		vm.IsWindowActive = false;
		window.Activated += (_, _) => vm.IsWindowActive = true;
		window.Deactivated += (_, _) => vm.IsWindowActive = false;
		window.Closed += (_, _) =>
		{
			if (!quitting)
			{
				extras.Remove(window);
				normal.Remove(window);
				Save();
			}

			vm.IsWindowActive = false;
			spare.Push(vm);
			_ = LeaveAsync(vm);
		};

		extras.Add(window);
		window.Show();
		if (place is null)
			Save();
		return window;
	}

	/// <summary>Every window's view model beside the main one's, open or closed. The app finishes them as it quits.</summary>
	public static IEnumerable<MainWindowViewModel> Others => [.. extras.Select(w => w.DataContext).OfType<MainWindowViewModel>(), .. spare];

	/// <summary>What a window was in the middle of is finished as it closes: an answer that could still be taken back, and traffic it was watching.</summary>
	public static async Task LeaveAsync(MainWindowViewModel vm)
	{
		try
		{
			await vm.Inbox.CommitAsync();
			await vm.Sources.StopSniffersAsync();
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not finish what a closed window was doing.", ex);
		}
	}

	/// <summary>F11: opens the Wall view, or closes it when it is open.</summary>
	public static void ToggleWall()
	{
		if (wall is not null)
		{
			wall.Close();
			return;
		}

		if (main is null || mainVm is null)
			return;

		WallViewModel vm = mainVm.Wall;
		Window window = new()
		{
			Title = "Wall · Neuro.AA",
			Icon = main.Icon,
			Width = 1280,
			Height = 720,
			MinWidth = 640,
			MinHeight = 400,
			Content = new WallView { DataContext = vm },
		};

		// On the screen the app is on, all of it. A wall has no title bar and no controls.
		Window from = extras.FirstOrDefault(w => w.IsActive) ?? (Window)main;
		if (from.IsVisible)
		{
			window.WindowStartupLocation = WindowStartupLocation.Manual;
			window.Position = from.Position;
		}

		window.WindowState = WindowState.FullScreen;
		window.KeyDown += (_, e) =>
		{
			if (e.Key is Key.Escape or Key.F11)
			{
				window.Close();
				e.Handled = true;
			}
		};
		window.Opened += (_, _) => vm.Start();
		window.Closed += (_, _) =>
		{
			vm.Stop();
			wall = null;
		};

		wall = window;
		window.Show();
	}

	/// <summary>Writes down where the windows are and what the extra ones show.</summary>
	public static void Save()
	{
		if (main is null || mainVm is null || quitting)
			return;

		mainVm.Settings.RememberWindows(PlaceOf(main, null), [.. extras.Select(w => PlaceOf(w, (w.DataContext as MainWindowViewModel)?.Address))]);
	}

	// ----- Where a window is -----

	/// <summary>Keeps track of where a window is while it is neither maximized nor minimized: that is what comes back.</summary>
	private static void Follow(Window window)
	{
		void Note()
		{
			if (window.WindowState == WindowState.Normal && window.IsVisible && window.Bounds.Width > 0)
				normal[window] = new Rect(window.Position.X, window.Position.Y, window.Bounds.Width, window.Bounds.Height);
		}

		window.Opened += (_, _) => Note();
		window.PositionChanged += (_, _) => Note();
		window.SizeChanged += (_, _) => Note();
	}

	private static WindowPlace? PlaceOf(Window window, string? showing)
	{
		if (!normal.TryGetValue(window, out Rect at))
		{
			// Never seen at its normal size: only what it showed is worth keeping.
			return showing is null ? null : new WindowPlace { Width = window.Width, Height = window.Height, X = int.MinValue, Showing = showing };
		}

		return new WindowPlace
		{
			X = (int)at.X,
			Y = (int)at.Y,
			Width = Math.Round(at.Width),
			Height = Math.Round(at.Height),
			Maximized = window.WindowState == WindowState.Maximized,
			Showing = showing,
		};
	}

	/// <summary>Puts a window where it was, when that is still on a screen. A screen that was unplugged does not take the window with it.</summary>
	public static void Place(Window window, WindowPlace? place)
	{
		if (place is null)
			return;

		if (place.Width >= window.MinWidth && place.Height >= window.MinHeight && double.IsFinite(place.Width) && double.IsFinite(place.Height))
		{
			window.Width = place.Width;
			window.Height = place.Height;
		}

		if (place.X != int.MinValue && IsOnAScreen(window, place))
		{
			window.WindowStartupLocation = WindowStartupLocation.Manual;
			window.Position = new PixelPoint(place.X, place.Y);
		}

		if (place.Maximized)
			window.WindowState = WindowState.Maximized;
	}

	/// <summary>True when enough of the window's top edge is on a screen to take hold of it.</summary>
	private static bool IsOnAScreen(Window window, WindowPlace place)
	{
		IReadOnlyList<Screen>? screens = window.Screens?.All;
		if (screens is null || screens.Count == 0)
			return false;

		PixelRect grip = new(place.X + 40, place.Y, Math.Max(1, (int)place.Width - 80), 30);
		foreach (Screen screen in screens)
		{
			PixelRect seen = screen.WorkingArea.Intersect(grip);
			if (seen.Width >= 120 && seen.Height >= 20)
				return true;
		}

		return false;
	}
}
