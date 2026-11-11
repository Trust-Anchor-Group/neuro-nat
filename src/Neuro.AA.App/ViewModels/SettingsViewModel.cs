using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Alerts;
using Neuro.AA.Engine.Chat;
using Waher.Networking.XMPP;

namespace Neuro.AA.App.ViewModels;

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
}

/// <summary>Keeps <see cref="UiSettings"/> between runs.</summary>
public sealed class JsonUiSettingsStore(string path)
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
	/// <summary>Every keyboard shortcut, as Settings lists them.</summary>
	public static IReadOnlyList<ShortcutGroup> Shortcuts { get; } =
	[
		new ShortcutGroup("Getting around",
		[
			new ShortcutRow("Ctrl+K", "Search for a screen, a Neuron or an account"),
			new ShortcutRow("Ctrl+1 to Ctrl+5", "Inbox, Neurons, Events, Chat, Console"),
			new ShortcutRow("Ctrl+,", "Settings"),
			new ShortcutRow("Ctrl+F", "Filter or find on the screen in front"),
			new ShortcutRow("F5", "Read the screen again"),
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
	private JsonAlertSettingsStore? alertStore;
	private JsonUiSettingsStore? uiStore;
	private Watch.IAutostart? autostart;
	private AlertSettings local = new();
	private UiSettings ui = new();

	/// <summary>Raised after any setting changed. UI thread.</summary>
	public event EventHandler? Changed;

	/// <summary>Connects the settings to the running alert service and to their files. Called once by the host.</summary>
	public void Attach(AlertService alerts, JsonAlertSettingsStore alertStore, JsonUiSettingsStore? uiStore = null, Watch.IAutostart? autostart = null)
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

	// ----- Pop-ups -----

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
				AppLog.Error("Could not change whether Neuro.AA starts with Windows.", ex);
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
