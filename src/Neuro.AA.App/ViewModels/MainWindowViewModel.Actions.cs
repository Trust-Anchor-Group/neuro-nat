using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.ViewModels;

/// <summary>
/// The shell's side of "anything you can see, you can act on": where a Neuron's "Go to" leads
/// from any screen, what can be done to a Neuron or an account wherever it is shown, and the
/// short note that confirms an action.
/// </summary>
public sealed partial class MainWindowViewModel : IShell, IActionSource
{
	private DispatcherTimer? flashTimer;

	/// <summary>The note at the bottom of the window: "Copied 3 events". Null when nothing is shown.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasFlash))]
	private string? flash;

	public bool HasFlash => this.Flash is not null;

	private void StartActions()
	{
		Ui.Said += this.Ui_Said;
		this.Events.Shell = this;
		this.Fleet.Shell = this;
		this.Console.Shell = this;
		this.Sources.Shell = this;
		this.Chat.Shell = this;
		this.Inbox.Shell = this;
		this.Topology.Shell = this;
		this.Updates.Shell = this;
	}

	private void Ui_Said(string note)
	{
		if (Dispatcher.UIThread.CheckAccess())
			this.ShowFlash(note);
		else
			Dispatcher.UIThread.Post(() => this.ShowFlash(note));
	}

	private void ShowFlash(string note)
	{
		this.Flash = note;
		this.flashTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2.4), DispatcherPriority.Background, (_, _) =>
		{
			this.flashTimer!.Stop();
			this.Flash = null;
		});
		this.flashTimer.Stop();
		this.flashTimer.Start();
	}

	// ----- Neurons, wherever they appear -----

	private FleetRowViewModel? RowOf(string jid, Guid? account = null)
		=> this.Fleet.Rows.FirstOrDefault(r => string.Equals(r.BareJid, jid, StringComparison.OrdinalIgnoreCase) && (account is null || r.AccountId == account))
			?? this.Fleet.Rows.FirstOrDefault(r => string.Equals(r.BareJid, jid, StringComparison.OrdinalIgnoreCase));

	public IReadOnlyList<ItemAction> NeuronPlaces(string neuronJid, Guid? accountId = null, WorkspaceKind? except = null)
	{
		FleetRowViewModel? row = this.RowOf(neuronJid, accountId);
		Guid? account = row?.AccountId ?? accountId;
		ActionList list = [];

		// The tab you are on is not offered, and neither is the page while you are on it.
		bool here = this.IsNeuronPage && this.Page is NeuronPageViewModel p && string.Equals(p.BareJid, neuronJid, StringComparison.OrdinalIgnoreCase);

		void Tab(WorkspaceKind kind, string text, string icon)
		{
			if (except != kind && !(here && this.Workspace == kind))
				list.GoTo(text, () => this.OpenNeuron(neuronJid, account, kind), icon);
		}

		if (row is not null)
		{
			if (except != WorkspaceKind.Fleet)
				Tab(WorkspaceKind.Neuron, here ? "Overview" : "Its page", "Fleet");
			Tab(WorkspaceKind.NeuronEvents, "Its events", "Events");
			Tab(WorkspaceKind.Sources, "Sources", "Sources");
			Tab(WorkspaceKind.NeuronConsole, "Console", "Console");
			Tab(WorkspaceKind.Updates, "Software", "Updates");
			Tab(WorkspaceKind.Identities, "Identity", "Identities");
		}

		if (except != WorkspaceKind.Events)
			list.GoTo(row is null ? "Events" : "Its events among all Neurons", () => this.ShowEventsOf(neuronJid), "Events");

		if (except != WorkspaceKind.Topology)
		{
			list.GoTo("Map", () =>
			{
				this.ShowTopology();
				this.Topology.SelectNeuron(neuronJid);
			}, "Topology");
		}

		return list;
	}

	public IReadOnlyList<ItemAction> NeuronActions(string neuronJid, Guid? accountId = null, WorkspaceKind? except = null)
	{
		FleetRowViewModel? row = this.RowOf(neuronJid, accountId);
		ActionList list = [];

		list.Copy("Copy address", () => neuronJid, "the address", "Ctrl+C");
		if (row is not null)
		{
			if (row.Software.Length > 1)
				list.Copy("Copy version", () => row.Software, "the version");
			list.Copy("Copy status summary", () => FleetViewModel.Summarize(row), "the status summary");
		}

		list.AddRange(this.NeuronPlaces(neuronJid, accountId, except));

		if (row is not null)
		{
			Guid a = row.AccountId;
			list.Do("Read its health now", () => this.Fleet.RefreshNowCommand.Execute(null), icon: "Refresh");
			list.Do("Watch its traffic", () => this.WatchTrafficOf(a, neuronJid), icon: "Sniffer");
			list.Do("Check for updates", () =>
			{
				this.OpenNeuron(neuronJid, a, WorkspaceKind.Updates);
				if (this.Updates.CheckCommand.CanExecute(null))
					this.Updates.CheckCommand.Execute(null);
			});
			list.Do("Reboot…", () =>
			{
				this.OpenNeuron(neuronJid, a, WorkspaceKind.NeuronConsole);
				if (this.PageConsole is ConsoleViewModel console)
					console.CommandText = "Reboot";
			}, danger: true);
		}

		return list;
	}

	public void ShowEvents(string neuronJid, string? search = null)
	{
		this.ShowEventsOf(neuronJid);
		if (search is not null)
		{
			this.Events.SeverityFilter = SeverityFilter.All;
			this.Events.SearchText = search;
		}
	}

	public void ShowIssue(string fingerprint)
	{
		this.LensJid = null;
		this.Events.SearchText = string.Empty;
		this.Events.ShowResolved = this.Events.ShowResolved || this.Events.Issues.All(i => i.Fingerprint != fingerprint);
		this.Events.Flush();
		this.Events.SelectedIssue = this.Events.Issues.FirstOrDefault(i => i.Fingerprint == fingerprint) ?? this.Events.SelectedIssue;
		this.Workspace = WorkspaceKind.Events;
	}

	/// <summary>Opens the Console with these Neurons ticked, and a command ready to run when one is given.</summary>
	public void ShowConsole(IReadOnlyList<(Guid Account, string Jid)> neurons, string? command = null)
	{
		this.Console.Focus(neurons);
		if (command is not null)
			this.Console.CommandText = command;

		this.Workspace = WorkspaceKind.Console;
	}

	public void ShowChat(Guid accountId, string bareJid)
	{
		this.Chat.Refresh();
		if (!this.Chat.Open(accountId, bareJid))
			Ui.Say("No conversation with " + bareJid + " yet");

		this.Workspace = WorkspaceKind.Chat;
	}

	public void ShowAccount(Guid accountId)
	{
		if (this.Accounts.FirstOrDefault(a => a.Session.Profile.Id == accountId) is AccountItemViewModel account)
			this.SelectedAccount = account;

		this.ShowAccounts();
	}

	void IShell.Share(string title, string markdown) => this.Share(title, markdown);

	// ----- Things the shell itself shows: Neuron chips and accounts -----

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		if (items.Count != 1)
			return [];

		switch (items[0])
		{
			case NeuronChipViewModel chip:
				ActionList forChip = [];
				forChip.Filter(chip.IsSelected ? "Show all Neurons" : "Show only this Neuron", () => this.ToggleNeuron(chip));
				forChip.AddRange(this.NeuronActions(chip.BareJid, null, WorkspaceKind.Events));
				return forChip;

			case NeuronPageViewModel page:
				return this.NeuronActions(page.BareJid, page.AccountId);

			case AccountItemViewModel account:
				ActionList forAccount = [];
				string jid = account.Session.Profile.UserName + "@" + account.Session.Profile.EffectiveDomain;
				forAccount.Copy("Copy address", () => jid, "the address", "Ctrl+C");
				forAccount.Copy("Copy server", () => account.Endpoint, "the server");
				if (!this.IsAccounts || !ReferenceEquals(this.SelectedAccount, account))
					forAccount.GoTo("Its traffic", () => this.ShowAccount(account.Session.Profile.Id), "Sniffer");
				forAccount.Do("Connect", () => account.ConnectCommand.Execute(null), enabled: account.ConnectCommand.CanExecute(null), icon: "Plug");
				forAccount.Do("Go offline", () => account.DisconnectCommand.Execute(null), enabled: account.DisconnectCommand.CanExecute(null), icon: "Unplug");
				forAccount.Do("Remove…", () =>
				{
					this.ShowAccount(account.Session.Profile.Id);
					account.RemoveCommand.Execute(null);
				}, danger: true, icon: "Trash");
				return forAccount;
		}

		return [];
	}

	// ----- Keyboard: Ctrl+1 to Ctrl+5 are the five places -----

	/// <summary>Goes to a place by its number on the rail. Returns false for a number that is not a place.</summary>
	public bool GoToPlace(int number)
	{
		switch (number)
		{
			case 1: this.ShowAttention(); return true;
			case 2: this.ShowNeurons(); return true;
			case 3: this.ShowEvents(); return true;
			case 4: this.ShowChat(); return true;
			case 5: this.ShowConsole(); return true;
			default: return false;
		}
	}

	/// <summary>F5: reads the current screen again where that means something.</summary>
	public void RefreshCurrent()
	{
		switch (this.Workspace)
		{
			case WorkspaceKind.Fleet:
			case WorkspaceKind.Neuron:
				this.Fleet.RefreshNowCommand.Execute(null);
				break;
			case WorkspaceKind.Attention:
				this.Inbox.Refresh();
				break;
			case WorkspaceKind.Chat:
				this.Chat.Refresh();
				break;
			case WorkspaceKind.Sources:
				if (this.Sources.LoadTreeCommand.CanExecute(null))
					this.Sources.LoadTreeCommand.Execute(null);
				break;
			case WorkspaceKind.Topology:
				if (this.Topology.ScanCommand.CanExecute(null))
					this.Topology.ScanCommand.Execute(null);
				break;
			default:
				return;
		}

		Ui.Say("Reading again");
	}
}
