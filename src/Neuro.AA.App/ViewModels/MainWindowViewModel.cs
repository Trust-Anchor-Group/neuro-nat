using System.Collections.ObjectModel;
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
}

/// <summary>Shell view model: workspace navigation, the account lens, and the Accounts workspace.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
	private readonly AccountManager accounts;

	public MainWindowViewModel(AccountManager accounts, FleetService fleet, Engine.Chat.ChatService? chat = null, Engine.Identity.IdentityService? identities = null,
		Engine.Devices.DevicesService? devices = null, Engine.PubSub.PubSubService? pubsub = null)
	{
		this.accounts = accounts;
		this.Events = new EventsViewModel(fleet.Monitors);
		this.Fleet = new FleetViewModel(fleet, accounts) { ShowEventsFor = this.ShowEventsOf };
		this.Attention = new AttentionViewModel(fleet.Monitors, accounts) { ShowEventsFor = this.ShowEventsOf };
		this.Sources = new SourcesViewModel(fleet.Monitors, accounts);

		Engine.Chat.ChatService chatService = chat ?? new Engine.Chat.ChatService(accounts, fleet.Monitors);
		this.Console = new ConsoleViewModel(chatService, fleet.Monitors, accounts);
		this.Chat = new ChatViewModel(chatService, accounts)
		{
			OpenConsoleFor = (account, jid) =>
			{
				this.Console.Focus(account, jid);
				this.Workspace = WorkspaceKind.Console;
			},
		};
		this.Identities = new IdentitiesViewModel(identities ?? new Engine.Identity.IdentityService(accounts, chatService), fleet.Monitors, accounts);
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
		this.Devices = new DevicesViewModel(devices ?? new Engine.Devices.DevicesService(accounts), accounts);
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
				this.Attention.Filter = AttentionFilter.All;
				this.Attention.Refresh();
				this.Attention.SelectedItem = this.Attention.Items.FirstOrDefault(i => i.Id == alert.Target) ?? this.Attention.SelectedItem;
				this.Workspace = WorkspaceKind.Attention;
				break;

			case Engine.Alerts.AlertKind.ContactRequest:
				this.ShowChat();
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
		this.Events.SearchText = jid;
		this.Workspace = WorkspaceKind.Events;
	}

	/// <summary>The Attention workspace.</summary>
	public AttentionViewModel Attention { get; }

	/// <summary>The Events workspace.</summary>
	public EventsViewModel Events { get; }

	/// <summary>The Fleet workspace.</summary>
	public FleetViewModel Fleet { get; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAccounts), nameof(IsEvents), nameof(IsFleet), nameof(IsAttention), nameof(IsSources), nameof(IsChat), nameof(IsConsole), nameof(IsIdentities), nameof(IsTopology), nameof(IsUpdates), nameof(IsDevices), nameof(IsPubSub), nameof(WorkspaceTitle))]
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
	private void ShowAttention() => this.Workspace = WorkspaceKind.Attention;

	[RelayCommand]
	private void ShowFleet() => this.Workspace = WorkspaceKind.Fleet;
	public string WorkspaceTitle => this.Workspace == WorkspaceKind.PubSub ? "Publish/subscribe" : this.Workspace.ToString();

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
		if (Avalonia.Application.Current is Avalonia.Application app)
			app.RequestedThemeVariant = this.IsDark ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
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
				};
				this.Accounts.Add(item);
			}
		}

		this.SelectedAccount ??= this.Accounts.FirstOrDefault();
		this.OnPropertyChanged(nameof(this.HasAccounts));
		this.OnPropertyChanged(nameof(this.AccountSummary));
		this.OnPropertyChanged(nameof(this.AccountSummaryShort));
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
