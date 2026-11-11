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
		this.PubSub.Shell = this;
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

		// A Neuron that wrote to you has a conversation of its own.
		if (except != WorkspaceKind.Chat && this.Chat.Everyone.FirstOrDefault(c => !c.IsRoom && string.Equals(c.BareJid, neuronJid, StringComparison.OrdinalIgnoreCase) && (account is null || c.AccountId == account)) is PersonViewModel talk)
			list.GoTo("Its messages in Chat", () => this.ShowChat(talk.AccountId, talk.BareJid), "Chat");

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
			}, icon: "Updates");
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
		// These Neurons were asked for by name: the one picked at the top does not replace them.
		this.consoleLens = this.LensJid;
		this.Console.Focus(neurons);
		if (command is not null)
			this.Console.CommandText = command;

		this.Workspace = WorkspaceKind.Console;
	}

	public void ShowChat(Guid accountId, string bareJid)
	{
		this.Chat.Refresh();
		if (!this.Chat.Start(accountId, bareJid))
			Ui.Say("No conversation with " + bareJid + " yet");

		this.Workspace = WorkspaceKind.Chat;
	}

	public void ShowNode(Guid accountId, string service, string node, bool people = false)
	{
		PubSubTab tab = people ? PubSubTab.People : PubSubTab.Items;

		// A service with a person's address is that account's own storage. Any other is the service of the account's Neuron.
		if (service.Contains('@'))
		{
			if (this.Accounts.FirstOrDefault(a => a.Session.Profile.Id == accountId) is AccountItemViewModel account)
				this.SelectedAccount = account;

			this.ShowAccountTab(WorkspaceKind.AccountStorage);
			if (this.IsAccountStorage)
				this.PubSub.Go(node, tab);
			return;
		}

		string? neuron = this.accounts.Get(accountId)?.Profile.EffectiveDomain;
		if (neuron is not null && this.RowOf(neuron, accountId) is not null)
			this.OpenNeuron(neuron, accountId, WorkspaceKind.PubSub);

		if (this.IsPubSub)
			this.PubSub.Go(node, tab);
		else
			Ui.Say("The nodes of " + service + " are on the page of its Neuron, which is not among yours");
	}

	public void ShowAccount(Guid accountId)
	{
		if (this.Accounts.FirstOrDefault(a => a.Session.Profile.Id == accountId) is AccountItemViewModel account)
			this.SelectedAccount = account;

		this.ShowAccounts();
	}

	void IShell.Share(string title, string markdown) => this.Share(title, markdown);

	/// <summary>
	/// Check for updates on several Neurons at once, from the Neurons list. The answer shows on
	/// each row, and in one sentence. Upgrading stays one Neuron at a time, on its Software tab.
	/// </summary>
	public async Task CheckUpdatesAsync(IReadOnlyList<(Guid Account, string Jid)> neurons)
	{
		if (neurons.Count == 0)
			return;

		FleetRowViewModel? Row((Guid Account, string Jid) n)
			=> this.Fleet.Rows.FirstOrDefault(r => r.AccountId == n.Account && string.Equals(r.BareJid, n.Jid, StringComparison.OrdinalIgnoreCase));

		foreach ((Guid Account, string Jid) n in neurons)
		{
			if (Row(n) is FleetRowViewModel row)
				(row.UpdateNote, row.UpdateTone) = ("Checking…", "idle");
		}

		Ui.Say(neurons.Count == 1 ? "Asking it for updates" : $"Asking {neurons.Count} Neurons for updates");
		IReadOnlyList<UpdateCheck> answers = await this.Updates.CheckManyAsync(neurons);

		foreach (UpdateCheck a in answers)
		{
			if (Row((a.AccountId, a.NeuronJid)) is FleetRowViewModel row)
				(row.UpdateNote, row.UpdateTone) = (a.Text, a.HasUpdate ? "busy" : a.Answered ? "ok" : a.WasAsked ? "bad" : "idle");
		}

		int waiting = answers.Count(a => a.HasUpdate);
		int silent = answers.Count(a => !a.Answered && a.WasAsked);
		int away = answers.Count(a => !a.WasAsked);
		string said = waiting switch
		{
			0 => silent + away == answers.Count ? (answers.Count == 1 ? "It did not answer" : "None of them answered") : "No updates are waiting",
			1 => "1 Neuron has an update waiting: " + answers.First(a => a.HasUpdate).Label,
			_ => $"{waiting} Neurons have an update waiting",
		};
		if (silent + away < answers.Count)
		{
			List<string> rest = [];
			if (silent > 0)
				rest.Add($"{silent} did not answer");
			if (away > 0)
				rest.Add(away == 1 ? "1 is not connected" : $"{away} are not connected");
			if (rest.Count > 0)
				said += ". " + string.Join(", ", rest);
		}

		Ui.Say(said);
	}

	// ----- Things the shell itself shows: Neuron chips and accounts -----

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		if (items.Count != 1)
			return [];

		switch (items[0])
		{
			case HistoryMenu menu:
				return this.StopsMenu(ReferenceEquals(menu, HistoryMenu.Back));

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
