using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Engine.Alerts;

namespace Neuro.Nat.App.Watch;

/// <summary>
/// Keeps NAT watching when its window is closed, and makes sure you notice what arrives: a
/// pop-up in the corner of the window while it is in front, the same pop-up on the desktop when
/// it is not, a sound, a flashing taskbar button, and a tray icon that carries the number of
/// things that wait for you. The tray menu is short: do not disturb, open, quit. Everything
/// else is in Settings.
/// </summary>
public sealed class WatchAgent : IDisposable
{
	private const int MaxToasts = 3;

	private readonly IClassicDesktopStyleApplicationLifetime desktop;
	private readonly Window window;
	private readonly MainWindowViewModel vm;
	private readonly AlertService alerts;
	private readonly TrayIcon tray;
	private readonly List<ToastWindow> toasts = [];
	private readonly DispatcherTimer refresh;
	private readonly IAppUpdates? updates;
	private NativeMenu? menu;
	private NativeMenuItem? quietItem;
	private NativeMenuItem? updateItem;
	private IconState? shownState;
	private bool updateArranged;
	private bool quitting;

	public WatchAgent(IClassicDesktopStyleApplicationLifetime desktop, Window window, MainWindowViewModel vm, AlertService alerts, IAppUpdates? updates = null)
	{
		this.desktop = desktop;
		this.window = window;
		this.vm = vm;
		this.alerts = alerts;
		this.updates = updates;

		desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		window.Icon = AppIcon.Get(IconState.NotConnected);
		window.Closing += this.Window_Closing;

		// A conversation only counts as read while the window is in front.
		vm.IsWindowActive = window.IsActive;
		window.Activated += (_, _) => vm.IsWindowActive = true;
		window.Deactivated += (_, _) => vm.IsWindowActive = false;
		vm.BringForward = this.ShowWindow;
		vm.Settings.Changed += (_, _) => this.UpdateTray();

		this.tray = new TrayIcon
		{
			Icon = AppIcon.Get(IconState.NotConnected, tray: true),
			ToolTipText = "NAT",
			Menu = this.BuildMenu(),
			IsVisible = true,
		};
		this.tray.Clicked += (_, _) => this.ShowWindow();
		TrayIcon.SetIcons(Application.Current!, [this.tray]);

		alerts.Raised += (_, a) => Dispatcher.UIThread.Post(() => this.Show(a));

		this.refresh = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => this.UpdateTray());
		this.refresh.Start();

		if (updates is not null)
		{
			updates.Ready += (_, _) => Dispatcher.UIThread.Post(this.OfferUpdate);
			if (updates.ReadyVersion is not null)
				this.OfferUpdate();
		}
	}

	// ---- The window ------------------------------------------------------------------------

	private void Window_Closing(object? sender, WindowClosingEventArgs e)
	{
		if (this.quitting || !this.alerts.Settings.KeepRunningInTray)
		{
			this.Quit();
			return;
		}

		// Keep watching: hide instead of closing.
		e.Cancel = true;
		this.window.Hide();
		this.vm.IsWindowActive = false;
	}

	public void ShowWindow()
	{
		// Started in the tray, the window becomes the main window the first time it is shown.
		this.desktop.MainWindow ??= this.window;
		this.window.Show();
		if (this.window.WindowState == WindowState.Minimized)
			this.window.WindowState = WindowState.Normal;
		this.window.Activate();
	}

	private void Quit()
	{
		if (this.quitting && this.desktop.MainWindow is null)
			return;

		this.quitting = true;

		// A downloaded update is installed quietly once NAT has quit.
		if (!this.updateArranged && this.updates?.ReadyVersion is not null)
			this.ArrangeUpdate(restart: false);

		foreach (ToastWindow t in this.toasts.ToList())
			t.Close();
		this.tray.IsVisible = false;
		this.desktop.Shutdown();
	}

	// ---- The tray --------------------------------------------------------------------------

	private NativeMenu BuildMenu()
	{
		NativeMenu menu = [];

		NativeMenuItem open = new("Open NAT");
		open.Click += (_, _) => this.ShowWindow();
		menu.Add(open);

		this.quietItem = new NativeMenuItem("Do not disturb") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = this.vm.Settings.IsDoNotDisturb };
		this.quietItem.Click += (_, _) => this.vm.Settings.SetStatus(this.vm.Settings.IsDoNotDisturb ? OwnStatus.Available : OwnStatus.DoNotDisturb);
		menu.Add(this.quietItem);

		menu.Add(new NativeMenuItemSeparator());
		NativeMenuItem quit = new("Quit");
		quit.Click += (_, _) => this.Quit();
		menu.Add(quit);
		this.menu = menu;
		return menu;
	}

	/// <summary>An update has been downloaded: offer to restart into it, at the top of the menu.</summary>
	private void OfferUpdate()
	{
		if (this.menu is null || this.updates?.ReadyVersion is not string version || this.quitting)
			return;

		if (this.updateItem is null)
		{
			this.updateItem = new NativeMenuItem();
			this.updateItem.Click += (_, _) =>
			{
				this.ArrangeUpdate(restart: true);
				this.Quit();
			};
			this.menu.Items.Insert(0, this.updateItem);
			this.menu.Items.Insert(1, new NativeMenuItemSeparator());
		}
		this.updateItem.Header = $"Restart to update to {version}";
	}

	private void ArrangeUpdate(bool restart)
	{
		try
		{
			this.updates?.InstallWhenQuit(restart);
			this.updateArranged = true;
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not arrange for the update to be installed.", ex);
		}
	}

	/// <summary>
	/// The dot of the tray icon and of every window's icon (the taskbar button) shows the state of
	/// the Neurons; the tooltip says it in words, so colour is never the only signal.
	/// </summary>
	private void UpdateTray()
	{
		int waiting = this.vm.Inbox.WaitingCount;
		int down = this.vm.Fleet.Rows.Count(r => r.State is Engine.Events.WatchState.Offline or Engine.Events.WatchState.Failed);
		bool connected = this.vm.Accounts.Any(a => a.IsOnline);
		bool quiet = this.vm.Settings.IsDoNotDisturb;

		IconState state = IconArt.StateFor(connected, down, waiting);
		if (state != this.shownState)
		{
			this.shownState = state;
			this.tray.Icon = AppIcon.Get(state, tray: true);

			WindowIcon icon = AppIcon.Get(state);
			this.window.Icon = icon;
			foreach (Window w in Views.ShellWindows.Extras)
				w.Icon = icon;
			if (Views.ShellWindows.Wall is Window wall)
				wall.Icon = icon;
		}

		List<string> parts = [];
		if (!connected)
			parts.Add(this.vm.Accounts.Count == 0 ? "no accounts yet" : "not connected");
		if (waiting > 0)
			parts.Add(waiting == 1 ? "1 thing waits for you" : $"{waiting} things wait for you");
		if (down > 0)
			parts.Add($"{down} Neuron{(down == 1 ? "" : "s")} down");
		if (quiet)
			parts.Add("do not disturb is on");
		this.tray.ToolTipText = parts.Count == 0 ? "NAT: all quiet" : "NAT: " + string.Join(", ", parts);

		if (this.quietItem is not null)
			this.quietItem.IsChecked = quiet;
	}

	// ---- Alerts ----------------------------------------------------------------------------

	private void Show(Alert a)
	{
		bool inFront = this.window.IsVisible && this.window.IsActive && this.window.WindowState != WindowState.Minimized;
		this.vm.IsWindowActive = inFront;

		// In the conversation the message is about: it appears there, and that is all.
		if (this.vm.IsLookingAt(a))
			return;

		if (this.alerts.Settings.WantsSound(a))
			AlertSound.Play(a);

		// The list in front of you already shows it.
		if (this.vm.ShowsItself(a))
			return;

		NoticeViewModel notice = this.vm.Notice(a, out bool isNew);
		if (!isNew)
			return;

		if (inFront)
		{
			this.vm.ShowInApp(notice);
			return;
		}

		// Behind other windows: the taskbar button flashes until you come back.
		if (this.window.IsVisible)
			TaskbarFlash.Start(this.window);

		while (this.toasts.Count >= MaxToasts)
			this.toasts[0].Close();

		ToastWindow t = new(notice);
		t.Closed += (_, _) =>
		{
			this.toasts.Remove(t);
			this.Arrange();
		};
		this.toasts.Add(t);
		t.Opened += (_, _) => this.Arrange();
		t.SizeChanged += (_, _) => this.Arrange();
		t.Show();
		this.Arrange();
	}

	/// <summary>Stacks the pop-ups up from the bottom right corner of the main screen's work area.</summary>
	private void Arrange()
	{
		Avalonia.Platform.Screen? screen = this.window.Screens.Primary ?? this.window.Screens.All.FirstOrDefault();
		if (screen is null)
			return;

		PixelRect area = screen.WorkingArea;
		double scale = screen.Scaling;
		int y = area.Bottom - (int)(4 * scale);
		foreach (ToastWindow t in this.toasts)
		{
			int h = (int)((t.Bounds.Height > 0 ? t.Bounds.Height : 110) * scale);
			int w = (int)(t.Width * scale);
			y -= h;
			t.Position = new PixelPoint(area.Right - w - (int)(6 * scale), y);
		}
	}

	public void Dispose()
	{
		this.refresh.Stop();
		this.tray.Dispose();
	}
}
