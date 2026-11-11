using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Neuro.AA.App.ViewModels;
using Neuro.AA.Engine.Alerts;

namespace Neuro.AA.App.Watch;

/// <summary>
/// Keeps Neuro.AA watching when its window is closed: a tray icon that shows when something needs
/// you, a menu to open the window, snooze alerts, choose alerts and quit, and alerts in the corner
/// of the screen that open the right place when clicked.
/// </summary>
public sealed class WatchAgent : IDisposable
{
	private const int MaxToasts = 3;

	private readonly IClassicDesktopStyleApplicationLifetime desktop;
	private readonly Window window;
	private readonly MainWindowViewModel vm;
	private readonly AlertService alerts;
	private readonly JsonAlertSettingsStore store;
	private readonly TrayIcon tray;
	private readonly List<ToastWindow> toasts = [];
	private readonly List<Alert> waiting = [];
	private readonly DispatcherTimer refresh;
	private readonly IAutostart? autostart;
	private readonly IAppUpdates? updates;
	private NativeMenu? menu;
	private NativeMenuItem? snoozeItem;
	private NativeMenuItem? updateItem;
	private bool updateArranged;
	private bool quitting;

	public WatchAgent(IClassicDesktopStyleApplicationLifetime desktop, Window window, MainWindowViewModel vm, AlertService alerts, JsonAlertSettingsStore store,
		IAutostart? autostart = null, IAppUpdates? updates = null)
	{
		this.desktop = desktop;
		this.window = window;
		this.vm = vm;
		this.alerts = alerts;
		this.store = store;
		this.autostart = autostart;
		this.updates = updates;

		desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		window.Icon = AppIcon.Get();
		window.Closing += this.Window_Closing;

		this.tray = new TrayIcon
		{
			Icon = AppIcon.Get(),
			ToolTipText = "Neuro.AA",
			Menu = this.BuildMenu(),
			IsVisible = true,
		};
		this.tray.Clicked += (_, _) => this.ShowWindow();
		TrayIcon.SetIcons(Application.Current!, [this.tray]);

		alerts.Raised += (_, a) => Dispatcher.UIThread.Post(() => this.Show(a));
		alerts.SnoozeChanged += (_, _) => Dispatcher.UIThread.Post(this.UpdateTray);

		this.refresh = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => this.UpdateTray());
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

		// A downloaded update is installed quietly once Neuro.AA has quit.
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

		NativeMenuItem open = new("Open Neuro.AA");
		open.Click += (_, _) => this.ShowWindow();
		menu.Add(open);
		menu.Add(new NativeMenuItemSeparator());

		this.snoozeItem = new NativeMenuItem("Snooze alerts for an hour");
		this.snoozeItem.Click += (_, _) =>
		{
			if (this.alerts.IsSnoozed)
				this.alerts.Resume();
			else
				this.alerts.Snooze(TimeSpan.FromHours(1));
		};
		menu.Add(this.snoozeItem);

		NativeMenuItem which = new("Alert me about") { Menu = [] };
		which.Menu.Add(this.Toggle("Notifications that need someone", s => s.Notifications, (s, v) => s with { Notifications = v }));
		which.Menu.Add(this.Toggle("New and returning errors", s => s.Errors, (s, v) => s with { Errors = v }));
		which.Menu.Add(this.Toggle("Neurons going down", s => s.NeuronDown, (s, v) => s with { NeuronDown = v }));
		which.Menu.Add(this.Toggle("Contact requests", s => s.ContactRequests, (s, v) => s with { ContactRequests = v }));
		which.Menu.Add(this.Toggle("Information too (updates, restarts)", s => s.InformationalNotifications, (s, v) => s with { InformationalNotifications = v }));
		menu.Add(which);

		NativeMenuItem sounds = new("Play a sound for") { Menu = [] };
		sounds.Menu.Add(this.Toggle("Errors and Neurons going down", s => s.SoundForErrors, (s, v) => s with { SoundForErrors = v }));
		sounds.Menu.Add(this.Toggle("Notifications that need someone", s => s.SoundForNotifications, (s, v) => s with { SoundForNotifications = v }));
		sounds.Menu.Add(this.Toggle("Contact requests", s => s.SoundForContactRequests, (s, v) => s with { SoundForContactRequests = v }));
		sounds.Menu.Add(this.Toggle("Information too", s => s.SoundForInformation, (s, v) => s with { SoundForInformation = v }));
		menu.Add(sounds);
		menu.Add(this.Toggle("Keep watching when the window closes", s => s.KeepRunningInTray, (s, v) => s with { KeepRunningInTray = v }));
		if (this.autostart is not null)
			menu.Add(this.AutostartToggle(this.autostart));

		menu.Add(new NativeMenuItemSeparator());
		NativeMenuItem quit = new("Quit");
		quit.Click += (_, _) => this.Quit();
		menu.Add(quit);
		this.menu = menu;
		return menu;
	}

	private NativeMenuItem AutostartToggle(IAutostart autostart)
	{
		NativeMenuItem item = new("Start with Windows") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = autostart.IsEnabled };
		item.Click += (_, _) =>
		{
			try
			{
				autostart.SetEnabled(!autostart.IsEnabled);
			}
			catch (Exception ex)
			{
				AppLog.Error("Could not change whether Neuro.AA starts with Windows.", ex);
			}
			item.IsChecked = autostart.IsEnabled;
		};
		return item;
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

	private NativeMenuItem Toggle(string header, Func<AlertSettings, bool> get, Func<AlertSettings, bool, AlertSettings> set)
	{
		NativeMenuItem item = new(header) { ToggleType = MenuItemToggleType.CheckBox, IsChecked = get(this.alerts.Settings) };
		item.Click += (_, _) =>
		{
			AlertSettings s = set(this.alerts.Settings, !get(this.alerts.Settings));
			this.alerts.Settings = s;
			item.IsChecked = get(s);
			try
			{
				this.store.Save(s);
			}
			catch (Exception ex)
			{
				AppLog.Error("Could not save the alert settings.", ex);
			}
		};
		return item;
	}

	/// <summary>The icon shows a dot when something needs you; the tooltip says what.</summary>
	private void UpdateTray()
	{
		int open = this.vm.Attention.OpenCount;
		int questions = this.vm.Devices.QuestionCount + this.vm.Identities.PetitionCount + this.vm.Chat.Requests.Count;
		int down = this.vm.Fleet.Rows.Count(r => r.State is Engine.Events.WatchState.Offline or Engine.Events.WatchState.Failed);
		bool attention = open + questions + down > 0;
		bool snoozed = this.alerts.IsSnoozed;

		this.tray.Icon = AppIcon.Get(attention, snoozed);

		List<string> parts = [];
		if (open > 0)
			parts.Add($"{open} need{(open == 1 ? "s" : "")} you");
		if (questions > 0)
			parts.Add($"{questions} question{(questions == 1 ? "" : "s")}");
		if (down > 0)
			parts.Add($"{down} Neuron{(down == 1 ? "" : "s")} down");
		if (snoozed && this.alerts.SnoozedUntil is DateTime until)
			parts.Add($"alerts snoozed until {until:HH:mm}");
		this.tray.ToolTipText = parts.Count == 0 ? "Neuro.AA · all quiet" : "Neuro.AA · " + string.Join(", ", parts);

		if (this.snoozeItem is not null)
			this.snoozeItem.Header = snoozed ? "Resume alerts" : "Snooze alerts for an hour";
	}

	// ---- Alerts ----------------------------------------------------------------------------

	private void Show(Alert a)
	{
		if (this.alerts.Settings.WantsSound(a))
			AlertSound.Play(a);

		// While the window is in front, the app itself shows what changed; errors, Neurons going
		// down and contact requests still interrupt then.
		if (this.window.IsActive && a.Kind is AlertKind.Notification or AlertKind.NeuronBack)
			return;

		if (this.toasts.Count >= MaxToasts)
		{
			this.waiting.Add(a);
			return;
		}

		ToastWindow t = new(a, 0, () => this.Open(a));
		t.Closed += (_, _) =>
		{
			this.toasts.Remove(t);
			this.Arrange();
			if (this.waiting.Count > 0)
			{
				// Several waited: show the newest, and say how many more there were.
				Alert next = this.waiting[^1];
				int more = this.waiting.Count - 1;
				this.waiting.Clear();
				ToastWindow n = new(next, more, () => this.Open(next));
				this.Add(n);
			}
		};
		this.Add(t);
	}

	private void Add(ToastWindow t)
	{
		this.toasts.Add(t);
		t.Opened += (_, _) => this.Arrange();
		t.Show();
		this.Arrange();
	}

	/// <summary>Stacks the alerts up from the bottom right corner of the main screen's work area.</summary>
	private void Arrange()
	{
		Avalonia.Platform.Screen? screen = this.window.Screens.Primary ?? this.window.Screens.All.FirstOrDefault();
		if (screen is null)
			return;

		PixelRect area = screen.WorkingArea;
		double scale = screen.Scaling;
		int y = area.Bottom - (int)(8 * scale);
		foreach (ToastWindow t in this.toasts)
		{
			int h = (int)((t.Bounds.Height > 0 ? t.Bounds.Height : 110) * scale);
			int w = (int)(t.Width * scale);
			y -= h;
			t.Position = new PixelPoint(area.Right - w - (int)(8 * scale), y);
		}
	}

	private void Open(Alert a)
	{
		this.ShowWindow();
		this.vm.OpenAlert(a);
	}

	public void Dispose()
	{
		this.refresh.Stop();
		this.tray.Dispose();
	}
}
