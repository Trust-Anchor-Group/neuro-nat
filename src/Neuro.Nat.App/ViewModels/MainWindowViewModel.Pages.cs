using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.ViewModels;

/// <summary>
/// Pages: a Neuron's page with its tabs, and an account's page with its tabs. The tabs are the
/// screens that used to pick a Neuron or an account themselves; here the shell points them at
/// the one whose page is open.
/// </summary>
public sealed partial class MainWindowViewModel
{
	private readonly Dictionary<string, NeuronPageViewModel> pages = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ConsoleViewModel> pageConsoles = new(StringComparer.OrdinalIgnoreCase);
	private int pageDirty;

	private static string PageKey(Guid account, string jid) => account.ToString("N") + "|" + jid;

	// ----- A Neuron's page -----

	/// <summary>The Neuron whose page is open, or was open last.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(WorkspaceTitle), nameof(HasPage))]
	private NeuronPageViewModel? page;

	public bool HasPage => this.Page is not null;

	/// <summary>The console on the Neuron's page: it talks to that Neuron only, and keeps what was said.</summary>
	[ObservableProperty] private ConsoleViewModel? pageConsole;

	/// <summary>The events of the Neuron whose page is open: its Events tab, and the lists on its Overview.</summary>
	public EventsViewModel NeuronEvents { get; }

	private static bool IsPageScreen(WorkspaceKind screen) => screen is WorkspaceKind.Neuron or WorkspaceKind.NeuronEvents or WorkspaceKind.Sources
		or WorkspaceKind.NeuronConsole or WorkspaceKind.Updates or WorkspaceKind.PubSub or WorkspaceKind.Identities;

	/// <summary>True on a Neuron's page, whichever tab.</summary>
	public bool IsNeuronPage => IsPageScreen(this.Workspace);

	/// <summary>True on the board of all Neurons, as a list or as a map.</summary>
	public bool IsNeuronBoard => this.Workspace is WorkspaceKind.Fleet or WorkspaceKind.Topology;

	public bool IsNeuronOverview => this.Workspace == WorkspaceKind.Neuron;
	public bool IsNeuronEvents => this.Workspace == WorkspaceKind.NeuronEvents;
	public bool IsNeuronConsole => this.Workspace == WorkspaceKind.NeuronConsole;

	/// <summary>Opens a Neuron's page, on its Overview or on the tab asked for.</summary>
	public void OpenNeuron(string neuronJid, Guid? accountId = null, WorkspaceKind? tab = null)
	{
		FleetRowViewModel? row = this.RowOf(neuronJid, accountId);
		if (row is null)
		{
			this.Fleet.Refresh();
			row = this.RowOf(neuronJid, accountId);
		}

		if (row is null)
		{
			Ui.Say(neuronJid + " is not among your Neurons");
			return;
		}

		string key = PageKey(row.AccountId, row.BareJid);
		if (!this.pages.TryGetValue(key, out NeuronPageViewModel? p))
			this.pages[key] = p = new NeuronPageViewModel(row.AccountId, row.BareJid);

		this.Page = p;
		this.Fleet.SelectedRow = row;
		this.NeuronEvents.SelectedIssue = null;
		this.NeuronEvents.NeuronFilter = NeuronSet.Of(row.BareJid);
		this.RefreshPage(everything: true);
		this.ShowPageTab(tab is WorkspaceKind t && IsPageScreen(t) ? t : WorkspaceKind.Neuron);
	}

	/// <summary>Shows a tab of the open Neuron's page. With no page open, opens one for the Neuron picked in the list.</summary>
	[RelayCommand]
	private void ShowPageTab(WorkspaceKind tab)
	{
		if (this.Page is not NeuronPageViewModel p)
		{
			FleetRowViewModel? row = this.Fleet.SelectedRow ?? this.Fleet.Rows.FirstOrDefault(r => r.State == WatchState.Live) ?? this.Fleet.Rows.FirstOrDefault();
			if (row is null)
				this.ShowFleet();
			else
				this.OpenNeuron(row.BareJid, row.AccountId, tab);
			return;
		}

		switch (tab)
		{
			case WorkspaceKind.NeuronEvents:
				this.NeuronEvents.Flush();
				break;

			case WorkspaceKind.Sources:
				this.Sources.Focus(p.AccountId, p.BareJid);
				break;

			case WorkspaceKind.NeuronConsole:
				string key = PageKey(p.AccountId, p.BareJid);
				if (!this.pageConsoles.TryGetValue(key, out ConsoleViewModel? console))
				{
					this.pageConsoles[key] = console = new ConsoleViewModel(this.chatService, this.fleet.Monitors, this.accounts) { Shell = this };
					console.Pin(p.AccountId, p.BareJid);
				}

				console.SyncTargets();
				this.PageConsole = console;
				break;

			case WorkspaceKind.Updates:
				this.Updates.Focus(p.AccountId, p.BareJid);
				break;

			case WorkspaceKind.PubSub when p.HostsAccount:
				this.PubSub.Show(p.AccountId, personal: false);
				break;

			case WorkspaceKind.Identities:
				this.Identities.ShowNeuron(p.AccountId, p.BareJid);
				break;

			default:
				tab = WorkspaceKind.Neuron;
				this.NeuronEvents.Flush();
				this.RefreshPage(everything: true);
				if (!this.Topology.HasScan)
					this.Topology.Activate();
				break;
		}

		this.Workspace = tab;
	}

	/// <summary>Back from a Neuron's page to the board of all Neurons.</summary>
	[RelayCommand]
	private void BackToNeurons() => this.Show(this.lastBoard);

	/// <summary>The account's traffic in a window of its own, at the lines that mention this Neuron.</summary>
	[RelayCommand]
	private void WatchPageTraffic()
	{
		if (this.Page is NeuronPageViewModel p)
			this.WatchTrafficOf(p.AccountId, p.BareJid);
	}

	/// <summary>Opens the traffic of the account that reaches a Neuron, in its own window, at the lines that mention the Neuron.</summary>
	private void WatchTrafficOf(Guid accountId, string neuronJid)
	{
		if (this.Accounts.FirstOrDefault(a => a.Session.Profile.Id == accountId) is not AccountItemViewModel account)
			return;

		account.Sniffer.FindText = neuronJid;
		account.Sniffer.OpenWindowCommand.Execute(null);
	}

	[RelayCommand]
	private void CheckPageUpdates()
	{
		if (this.Page is null)
			return;

		this.ShowPageTab(WorkspaceKind.Updates);
		if (this.Updates.CheckCommand.CanExecute(null))
			this.Updates.CheckCommand.Execute(null);
	}

	[RelayCommand]
	private void ReadPageHealth()
	{
		this.Fleet.RefreshNowCommand.Execute(null);
		Ui.Say("Reading its health");
	}

	/// <summary>Opens what a row under "Waiting for you" on the page points at, in the Inbox.</summary>
	[RelayCommand]
	private void OpenWaiting(PageWaiting? item)
	{
		if (item is null)
			return;

		this.Inbox.Tab = InboxTab.Everything;
		this.Inbox.Refresh();
		this.Inbox.Select(item.Key);
		this.Workspace = WorkspaceKind.Attention;
	}

	/// <summary>Opens the page of a Neuron this one is connected to, or shows a broker on the map.</summary>
	[RelayCommand]
	private void OpenLink(TopologyConnection? link)
	{
		if (link is null)
			return;

		if (this.Topology.NeuronAt(link.Id) is string jid && this.RowOf(jid) is not null)
		{
			this.OpenNeuron(jid);
			return;
		}

		this.ShowTopology();
		this.Topology.SelectedId = link.Id;
	}

	[RelayCommand]
	private void ShowPageOnMap()
	{
		if (this.Page is not NeuronPageViewModel p)
			return;

		this.ShowTopology();
		this.Topology.SelectNeuron(p.BareJid);
	}

	/// <summary>Opens an issue from the page's Overview on its Events tab.</summary>
	[RelayCommand]
	private void OpenPageIssue(IssueItemViewModel? issue)
	{
		if (issue is null)
			return;

		this.ShowPageTab(WorkspaceKind.NeuronEvents);
		this.NeuronEvents.SelectedIssue = issue;
	}

	/// <summary>Brings the page up to date. The lists that cost more are only rebuilt when asked or when something changed.</summary>
	private void RefreshPage(bool everything = false)
	{
		if (this.Page is not NeuronPageViewModel p)
			return;

		FleetRowViewModel? row = this.Fleet.Rows.FirstOrDefault(r => r.AccountId == p.AccountId && string.Equals(r.BareJid, p.BareJid, StringComparison.OrdinalIgnoreCase));
		if (row is not null)
			p.Apply(row);

		p.Apply(this.fleet.Get(p.AccountId, p.BareJid), DateTime.Now);

		AccountProfile? profile = this.accounts.Get(p.AccountId)?.Profile;
		p.HostsAccount = profile is not null && string.Equals(profile.EffectiveDomain, p.BareJid, StringComparison.OrdinalIgnoreCase);

		if (!everything && Interlocked.Exchange(ref this.pageDirty, 0) == 0)
			return;

		DateTime now = DateTime.Now;
		List<PageWaiting> waiting = [.. this.Inbox.Everything
			.Where(r => !r.IsDone && string.Equals(r.Jid, p.BareJid, StringComparison.OrdinalIgnoreCase) && (r.AccountId == p.AccountId || r.AccountId == Guid.Empty))
			.Take(6)
			.Select(r => new PageWaiting(r.Key, r.IconKey, r.Title, RelativeTime.Format(r.When, now)))];
		if (!waiting.SequenceEqual(p.Waiting))
		{
			p.Waiting.Clear();
			foreach (PageWaiting w in waiting)
				p.Waiting.Add(w);
		}

		p.HasWaiting = p.Waiting.Count > 0;

		IReadOnlyList<TopologyConnection> links = this.Topology.ConnectionsOf(p.BareJid);
		if (!links.SequenceEqual(p.Links))
		{
			p.Links.Clear();
			foreach (TopologyConnection l in links)
				p.Links.Add(l);
		}

		p.HasLinks = p.Links.Count > 0;
		p.LinksNote = p.HasLinks ? string.Empty
			: this.Topology.IsScanning ? "Asking…"
			: this.Topology.HasScan ? "Nothing reported"
			: "Not asked yet.";
	}

	private void StartPages()
	{
		this.NeuronEvents.Shell = this;
		this.NeuronEvents.ShareTo = this.Share;
		this.Fleet.Refreshed += (_, _) => this.RefreshPage();
		this.Topology.Scanned += (_, _) => this.RefreshPage(everything: true);
		this.Inbox.Rows.CollectionChanged += (_, _) => Interlocked.Exchange(ref this.pageDirty, 1);
		this.Inbox.ShowAllNeurons = () => this.LensJid = null;
		this.Fleet.OpenNeuron = row => this.OpenNeuron(row.BareJid, row.AccountId);
	}

	// ----- An account's page -----

	public bool IsAccountIdentities => this.Workspace == WorkspaceKind.AccountIdentities;
	public bool IsAccountStorage => this.Workspace == WorkspaceKind.AccountStorage;

	/// <summary>Shows a tab of the selected account's page: its connection, devices, identities or storage.</summary>
	[RelayCommand]
	private void ShowAccountTab(WorkspaceKind tab)
	{
		Guid? id = this.SelectedAccount?.Session.Profile.Id;
		switch (tab)
		{
			case WorkspaceKind.Devices when id is Guid a:
				this.Devices.ShowAccount(a);
				break;

			case WorkspaceKind.AccountIdentities when id is Guid a:
				this.Identities.ShowAccount(a);
				break;

			case WorkspaceKind.AccountStorage when id is Guid a:
				this.PubSub.Show(a, personal: true);
				break;

			default:
				tab = WorkspaceKind.Accounts;
				break;
		}

		this.Workspace = tab;
	}

	partial void OnSelectedAccountChanged(AccountItemViewModel? value)
	{
		// The tab stays; what it shows follows the account.
		if (this.IsPlaceAccounts && this.Workspace != WorkspaceKind.Accounts)
			this.ShowAccountTab(this.Workspace);

		this.NoteStop();
	}
}
