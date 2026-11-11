using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.ViewModels;

/// <summary>Workspaces in the left rail that exist so far.</summary>
public enum WorkspaceKind
{
	Attention,
	Accounts,
	Fleet,
	Events,
	Sources,
	Chat,
	Console,
	Identities,
	Topology,
	Updates,
	Devices,
	PubSub,
	Settings,
}

/// <summary>Shell view model: workspace navigation, the account lens, and the Accounts workspace.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
	private readonly AccountManager accounts;
	private readonly FleetService fleet;
	private readonly Engine.Chat.ChatService chatService;
	private readonly Engine.Devices.DevicesService devicesService;
	private readonly Engine.Identity.IdentityService identityService;
	private readonly DispatcherTimer statusTimer;
	private long lastEventTicks;
	private WorkspaceKind lastNeuronsScreen = WorkspaceKind.Fleet;
	private WorkspaceKind lastAccountsScreen = WorkspaceKind.Accounts;

	public MainWindowViewModel(AccountManager accounts, FleetService fleet, Engine.Chat.ChatService? chat = null, Engine.Identity.IdentityService? identities = null,
		Engine.Devices.DevicesService? devices = null, Engine.PubSub.PubSubService? pubsub = null)
	{
		this.accounts = accounts;
		this.fleet = fleet;
		this.Search = new SearchViewModel(this.SearchItems);
		this.Events = new EventsViewModel(fleet.Monitors);
		this.Fleet = new FleetViewModel(fleet, accounts) { ShowEventsFor = this.ShowEventsOf };
		this.Attention = new AttentionViewModel(fleet.Monitors, accounts) { ShowEventsFor = this.ShowEventsOf };
		this.Sources = new SourcesViewModel(fleet.Monitors, accounts);

		Engine.Chat.ChatService chatService = this.chatService = chat ?? new Engine.Chat.ChatService(accounts, fleet.Monitors);
		this.devicesService = devices ?? new Engine.Devices.DevicesService(accounts);
		this.identityService = identities ?? new Engine.Identity.IdentityService(accounts, chatService);
		this.Settings = new SettingsViewModel(chatService);
		this.Console = new ConsoleViewModel(chatService, fleet.Monitors, accounts);
		this.Chat = new ChatViewModel(chatService, accounts)
		{
			OpenConsoleFor = (account, jid) =>
			{
				this.Console.Focus(account, jid);
				this.Workspace = WorkspaceKind.Console;
			},
		};
		this.Identities = new IdentitiesViewModel(this.identityService, fleet.Monitors, accounts);
		this.Topology = new TopologyViewModel(new Engine.Topology.TopologyService(fleet.Monitors, chatService), fleet, accounts)
		{
			ShowEventsFor = this.ShowEventsOf,
			OpenConsoleFor = (account, jid) =>
			{
				this.Console.Focus(account, jid);
				this.Workspace = WorkspaceKind.Console;
			},
		};
		this.Updates = new UpdatesViewModel(new Engine.Software.SoftwareService(fleet.Monitors, chatService), fleet.Monitors, accounts);
		this.Devices = new DevicesViewModel(this.devicesService, accounts);
		this.Inbox = new InboxViewModel(this.Attention, this.Chat, chatService, this.devicesService, this.identityService, accounts)
		{
			OpenIdentities = this.ShowIdentities,
			OpenDevices = this.ShowDevices,
		};
		this.Inbox.ShowsConversationChanged += (_, _) => this.SyncWatched();
		this.Inbox.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(InboxViewModel.WaitingCount))
				this.OnPropertyChanged(nameof(this.WindowTitle));
		};
		this.PubSub = new PubSubViewModel(pubsub ?? new Engine.PubSub.PubSubService(accounts), accounts);
		this.Events.ShareTo = this.Share;
		this.Attention.ShareTo = this.Share;
		this.Sources.ShareTo = this.Share;

		this.Chat.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(ChatViewModel.UnreadTotal) or nameof(ChatViewModel.HasRequests) or nameof(ChatViewModel.RequestCount))
			{
				this.OnPropertyChanged(nameof(this.HasUnreadChat));
				this.OnPropertyChanged(nameof(this.ChatBadge));
			}
		};

		// The Neuron chips and the status strip follow the Neurons list.
		this.Fleet.Refreshed += (_, _) => this.SyncNeurons();
		foreach (NeuronEvent e in fleet.Monitors.Hub.Snapshot())
			this.NoteEvent(e);
		fleet.Monitors.Hub.EventAdded += (_, e) => this.NoteEvent(e);
		this.statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
		{
			this.RefreshLastEvent();
			this.PruneNotices();
		});
		this.statusTimer.Start();
		this.RefreshLastEvent();
	}

	// ----- Places: the rail has five, plus Accounts. Each holds one or more screens. -----

	/// <summary>The place the current screen belongs to.</summary>
	public PlaceKind Place => PlaceOf(this.Workspace);

	private static PlaceKind PlaceOf(WorkspaceKind screen) => screen switch
	{
		WorkspaceKind.Attention => PlaceKind.Inbox,
		WorkspaceKind.Events => PlaceKind.Events,
		WorkspaceKind.Chat => PlaceKind.Chat,
		WorkspaceKind.Console => PlaceKind.Console,
		WorkspaceKind.Accounts or WorkspaceKind.Devices => PlaceKind.Accounts,
		WorkspaceKind.Settings => PlaceKind.Settings,
		_ => PlaceKind.Neurons,
	};

	public bool IsPlaceInbox => this.Place == PlaceKind.Inbox;
	public bool IsPlaceNeurons => this.Place == PlaceKind.Neurons;
	public bool IsPlaceAccounts => this.Place == PlaceKind.Accounts;

	partial void OnWorkspaceChanged(WorkspaceKind value)
	{
		// Coming back to a place returns to the screen you left it on.
		if (PlaceOf(value) == PlaceKind.Neurons)
			this.lastNeuronsScreen = value;
		else if (PlaceOf(value) == PlaceKind.Accounts)
			this.lastAccountsScreen = value;

		this.SyncWatched();
	}

	/// <summary>The window is in front. Set by the window; a conversation only counts as read while it is.</summary>
	public bool IsWindowActive
	{
		get => this.isWindowActive;
		set
		{
			this.isWindowActive = value;
			this.SyncWatched();
		}
	}

	private bool isWindowActive = true;

	/// <summary>Tells Chat whether its open conversation is actually on screen.</summary>
	private void SyncWatched()
		=> this.Chat.IsWatched = this.IsWindowActive && (this.IsChat || (this.IsAttention && this.Inbox.ShowsConversation));

	/// <summary>"(3) Neuro.AA": the title carries the Inbox count, so the taskbar shows it too.</summary>
	public string WindowTitle => this.Inbox.WaitingCount > 0 ? $"({this.Inbox.Badge}) Neuro.AA" : "Neuro.AA";

	/// <summary>The Inbox: everything that waits for you.</summary>
	public InboxViewModel Inbox { get; }

	/// <summary>Settings: your status, the look, pop-ups and sounds.</summary>
	public SettingsViewModel Settings { get; }

	[RelayCommand]
	private void ShowNeurons() => this.Show(this.lastNeuronsScreen);

	[RelayCommand]
	private void ShowAccountsPlace() => this.Show(this.lastAccountsScreen);

	/// <summary>Opens a screen the way its rail or tab button does, so it loads what it needs.</summary>
	private void Show(WorkspaceKind screen)
	{
		switch (screen)
		{
			case WorkspaceKind.Attention: this.ShowAttention(); break;
			case WorkspaceKind.Fleet: this.ShowFleet(); break;
			case WorkspaceKind.Topology: this.ShowTopology(); break;
			case WorkspaceKind.Sources: this.ShowSources(); break;
			case WorkspaceKind.Updates: this.ShowUpdates(); break;
			case WorkspaceKind.PubSub: this.ShowPubSub(); break;
			case WorkspaceKind.Identities: this.ShowIdentities(); break;
			case WorkspaceKind.Events: this.ShowEvents(); break;
			case WorkspaceKind.Chat: this.ShowChat(); break;
			case WorkspaceKind.Console: this.ShowConsole(); break;
			case WorkspaceKind.Devices: this.ShowDevices(); break;
			case WorkspaceKind.Settings: this.ShowSettings(); break;
			default: this.ShowAccounts(); break;
		}
	}

	// ----- Neuron chips: click one and the screens show only that Neuron. -----

	public ObservableCollection<NeuronChipViewModel> NeuronChips { get; } = [];

	/// <summary>Bare JID of the Neuron the screens are filtered to; null for all.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAllNeurons))]
	private string? lensJid;

	public bool IsAllNeurons => this.LensJid is null;

	[ObservableProperty]
	private bool hasNeuronChips;

	/// <summary>The chips show where they filter something: the Events screen, for now.</summary>
	public bool IsLensVisible => this.Workspace == WorkspaceKind.Events;

	partial void OnLensJidChanged(string? value)
	{
		foreach (NeuronChipViewModel chip in this.NeuronChips)
			chip.IsSelected = string.Equals(chip.BareJid, value, StringComparison.OrdinalIgnoreCase);

		this.Events.NeuronFilter = value;
	}

	[RelayCommand]
	private void ShowAllNeurons() => this.LensJid = null;

	[RelayCommand]
	private void ToggleNeuron(NeuronChipViewModel? chip)
		=> this.LensJid = chip is null || chip.IsSelected ? null : chip.BareJid;

	private void SyncNeurons()
	{
		List<IGrouping<string, FleetRowViewModel>> groups = [.. this.Fleet.Rows.GroupBy(r => r.BareJid, StringComparer.OrdinalIgnoreCase)];

		for (int i = this.NeuronChips.Count - 1; i >= 0; i--)
		{
			if (!groups.Any(g => string.Equals(g.Key, this.NeuronChips[i].BareJid, StringComparison.OrdinalIgnoreCase)))
				this.NeuronChips.RemoveAt(i);
		}

		for (int i = 0; i < groups.Count; i++)
		{
			NeuronChipViewModel? chip = this.NeuronChips.FirstOrDefault(c => string.Equals(c.BareJid, groups[i].Key, StringComparison.OrdinalIgnoreCase));
			if (chip is null)
			{
				chip = new NeuronChipViewModel(groups[i].Key);
				this.NeuronChips.Insert(Math.Min(i, this.NeuronChips.Count), chip);
			}

			chip.Apply([.. groups[i]]);
			chip.IsSelected = string.Equals(chip.BareJid, this.LensJid, StringComparison.OrdinalIgnoreCase);
		}

		// A Neuron that is gone cannot stay the filter.
		if (this.LensJid is not null && !this.NeuronChips.Any(c => c.IsSelected))
			this.LensJid = null;

		this.HasNeuronChips = this.NeuronChips.Count > 0;

		int total = this.NeuronChips.Count;
		int live = this.NeuronChips.Count(c => c.IsOk);
		this.StatusNeurons = total switch
		{
			0 => "No Neurons yet",
			1 => live == 1 ? "1 Neuron reporting" : "1 Neuron, not reporting",
			_ => $"{live} of {total} Neurons reporting",
		};
		this.StatusOk = total > 0 && live == total;
		this.StatusBad = total > 0 && live == 0;
		this.StatusBusy = total > 0 && live > 0 && live < total;
	}

	// ----- Status strip at the bottom of the window. -----

	[ObservableProperty] private string statusNeurons = "No Neurons yet";
	[ObservableProperty] private bool statusOk;
	[ObservableProperty] private bool statusBusy;
	[ObservableProperty] private bool statusBad;
	[ObservableProperty] private string statusLastEvent = "No events yet";

	/// <summary>True when an account failed to sign in: the strip and the rail point at it.</summary>
	public bool HasFailedAccount => this.Accounts.Any(a => a.IsFailed);

	private void NoteEvent(NeuronEvent e)
	{
		long t = e.Timestamp.Ticks;
		long seen;
		while (t > (seen = Interlocked.Read(ref this.lastEventTicks)))
		{
			if (Interlocked.CompareExchange(ref this.lastEventTicks, t, seen) == seen)
				break;
		}
	}

	private void RefreshLastEvent()
	{
		long ticks = Interlocked.Read(ref this.lastEventTicks);
		if (ticks == 0)
		{
			this.StatusLastEvent = "No events yet";
			return;
		}

		DateTime now = DateTime.Now;
		TimeSpan age = now - new DateTime(ticks);
		this.StatusLastEvent = "Last event " + (age < TimeSpan.FromSeconds(5) ? "just now"
			: age < TimeSpan.FromSeconds(60) ? $"{(int)age.TotalSeconds} s ago"
			: RelativeTime.Format(new DateTime(ticks), now));
	}

	// ----- Search box (Ctrl+K). -----

	public SearchViewModel Search { get; }

	private IEnumerable<SearchItem> SearchItems()
	{
		static Geometry? Icon(string key)
			=> Avalonia.Application.Current is Avalonia.Application app && Avalonia.Controls.ResourceNodeExtensions.TryFindResource(app, "Icon." + key, out object? g) ? g as Geometry : null;

		yield return new SearchItem("Inbox", "What needs you", Icon("Attention"), this.ShowAttention);
		yield return new SearchItem("Neurons", "The list of all Neurons", Icon("Fleet"), this.ShowFleet);
		yield return new SearchItem("Events", "Problems and the live stream", Icon("Events"), this.ShowEvents);
		yield return new SearchItem("Chat", "People and rooms", Icon("Chat"), this.ShowChat);
		yield return new SearchItem("Console", "Send commands to Neurons", Icon("Console"), this.ShowConsole);
		yield return new SearchItem("Map", "How the Neurons connect", Icon("Topology"), this.ShowTopology);
		yield return new SearchItem("Sources", "Data sources and nodes", Icon("Sources"), this.ShowSources);
		yield return new SearchItem("Software", "Versions and updates", Icon("Updates"), this.ShowUpdates);
		yield return new SearchItem("Publish/subscribe", "Nodes and personal storage", Icon("PubSub"), this.ShowPubSub);
		yield return new SearchItem("Identities", "Legal identities", Icon("Identities"), this.ShowIdentities);
		yield return new SearchItem("Accounts", "Your accounts and their connections", Icon("Accounts"), this.ShowAccounts);
		yield return new SearchItem("Devices", "Devices you own", Icon("Devices"), this.ShowDevices);
		yield return new SearchItem("Settings", "Status, look, pop-ups and sounds", Icon("Settings"), this.ShowSettings);
		yield return new SearchItem(this.Settings.IsDoNotDisturb ? "Available" : "Do not disturb", this.Settings.IsDoNotDisturb ? "Let pop-ups and sounds through again" : "Hold back pop-ups and sounds",
			Icon(this.Settings.IsDoNotDisturb ? "Bell" : "BellOff"), () => this.Settings.SetStatus(this.Settings.IsDoNotDisturb ? OwnStatus.Available : OwnStatus.DoNotDisturb));

		foreach (NeuronChipViewModel chip in this.NeuronChips)
		{
			string jid = chip.BareJid;
			yield return new SearchItem(chip.Label, "Neuron: show its events", Icon("Events"), () => this.ShowEventsOf(jid));
		}

		foreach (AccountItemViewModel account in this.Accounts)
		{
			AccountItemViewModel a = account;
			yield return new SearchItem(a.Label, "Account: " + a.StatusText, Icon("Accounts"), () =>
			{
				this.SelectedAccount = a;
				this.ShowAccounts();
			});
		}

		yield return new SearchItem(this.IsDark ? "Light look" : "Dark look", "Switch between light and dark", Icon(this.IsDark ? "Sun" : "Moon"), this.ToggleTheme);
	}

	/// <summary>The Chat workspace.</summary>
	public ChatViewModel Chat { get; }

	/// <summary>The Publish/subscribe workspace.</summary>
	public PubSubViewModel PubSub { get; }

	/// <summary>The Devices workspace.</summary>
	public DevicesViewModel Devices { get; }

	/// <summary>The Updates workspace.</summary>
	public UpdatesViewModel Updates { get; }

	/// <summary>Opens what an alert points at: the notification, the issue, or the Neuron in Fleet.</summary>
	public void OpenAlert(Engine.Alerts.Alert alert)
	{
		switch (alert.Kind)
		{
			case Engine.Alerts.AlertKind.Notification:
				// One that is only information is not in the list until done items show.
				this.Inbox.Tab = InboxTab.Everything;
				this.Attention.Refresh();
				if (!this.Inbox.Select("n:" + alert.Target))
				{
					this.Inbox.ShowDone = true;
					this.Attention.Refresh();
					this.Inbox.Select("n:" + alert.Target);
				}

				this.Workspace = WorkspaceKind.Attention;
				break;

			case Engine.Alerts.AlertKind.Message when alert.AccountId is Guid account && alert.Target is string jid:
				this.ShowChat();
				this.Chat.Open(account, jid);
				break;

			case Engine.Alerts.AlertKind.Message:
				this.ShowChat();
				break;

			case Engine.Alerts.AlertKind.ContactRequest:
			case Engine.Alerts.AlertKind.RoomInvitation:
			case Engine.Alerts.AlertKind.DeviceQuestion:
				this.Inbox.Tab = InboxTab.Requests;
				this.Inbox.Select(alert.Kind switch
				{
					Engine.Alerts.AlertKind.ContactRequest => "c:" + alert.AccountId?.ToString("N") + "|" + alert.Target?.ToLowerInvariant(),
					Engine.Alerts.AlertKind.RoomInvitation => "inv:" + alert.AccountId?.ToString("N") + "|" + alert.Target?.ToLowerInvariant(),
					_ => "q:" + alert.Target,
				});
				this.Workspace = WorkspaceKind.Attention;
				break;

			case Engine.Alerts.AlertKind.IdentityRequest:
				this.ShowIdentities();
				break;

			case Engine.Alerts.AlertKind.Error:
				this.Events.Flush();
				this.Events.SelectedIssue = this.Events.Issues.FirstOrDefault(i => i.Fingerprint == alert.Target) ?? this.Events.SelectedIssue;
				this.Workspace = WorkspaceKind.Events;
				break;

			default:
				this.Fleet.Refresh();
				this.Fleet.SelectedRow = this.Fleet.Rows.FirstOrDefault(r => string.Equals(r.BareJid, alert.NeuronJid, StringComparison.OrdinalIgnoreCase)) ?? this.Fleet.SelectedRow;
				this.Workspace = WorkspaceKind.Fleet;
				break;
		}
	}

	/// <summary>The Topology workspace.</summary>
	public TopologyViewModel Topology { get; }

	/// <summary>The Identities workspace.</summary>
	public IdentitiesViewModel Identities { get; }

	/// <summary>The chat admin console.</summary>
	public ConsoleViewModel Console { get; }

	/// <summary>Unread messages and contact requests, for the Chat badge on the rail.</summary>
	public int ChatBadge => this.Chat.UnreadTotal + this.Chat.Requests.Count;

	public bool HasUnreadChat => this.ChatBadge > 0;

	/// <summary>Opens Chat to pick who gets a shared event, issue, notification or node.</summary>
	private void Share(string title, string markdown)
	{
		this.Chat.BeginShare(title, markdown);
		this.Chat.Refresh();
		this.Workspace = WorkspaceKind.Chat;
	}

	/// <summary>The Sources workspace.</summary>
	public SourcesViewModel Sources { get; }

	private void ShowEventsOf(string jid)
	{
		this.Events.SelectedIssue = null;

		// A Neuron with a chip is filtered through the chip; anything else through the text filter.
		if (this.NeuronChips.Any(c => string.Equals(c.BareJid, jid, StringComparison.OrdinalIgnoreCase)))
		{
			this.Events.SearchText = string.Empty;
			this.LensJid = jid;
		}
		else
		{
			this.LensJid = null;
			this.Events.SearchText = jid;
		}

		this.Workspace = WorkspaceKind.Events;
	}

	/// <summary>The Attention workspace.</summary>
	public AttentionViewModel Attention { get; }

	/// <summary>The Events workspace.</summary>
	public EventsViewModel Events { get; }

	/// <summary>The Fleet workspace.</summary>
	public FleetViewModel Fleet { get; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAccounts), nameof(IsEvents), nameof(IsFleet), nameof(IsAttention), nameof(IsSources), nameof(IsChat), nameof(IsConsole), nameof(IsIdentities), nameof(IsTopology), nameof(IsUpdates), nameof(IsDevices), nameof(IsPubSub), nameof(IsSettings), nameof(WorkspaceTitle),
		nameof(Place), nameof(IsPlaceInbox), nameof(IsPlaceNeurons), nameof(IsPlaceAccounts), nameof(IsLensVisible))]
	private WorkspaceKind workspace = WorkspaceKind.Accounts;

	public bool IsAccounts => this.Workspace == WorkspaceKind.Accounts;
	public bool IsEvents => this.Workspace == WorkspaceKind.Events;
	public bool IsFleet => this.Workspace == WorkspaceKind.Fleet;
	public bool IsAttention => this.Workspace == WorkspaceKind.Attention;
	public bool IsSources => this.Workspace == WorkspaceKind.Sources;
	public bool IsChat => this.Workspace == WorkspaceKind.Chat;
	public bool IsConsole => this.Workspace == WorkspaceKind.Console;
	public bool IsIdentities => this.Workspace == WorkspaceKind.Identities;
	public bool IsTopology => this.Workspace == WorkspaceKind.Topology;
	public bool IsUpdates => this.Workspace == WorkspaceKind.Updates;
	public bool IsDevices => this.Workspace == WorkspaceKind.Devices;
	public bool IsPubSub => this.Workspace == WorkspaceKind.PubSub;
	public bool IsSettings => this.Workspace == WorkspaceKind.Settings;

	[RelayCommand]
	private void ShowSettings() => this.Workspace = WorkspaceKind.Settings;

	[RelayCommand]
	private void ShowPubSub()
	{
		this.PubSub.Activate();
		this.Workspace = WorkspaceKind.PubSub;
	}

	[RelayCommand]
	private void ShowDevices()
	{
		this.Devices.Activate();
		this.Workspace = WorkspaceKind.Devices;
	}

	[RelayCommand]
	private void ShowUpdates()
	{
		this.Updates.Activate();
		this.Workspace = WorkspaceKind.Updates;
	}

	[RelayCommand]
	private void ShowTopology()
	{
		this.Topology.Activate();
		this.Workspace = WorkspaceKind.Topology;
	}

	[RelayCommand]
	private void ShowIdentities()
	{
		this.Identities.Activate();
		this.Workspace = WorkspaceKind.Identities;
	}

	[RelayCommand]
	private void ShowChat()
	{
		this.Chat.Refresh();
		this.Workspace = WorkspaceKind.Chat;
	}

	[RelayCommand]
	private void ShowConsole()
	{
		this.Console.SyncTargets();
		this.Workspace = WorkspaceKind.Console;
	}

	[RelayCommand]
	private void ShowSources()
	{
		this.Sources.SyncNeurons();
		this.Workspace = WorkspaceKind.Sources;
	}

	[RelayCommand]
	private void ShowAttention()
	{
		this.Inbox.Refresh();
		this.Workspace = WorkspaceKind.Attention;
	}

	[RelayCommand]
	private void ShowFleet() => this.Workspace = WorkspaceKind.Fleet;
	/// <summary>The name of the current place, shown in the top bar.</summary>
	public string WorkspaceTitle => this.Place.ToString();

	[RelayCommand]
	private void ShowAccounts() => this.Workspace = WorkspaceKind.Accounts;

	[RelayCommand]
	private void ShowEvents() => this.Workspace = WorkspaceKind.Events;

	public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

	[ObservableProperty]
	private AccountItemViewModel? selectedAccount;

	// Add-account form
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(AddAccountCommand))]
	private string newHost = "localhost";

	[ObservableProperty]
	private string newPort = "5222";

	[ObservableProperty]
	private string newDomain = string.Empty;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(AddAccountCommand))]
	private string newUserName = string.Empty;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(AddAccountCommand))]
	private string newPassword = string.Empty;

	[ObservableProperty]
	private bool newTrustCertificate;

	/// <summary>Create the account on the Neuron with an API key, instead of logging in to an existing one.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AddFormTitle), nameof(AddButtonText))]
	[NotifyCanExecuteChangedFor(nameof(AddAccountCommand))]
	private bool newCreate;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(AddAccountCommand))]
	private string newApiKey = string.Empty;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(AddAccountCommand))]
	private string newApiSecret = string.Empty;

	[ObservableProperty] private string newEMail = string.Empty;
	[ObservableProperty] private string newPhone = string.Empty;

	public string AddFormTitle => this.NewCreate ? "Create an account" : "Add an account";
	public string AddButtonText => this.NewCreate ? "Create and connect" : "Add and connect";

	[ObservableProperty]
	private string? formError;

	[ObservableProperty]
	private bool isAddFormOpen;

	public bool HasAccounts => this.Accounts.Count > 0;

	/// <summary>"2 accounts · 1 online"</summary>
	public string AccountSummary
	{
		get
		{
			int n = this.Accounts.Count;
			int online = this.Accounts.Count(a => a.IsOnline);
			return n == 0 ? "No accounts" : $"{n} account{(n == 1 ? "" : "s")} · {online} online";
		}
	}

	/// <summary>"0/3 online", for narrow windows.</summary>
	public string AccountSummaryShort => $"{this.Accounts.Count(a => a.IsOnline)}/{this.Accounts.Count} online";

	/// <summary>True when the dark theme is showing.</summary>
	public bool IsDark => Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

	[RelayCommand]
	private void ToggleTheme()
	{
		this.Settings.SetLook(this.IsDark ? LookChoice.Light : LookChoice.Dark);
	}

	private void ThemeChanged()
	{
		foreach (AccountItemViewModel a in this.Accounts)
			a.RefreshTheme();
		this.OnPropertyChanged(nameof(this.IsDark));
	}

	public async Task StartAsync()
	{
		if (Avalonia.Application.Current is Avalonia.Application app)
			app.ActualThemeVariantChanged += (_, _) => this.ThemeChanged();

		accounts.SessionsChanged += (_, _) => Dispatcher.UIThread.Post(this.Sync);
		await accounts.LoadAsync();
		this.Sync();
		this.IsAddFormOpen = this.Accounts.Count == 0;
		await accounts.ConnectStartupAccountsAsync();
	}

	private void Sync()
	{
		IReadOnlyList<AccountSession> sessions = accounts.Sessions;

		foreach (AccountItemViewModel gone in this.Accounts.Where(a => !sessions.Contains(a.Session)).ToList())
		{
			this.Accounts.Remove(gone);
			gone.Dispose();
		}

		foreach (AccountSession s in sessions)
		{
			if (!this.Accounts.Any(a => a.Session == s))
			{
				AccountItemViewModel item = new(s, this.RemoveAsync, (id, secret) => accounts.SetSecretAsync(id, secret));
				item.PropertyChanged += (_, e) =>
				{
					if (e.PropertyName == nameof(AccountItemViewModel.IsOnline))
						this.OnPropertyChanged(nameof(this.AccountSummary));
					if (e.PropertyName == nameof(AccountItemViewModel.IsOnline))
						this.OnPropertyChanged(nameof(this.AccountSummaryShort));
					if (e.PropertyName == nameof(AccountItemViewModel.IsFailed))
						this.OnPropertyChanged(nameof(this.HasFailedAccount));
				};
				this.Accounts.Add(item);
			}
		}

		this.SelectedAccount ??= this.Accounts.FirstOrDefault();
		this.OnPropertyChanged(nameof(this.HasAccounts));
		this.OnPropertyChanged(nameof(this.AccountSummary));
		this.OnPropertyChanged(nameof(this.AccountSummaryShort));
		this.OnPropertyChanged(nameof(this.HasFailedAccount));
	}

	private bool CanAddAccount() => !string.IsNullOrWhiteSpace(this.NewHost) && !string.IsNullOrWhiteSpace(this.NewUserName) && !string.IsNullOrEmpty(this.NewPassword) &&
		(!this.NewCreate || (!string.IsNullOrWhiteSpace(this.NewApiKey) && !string.IsNullOrEmpty(this.NewApiSecret)));

	[RelayCommand(CanExecute = nameof(CanAddAccount))]
	private async Task AddAccount()
	{
		this.FormError = null;

		if (!int.TryParse(this.NewPort, out int port) || port is < 1 or > 65535)
		{
			this.FormError = "Port must be a number between 1 and 65535.";
			return;
		}

		AccountProfile profile = new()
		{
			Host = this.NewHost.Trim(),
			Port = port,
			Domain = this.NewDomain.Trim(),
			UserName = this.NewUserName.Trim(),
			TrustServerCertificate = this.NewTrustCertificate,
		};

		try
		{
			AccountSession session = this.NewCreate
				? await accounts.CreateAsync(profile, this.NewPassword, new AccountRegistration(this.NewApiKey.Trim(), this.NewApiSecret,
					string.IsNullOrWhiteSpace(this.NewEMail) ? null : this.NewEMail, string.IsNullOrWhiteSpace(this.NewPhone) ? null : this.NewPhone))
				: await accounts.AddAsync(profile, AccountSecret.FromPassword(this.NewPassword), connect: true);
			this.NewPassword = string.Empty;
			this.NewApiSecret = string.Empty;
			this.NewApiKey = string.Empty;
			this.NewCreate = false;
			this.NewUserName = string.Empty;
			this.IsAddFormOpen = false;
			this.Sync();
			this.SelectedAccount = this.Accounts.FirstOrDefault(a => a.Session == session);
		}
		catch (Exception ex)
		{
			this.FormError = ex.Message;
		}
	}

	[RelayCommand]
	private void ToggleAddForm() => this.IsAddFormOpen = !this.IsAddFormOpen;

	private async Task RemoveAsync(AccountItemViewModel item)
	{
		await accounts.RemoveAsync(item.Session.Profile.Id);
		if (this.SelectedAccount == item)
			this.SelectedAccount = this.Accounts.FirstOrDefault(a => a != item);
	}
}
