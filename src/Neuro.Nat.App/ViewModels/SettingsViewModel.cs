using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Alerts;
using Neuro.Nat.Engine.Chat;
using Waher.Networking.XMPP;

namespace Neuro.Nat.App.ViewModels;

/// <summary>What your contacts see, and whether pop-ups and sounds may reach you.</summary>
public enum OwnStatus
{
	Available,

	/// <summary>Contacts see you as away. Pop-ups and sounds still come.</summary>
	Away,

	/// <summary>Contacts see you as busy. Pop-ups and sounds wait; the counts keep running.</summary>
	DoNotDisturb,
}

/// <summary>Which look the app uses.</summary>
public enum LookChoice
{
	/// <summary>Follows the operating system.</summary>
	System,
	Light,
	Dark,
}

/// <summary>Choices about the app itself that are kept between runs.</summary>
public sealed record UiSettings
{
	public LookChoice Look { get; init; } = LookChoice.System;

	/// <summary>The one Neuron the screens were showing, by its address. Null for all of them.</summary>
	public string? Neuron { get; init; }

	/// <summary>Text size in percent: 90, 100, 115 or 130.</summary>
	public int TextSize { get; init; } = 100;

	public bool CompactRows { get; init; }

	/// <summary>Nothing fades or slides: things change at once.</summary>
	public bool LessMotion { get; init; }

	/// <summary>How wide each pane was dragged, by the pane's name.</summary>
	public Dictionary<string, double> Widths { get; init; } = [];

	/// <summary>The column the Neurons list is sorted by, with "-" after it for largest first.</summary>
	public string? NeuronSort { get; init; }

	/// <summary>Where the main window was.</summary>
	public WindowPlace? Window { get; init; }

	/// <summary>The other windows that were open.</summary>
	public List<WindowPlace> MoreWindows { get; init; } = [];
}

/// <summary>Keeps <see cref="UiSettings"/> between runs.</summary>
public interface IUiSettingsStore
{
	UiSettings Load();

	void Save(UiSettings settings);
}

/// <summary><see cref="UiSettings"/> in the database.</summary>
public sealed class DatabaseUiSettingsStore(Engine.Storage.DatabaseSettings settings) : IUiSettingsStore
{
	/// <summary>The settings group's name in the database.</summary>
	public const string Name = "ui";

	public UiSettings Load() => settings.Get<UiSettings>(Name) ?? new();

	public void Save(UiSettings value) => settings.Set(Name, value);
}

/// <summary><see cref="UiSettings"/> for this run only, where nothing can be kept.</summary>
public sealed class MemoryUiSettingsStore : IUiSettingsStore
{
	private UiSettings settings = new();

	public UiSettings Load() => this.settings;

	public void Save(UiSettings value) => this.settings = value;
}

/// <summary><see cref="UiSettings"/> in a JSON file: how they were kept before the database. Tests use it.</summary>
public sealed class JsonUiSettingsStore(string path) : IUiSettingsStore
{
	private static readonly JsonSerializerOptions options = new() { WriteIndented = true };

	public UiSettings Load()
	{
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path)) ?? new() : new();
		}
		catch (Exception)
		{
			return new();
		}
	}

	public void Save(UiSettings settings)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(settings, options));
		File.Move(tmp, path, true);
	}
}

/// <summary>
/// Settings: your status, the look, and every switch for pop-ups and sounds. The same object
/// backs the Settings screen, the status picker in the status strip and the tray menu, so a
/// change in one shows in the others.
/// </summary>
/// <summary>One line of the keyboard list in Settings.</summary>
public sealed record ShortcutRow(string Keys, string Does);

/// <summary>A group of shortcuts that belong together.</summary>
public sealed record ShortcutGroup(string Name, IReadOnlyList<ShortcutRow> Rows);

public sealed partial class SettingsViewModel(ChatService chat) : ObservableObject
{
	/// <summary>Said in Settings where there is no encrypted database to keep things in.</summary>
	public const string NothingKept = "On this computer NAT keeps nothing between runs yet: accounts, chats and settings are gone when it quits.";

	/// <summary>A line about what is kept, when it needs saying. Null when everything is kept.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasStorageNote))]
	private string? storageNote;

	public bool HasStorageNote => this.StorageNote is not null;

	/// <summary>The app's long name and version, at the bottom of Settings.</summary>
	public static string About { get; } = $"Neuro Administration Tool, version {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}";

	/// <summary>Every keyboard shortcut, as Settings lists them.</summary>
	public static IReadOnlyList<ShortcutGroup> Shortcuts { get; } =
	[
		new ShortcutGroup("Getting around",
		[
			new ShortcutRow("Ctrl+K", "Search for a screen, a Neuron, a person, a problem or something to do"),
			new ShortcutRow("Alt+Left, Alt+Right", "Back and forward through where you have been. The mouse's side buttons do the same."),
			new ShortcutRow("Ctrl+1 to Ctrl+5", "Inbox, Neurons, Events, Chat, Console"),
			new ShortcutRow("Ctrl+,", "Settings"),
			new ShortcutRow("Ctrl+F", "Filter or find on the screen in front"),
			new ShortcutRow("F5", "Read the screen again"),
			new ShortcutRow("Ctrl+Z", "Take back what you just did, while the note at the bottom offers Undo"),
		]),
		new ShortcutGroup("Sizes and windows",
		[
			new ShortcutRow("Ctrl+plus, Ctrl+minus", "Larger and smaller text"),
			new ShortcutRow("Ctrl+0", "Text back to its normal size"),
			new ShortcutRow("Ctrl+Shift+N", "Open the screen in front in a window of its own"),
			new ShortcutRow("F11", "Wall view: the Neurons and their latest problems, for a big screen"),
			new ShortcutRow("Drag the line between two panes", "Make one wider. Double-click it to put it back."),
		]),
		new ShortcutGroup("In any list",
		[
			new ShortcutRow("Right-click", "Everything that can be done with the row"),
			new ShortcutRow("Ctrl+click, Shift+click", "Select several rows"),
			new ShortcutRow("Ctrl+A", "Select every row"),
			new ShortcutRow("Ctrl+C", "Copy what is selected"),
			new ShortcutRow("Ctrl+Shift+C", "Copy it the other way: with times, or as Markdown"),
			new ShortcutRow("Ctrl+S", "Save what is selected to a file"),
			new ShortcutRow("Enter", "Open the row"),
			new ShortcutRow("Escape", "Clear the selection"),
		]),
		new ShortcutGroup("Traffic",
		[
			new ShortcutRow("Drag over lines", "Select them"),
			new ShortcutRow("Enter, or double-click", "Open a line as tidy XML, or close it up"),
			new ShortcutRow("Enter and Shift+Enter in the find box", "Next and previous match"),
			new ShortcutRow("F3 and Shift+F3", "Next and previous match, from the list"),
			new ShortcutRow("Ctrl+End", "Jump to the latest line and follow again"),
		]),
		new ShortcutGroup("Console and chat",
		[
			new ShortcutRow("Enter", "Run the command, or send the message"),
			new ShortcutRow("Shift+Enter", "A new line"),
			new ShortcutRow("Up and Down", "Earlier commands"),
			new ShortcutRow("Tab", "Finish the command name"),
		]),
		new ShortcutGroup("Sources",
		[
			new ShortcutRow("F2", "Edit the node"),
			new ShortcutRow("Alt+Up, Alt+Down", "Move the node among its siblings"),
			new ShortcutRow("Delete", "Delete the node. It asks once more first."),
		]),
	];

	private AlertService? alerts;
	private IAlertSettingsStore? alertStore;
	private IUiSettingsStore? uiStore;
	private Watch.IAutostart? autostart;
	private AlertSettings local = new();
	private UiSettings ui = new();

	/// <summary>Raised after any setting changed. UI thread.</summary>
	public event EventHandler? Changed;

	/// <summary>Raised once what was kept from the last run has been read.</summary>
	public event EventHandler? Loaded;

	/// <summary>The Neuron the screens showed last time, when it was one.</summary>
	public string? Neuron => this.ui.Neuron;

	/// <summary>Keeps which Neuron the screens show, for the next run.</summary>
	public void RememberNeuron(string? bareJid)
	{
		if (string.Equals(this.ui.Neuron, bareJid, StringComparison.OrdinalIgnoreCase))
			return;

		this.ui = this.ui with { Neuron = bareJid };
		try
		{
			this.uiStore?.Save(this.ui);
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not save which Neuron is shown.", ex);
		}
	}

	/// <summary>Connects the settings to the running alert service and to their files. Called once by the host.</summary>
	public void Attach(AlertService alerts, IAlertSettingsStore alertStore, IUiSettingsStore? uiStore = null, Watch.IAutostart? autostart = null)
	{
		this.alerts = alerts;
		this.alertStore = alertStore;
		this.uiStore = uiStore;
		this.autostart = autostart;
		if (uiStore is not null)
		{
			this.ui = uiStore.Load();
			this.ApplyLook();
		}

		this.ApplyStatus();
		this.RaiseAll();
		this.Loaded?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>The alert settings in force.</summary>
	public AlertSettings Alerts => this.alerts?.Settings ?? this.local;

	private void Change(Func<AlertSettings, AlertSettings> change)
	{
		AlertSettings s = change(this.Alerts);
		if (this.alerts is not null)
			this.alerts.Settings = s;
		else
			this.local = s;

		try
		{
			this.alertStore?.Save(s);
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not save the notification settings.", ex);
		}

		this.RaiseAll();
	}

	private void RaiseAll()
	{
		this.OnPropertyChanged(string.Empty);
		this.Changed?.Invoke(this, EventArgs.Empty);
	}

	// ----- Your status -----

	public OwnStatus Status => this.Alerts.DoNotDisturb ? OwnStatus.DoNotDisturb : this.Alerts.Away ? OwnStatus.Away : OwnStatus.Available;

	public bool IsAvailable => this.Status == OwnStatus.Available;
	public bool IsAway => this.Status == OwnStatus.Away;
	public bool IsDoNotDisturb => this.Status == OwnStatus.DoNotDisturb;

	public string StatusText => this.Status switch
	{
		OwnStatus.Away => "Away",
		OwnStatus.DoNotDisturb => "Do not disturb",
		_ => "Available",
	};

	public string StatusHint => this.Status switch
	{
		OwnStatus.Away => "Contacts see you as away. Pop-ups and sounds still reach you.",
		OwnStatus.DoNotDisturb => "Pop-ups and sounds wait. The counts keep running, and contacts see you as busy.",
		_ => "Contacts see you as online, and pop-ups and sounds reach you.",
	};

	[RelayCommand]
	public void SetStatus(OwnStatus status)
	{
		this.Change(s => s with { DoNotDisturb = status == OwnStatus.DoNotDisturb, Away = status == OwnStatus.Away });
		this.ApplyStatus();
	}

	private void ApplyStatus()
		=> _ = chat.SetOwnAvailabilityAsync(this.Status switch
		{
			OwnStatus.Away => Availability.Away,
			OwnStatus.DoNotDisturb => Availability.DoNotDisturb,
			_ => Availability.Online,
		});

	// ----- The look -----

	public LookChoice Look => this.ui.Look;

	public bool IsLookSystem => this.Look == LookChoice.System;
	public bool IsLookLight => this.Look == LookChoice.Light;
	public bool IsLookDark => this.Look == LookChoice.Dark;

	[RelayCommand]
	public void SetLook(LookChoice look)
	{
		this.ui = this.ui with { Look = look };
		this.ApplyLook();
		try
		{
			this.uiStore?.Save(this.ui);
		}
		catch (Exception ex)
		{
			AppLog.Error("Could not save the look.", ex);
		}

		this.RaiseAll();
	}

	private void ApplyLook()
	{
		if (Avalonia.Application.Current is Avalonia.Application app)
		{
			app.RequestedThemeVariant = this.ui.Look switch
			{
				LookChoice.Light => Avalonia.Styling.ThemeVariant.Light,
				LookChoice.Dark => Avalonia.Styling.ThemeVariant.Dark,
				_ => Avalonia.Styling.ThemeVariant.Default,
			};
		}
	}

	// ----- Pop-ups and sounds: a few plain choices first -----

	private bool ownAlertsOpen;

	private static AlertSettings With(AlertSettings s, bool needsMe, bool information) => s with
	{
		Messages = needsMe,
		ContactRequests = needsMe,
		Notifications = needsMe,
		Errors = needsMe,
		NeuronDown = needsMe,
		InformationalNotifications = information,
		SoundForMessages = needsMe,
		SoundForContactRequests = needsMe,
		SoundForNotifications = needsMe,
		SoundForErrors = needsMe,
		SoundForInformation = information,
	};

	private bool AlertsAre(bool needsMe, bool information) => With(this.Alerts, needsMe, information) == this.Alerts;

	/// <summary>A pop-up and a sound for everything, information too.</summary>
	public bool IsAlertsEverything => !this.ownAlertsOpen && this.AlertsAre(true, true);

	/// <summary>A pop-up and a sound for what needs someone. This is how the app comes.</summary>
	public bool IsAlertsNeedsMe => !this.ownAlertsOpen && this.AlertsAre(true, false);

	/// <summary>No pop-ups and no sounds. The counts keep running.</summary>
	public bool IsAlertsNothing => !this.ownAlertsOpen && this.AlertsAre(false, false);

	/// <summary>Chosen kind by kind: the table of switches shows.</summary>
	public bool IsAlertsOwn => !this.IsAlertsEverything && !this.IsAlertsNeedsMe && !this.IsAlertsNothing;

	public string AlertsHint
		=> this.IsAlertsEverything ? "A pop-up and a sound for everything, also for what is only information: updates, restarts, backups."
		: this.IsAlertsNeedsMe ? "A pop-up and a sound for chat messages, requests, notifications that need someone, errors, and a Neuron that stops answering. What is only information just counts in the Inbox."
		: this.IsAlertsNothing ? "No pop-ups and no sounds. Everything still counts in the Inbox and on the tray icon."
		: "Choose for each kind below.";

	/// <summary>One of the plain choices: "everything", "needsMe", "nothing", or "own" for the table.</summary>
	[RelayCommand]
	public void SetAlerts(string choice)
	{
		this.ownAlertsOpen = choice == "own";
		switch (choice)
		{
			case "everything": this.Change(s => With(s, true, true)); break;
			case "needsMe": this.Change(s => With(s, true, false)); break;
			case "nothing": this.Change(s => With(s, false, false)); break;
			default: this.RaiseAll(); break;
		}
	}

	// ----- Pop-ups, kind by kind -----

	public bool PopMessages { get => this.Alerts.Messages; set => this.Change(s => s with { Messages = value }); }
	public bool PopRequests { get => this.Alerts.ContactRequests; set => this.Change(s => s with { ContactRequests = value }); }
	public bool PopNotifications { get => this.Alerts.Notifications; set => this.Change(s => s with { Notifications = value }); }
	public bool PopErrors { get => this.Alerts.Errors; set => this.Change(s => s with { Errors = value }); }
	public bool PopNeuronDown { get => this.Alerts.NeuronDown; set => this.Change(s => s with { NeuronDown = value }); }
	public bool PopInformation { get => this.Alerts.InformationalNotifications; set => this.Change(s => s with { InformationalNotifications = value }); }

	// ----- Sounds -----

	public bool SoundMessages { get => this.Alerts.SoundForMessages; set => this.Change(s => s with { SoundForMessages = value }); }
	public bool SoundRequests { get => this.Alerts.SoundForContactRequests; set => this.Change(s => s with { SoundForContactRequests = value }); }
	public bool SoundNotifications { get => this.Alerts.SoundForNotifications; set => this.Change(s => s with { SoundForNotifications = value }); }
	public bool SoundErrors { get => this.Alerts.SoundForErrors; set => this.Change(s => s with { SoundForErrors = value }); }
	public bool SoundInformation { get => this.Alerts.SoundForInformation; set => this.Change(s => s with { SoundForInformation = value }); }

	// ----- The app -----

	public bool KeepRunningInTray { get => this.Alerts.KeepRunningInTray; set => this.Change(s => s with { KeepRunningInTray = value }); }

	/// <summary>True where the host can start the app when you sign in.</summary>
	public bool CanAutostart => this.autostart is not null;

	public bool StartWithSystem
	{
		get => this.autostart?.IsEnabled ?? false;
		set
		{
			try
			{
				this.autostart?.SetEnabled(value);
			}
			catch (Exception ex)
			{
				AppLog.Error("Could not change whether NAT starts with Windows.", ex);
			}

			this.RaiseAll();
		}
	}

	// ----- Try it -----

	/// <summary>True once the alert service is attached: the test buttons have something to raise.</summary>
	public bool CanTest => this.alerts is not null;

	[RelayCommand]
	private void TestMessage() => this.alerts?.Test(AlertKind.Message);

	[RelayCommand]
	private void TestAlert() => this.alerts?.Test(AlertKind.Error);
}
