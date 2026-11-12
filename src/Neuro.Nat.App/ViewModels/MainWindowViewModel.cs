using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.ViewModels;

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

	/// <summary>A Neuron's page, on its Overview.</summary>
	Neuron,

	/// <summary>The Events tab of a Neuron's page.</summary>
	NeuronEvents,

	/// <summary>The Console tab of a Neuron's page.</summary>
	NeuronConsole,

	/// <summary>The Identities tab of an account's page.</summary>
	AccountIdentities,

	/// <summary>The Storage tab of an account's page.</summary>
	AccountStorage,

	/// <summary>Two Neurons side by side, or a Neuron beside a kept copy of it.</summary>
	Compare,
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
	private WorkspaceKind lastBoard = WorkspaceKind.Fleet;
	private WorkspaceKind lastAccountsScreen = WorkspaceKind.Accounts;

	public MainWindowViewModel(AccountManager accounts, FleetService fleet, Engine.Chat.ChatService? chat = null, Engine.Identity.IdentityService? identities = null,
		Engine.Devices.DevicesService? devices = null, Engine.PubSub.PubSubService? pubsub = null, Engine.Software.SoftwareService? software = null,
		MainWindowViewModel? primary = null)
	{
		this.primary = primary;
		this.accounts = accounts;
		this.fleet = fleet;
		this.Search = new SearchViewModel(this.SearchItems)
		{
			// People and problems are found as they are now, not as they were when their screens were last open.
			Opening = () =>
			{
				this.Chat?.Refresh();
				this.Events?.Flush();
			},
		};
		this.Picker = new NeuronPickerViewModel(this.NeuronChips, jids => this.SetLens(jids)) { Actions = this };
		this.Events = new EventsViewModel(fleet.Monitors);
		this.NeuronEvents = new EventsViewModel(fleet.Monitors);
		this.Fleet = new FleetViewModel(fleet, accounts) { ShowEventsFor = this.ShowEventsOf };
		this.Attention = new AttentionViewModel(fleet.Monitors, accounts) { ShowEventsFor = this.ShowEventsOf };
		this.Sources = new SourcesViewModel(fleet.Monitors, accounts);

		Engine.Chat.ChatService chatService = this.chatService = chat ?? new Engine.Chat.ChatService(accounts, fleet.Monitors);
		this.devicesService = devices ?? new Engine.Devices.DevicesService(accounts);
		this.identityService = identities ?? new Engine.Identity.IdentityService(accounts, chatService);

		// Windows opened beside the main one share its Settings.
		this.Settings = primary?.Settings ?? new SettingsViewModel(chatService);
		Ui.Layout = this.Settings;
		this.Settings.Loaded += (_, _) =>
		{
			// The Neurons list is sorted the way it was left.
			this.Fleet.Sort = this.Settings.NeuronSort;

			// The Neurons ticked last time are ticked again, and count as soon as they are among the Neurons.
			if (!this.IsExtra && this.LensJids.Count == 0 && this.Settings.Neurons.Count > 0)
				this.SetLens(this.Settings.Neurons, remember: false);
		};
		this.Console = new ConsoleViewModel(chatService, fleet.Monitors, accounts);
		this.Chat = new ChatViewModel(chatService, accounts)
		{
			OpenConsoleFor = (account, jid) => this.OpenNeuron(jid, account, WorkspaceKind.NeuronConsole),
		};
		this.Identities = new IdentitiesViewModel(this.identityService, fleet.Monitors, accounts);
		this.Topology = new TopologyViewModel(new Engine.Topology.TopologyService(fleet.Monitors, chatService), fleet, accounts)
		{
			ShowEventsFor = jid => this.OpenNeuron(jid, null, WorkspaceKind.NeuronEvents),
			OpenNeuronFor = (account, jid) => this.OpenNeuron(jid, account),
			OpenConsoleFor = (account, jid) => this.OpenNeuron(jid, account, WorkspaceKind.NeuronConsole),
		};
		Engine.Software.SoftwareService softwareService = software ?? new Engine.Software.SoftwareService(fleet.Monitors, chatService);
		this.Updates = new UpdatesViewModel(softwareService, fleet.Monitors, accounts);
		this.StartCompare(softwareService);
		this.Devices = new DevicesViewModel(this.devicesService, accounts);
		Engine.PubSub.PubSubService pubsubService = pubsub ?? new Engine.PubSub.PubSubService(accounts);
		this.Inbox = new InboxViewModel(this.Attention, this.Chat, chatService, this.devicesService, this.identityService, accounts, pubsubService)
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
		this.PubSub = new PubSubViewModel(pubsubService, accounts);
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

		this.Fleet.Sorted += (_, _) => this.Settings.RememberNeuronSort(this.Fleet.Sort);
		this.Chat.PropertyChanged += (_, e) =>
		{
			// Picking someone else by hand: what the window was waiting to show is no longer wanted.
			if (e.PropertyName == nameof(ChatViewModel.SelectedPerson) && !this.goingTo && this.Chat.SelectedPerson is not null)
				this.wantedAddress = null;
		};
		this.Settings.LayoutReset += (_, _) => this.Fleet.Sort = null;

		// The Neuron chips and the status strip follow the Neurons list.
		this.Fleet.Refreshed += (_, _) =>
		{
			this.SyncNeurons();
			this.TryWanted();
		};
		foreach (NeuronEvent e in fleet.Monitors.Hub.Snapshot())
			this.NoteEvent(e);
		fleet.Monitors.Hub.EventAdded += (_, e) => this.NoteEvent(e);
		this.statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
		{
			this.RefreshLastEvent();
			this.PruneNotices();
			this.TryWanted();
			if (this.IsNeuronOverview)
				this.RefreshPage();
		});
		this.statusTimer.Start();
		this.RefreshLastEvent();
		this.StartActions();
		this.StartPages();
		this.StartHistory();
		this.StartLinks();
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
		WorkspaceKind.Accounts or WorkspaceKind.Devices or WorkspaceKind.AccountIdentities or WorkspaceKind.AccountStorage => PlaceKind.Accounts,
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
		{
			this.lastNeuronsScreen = value;
			if (!IsPageScreen(value))
				this.lastBoard = value;
		}
		else if (PlaceOf(value) == PlaceKind.Accounts)
			this.lastAccountsScreen = value;

		this.SyncWatched();
		this.ApplyLens();
		this.NoteStop();

		// Going somewhere by hand: what the window was waiting to show is no longer wanted, nor a link waiting for its Neuron.
		if (!this.goingTo)
		{
			this.wantedAddress = null;
			this.pendingLink = null;
		}

		if (this.IsExtra)
			this.OnPropertyChanged(nameof(this.WindowTitle));
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

	/// <summary>"(3) NAT": the title carries the Inbox count, so the taskbar shows it too.</summary>
	public string WindowTitle => this.IsExtra ? this.WorkspaceTitle + " - NAT" : this.Inbox.WaitingCount > 0 ? $"({this.Inbox.Badge}) NAT" : "NAT";

	/// <summary>The Inbox: everything that waits for you.</summary>
	public InboxViewModel Inbox { get; }

	/// <summary>Settings: your status, the look, pop-ups and sounds.</summary>
	public SettingsViewModel Settings { get; }

	/// <summary>
	/// The Neurons button on the rail: back to where you were among the Neurons. Pressed while
	/// already on a Neuron's page, it goes up to the board of all of them.
	/// </summary>
	[RelayCommand]
	private void ShowNeurons() => this.Show(this.IsNeuronPage ? this.lastBoard : this.lastNeuronsScreen);

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
			case WorkspaceKind.Neuron:
			case WorkspaceKind.NeuronEvents:
			case WorkspaceKind.NeuronConsole:
			case WorkspaceKind.Sources:
			case WorkspaceKind.Updates:
			case WorkspaceKind.PubSub:
			case WorkspaceKind.Identities:
				this.ShowPageTab(screen);
				break;
			case WorkspaceKind.Events: this.ShowEvents(); break;
			case WorkspaceKind.Chat: this.ShowChat(); break;
			case WorkspaceKind.Console: this.ShowConsole(); break;
			case WorkspaceKind.Devices:
			case WorkspaceKind.AccountIdentities:
			case WorkspaceKind.AccountStorage:
				this.ShowAccountTab(screen);
				break;
			case WorkspaceKind.Settings: this.ShowSettings(); break;
			case WorkspaceKind.Compare: this.ShowCompare(); break;
			default: this.ShowAccounts(); break;
		}
	}

	// ----- The Neuron filter: tick Neurons and the screens show only those. -----

	/// <summary>Every Neuron, with its state and whether it is ticked. In the order of the Neurons list.</summary>
	public ObservableCollection<NeuronChipViewModel> NeuronChips { get; } = [];

	/// <summary>The button in the top bar and its list.</summary>
	public NeuronPickerViewModel Picker { get; }

	// The Neurons ticked by hand, by bare JID. It may name Neurons that are not there (yet):
	// they count as soon as they are, so a remembered choice comes back as its Neurons connect.
	private FrozenSet<string> lens = NeuronSet.All;

	// What the screens are filtered to now: the ticked Neurons that are there. Empty for all.
	private FrozenSet<string> applied = NeuronSet.All;

	/// <summary>The Neurons ticked by hand. Empty for all of them.</summary>
	public IReadOnlySet<string> LensJids
	{
		get => this.lens;
		set => this.SetLens(value);
	}

	/// <summary>What the screens are filtered to: the ticked Neurons that are there. Empty when every Neuron shows.</summary>
	public IReadOnlySet<string> Lens => this.applied;

	/// <summary>
	/// The one Neuron the screens show, when it is exactly one; null for several or all.
	/// Setting it ticks that Neuron alone, and null shows them all.
	/// </summary>
	public string? LensJid
	{
		get => this.applied.Count == 1 ? this.applied.First() : null;
		set => this.SetLens(value is null ? [] : [value]);
	}

	public bool IsAllNeurons => this.applied.Count == 0;

	[ObservableProperty]
	private bool hasNeuronChips;

	/// <summary>
	/// The Neuron filter shows on the screens that are about all Neurons at once: the Inbox, the
	/// list and the map, Events and the Console. A Neuron's own page, Chat, Accounts and Settings
	/// have nothing for it to narrow.
	/// </summary>
	public bool IsLensVisible => this.Workspace is WorkspaceKind.Attention or WorkspaceKind.Fleet or WorkspaceKind.Topology or WorkspaceKind.Events or WorkspaceKind.Console;

	private string? consoleLens;

	/// <summary>
	/// Ticks these Neurons and no others. Chosen by hand, the main window keeps the choice for
	/// the next run; <paramref name="remember"/> is false when it comes back from there.
	/// </summary>
	public void SetLens(IEnumerable<string> jids, bool remember = true)
	{
		FrozenSet<string> next = NeuronSet.Of(jids);
		if (next.SameAs(this.lens))
			return;

		this.lens = next;
		this.OnPropertyChanged(nameof(this.LensJids));
		this.ApplyTicks();

		// The main window's choice is the one that comes back at start. The others keep theirs with the window.
		if (remember && !this.IsExtra)
			this.Settings.RememberNeurons(next);
	}

	/// <summary>Puts the ticks on the Neurons, and narrows the screens to what is ticked and there.</summary>
	private void ApplyTicks()
	{
		int ticked = 0;
		foreach (NeuronChipViewModel chip in this.NeuronChips)
		{
			chip.IsSelected = this.lens.Contains(chip.BareJid);
			if (chip.IsSelected)
				ticked++;
		}

		// Ticked, but not there: it is not a filter until it is. All of them ticked is no filter.
		FrozenSet<string> next = ticked == 0 || ticked == this.NeuronChips.Count
			? NeuronSet.All
			: NeuronSet.Of(this.NeuronChips.Where(c => c.IsSelected).Select(c => c.BareJid));
		this.Picker.Refresh();
		if (next.SameAs(this.applied))
			return;

		this.applied = next;
		this.Events.NeuronFilter = next;
		this.Inbox.NeuronFilterName = this.Picker.Text;
		this.Inbox.NeuronFilter = next;
		this.Fleet.NeuronFilter = next;
		this.OnPropertyChanged(nameof(this.Lens));
		this.OnPropertyChanged(nameof(this.LensJid));
		this.OnPropertyChanged(nameof(this.IsAllNeurons));
		this.ApplyLens();
	}

	/// <summary>What the picked Neuron means on the screen in front, where that is more than a filter. Only for exactly one.</summary>
	private void ApplyLens()
	{
		if (this.LensJid is not string jid)
		{
			this.consoleLens = null;
			return;
		}

		switch (this.Workspace)
		{
			// On the map nothing is hidden: the Neuron is pointed out.
			case WorkspaceKind.Topology:
				this.Topology.SelectNeuron(jid);
				break;

			// In the Console it is the Neuron the next command goes to. Ticking others by hand afterwards stands.
			case WorkspaceKind.Console when !string.Equals(this.consoleLens, jid, StringComparison.OrdinalIgnoreCase):
				this.consoleLens = jid;
				List<(Guid, string)> targets = [.. this.Fleet.Rows.Where(r => string.Equals(r.BareJid, jid, StringComparison.OrdinalIgnoreCase)).Select(r => (r.AccountId, r.BareJid))];
				if (targets.Count > 0)
					this.Console.Focus(targets);
				break;
		}
	}

	[RelayCommand]
	private void ShowAllNeurons() => this.SetLens([]);

	/// <summary>Ticks a Neuron, or takes its tick away.</summary>
	[RelayCommand]
	private void ToggleNeuron(NeuronChipViewModel? chip)
	{
		if (chip is null)
			this.SetLens([]);
		else
			this.Picker.ToggleCommand.Execute(chip);
	}

	private void SyncNeurons()
	{
		List<IGrouping<string, FleetRowViewModel>> groups = [.. this.Fleet.Rows.GroupBy(r => r.BareJid, StringComparer.OrdinalIgnoreCase)];
		HashSet<string> there = new(groups.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);

		for (int i = this.NeuronChips.Count - 1; i >= 0; i--)
		{
			if (!there.Contains(this.NeuronChips[i].BareJid))
				this.NeuronChips.RemoveAt(i);
		}

		Dictionary<string, NeuronChipViewModel> byJid = new(StringComparer.OrdinalIgnoreCase);
		foreach (NeuronChipViewModel c in this.NeuronChips)
			byJid[c.BareJid] = c;

		for (int i = 0; i < groups.Count; i++)
		{
			if (!byJid.TryGetValue(groups[i].Key, out NeuronChipViewModel? chip))
			{
				chip = new NeuronChipViewModel(groups[i].Key);
				this.NeuronChips.Insert(Math.Min(i, this.NeuronChips.Count), chip);
			}

			chip.Apply([.. groups[i]]);
		}

		// A ticked Neuron that went away stops being a filter; it is ticked again when it comes back.
		this.ApplyTicks();

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
		static Geometry? Icon(string? key)
			=> key is not null && Avalonia.Application.Current is Avalonia.Application app && Avalonia.Controls.ResourceNodeExtensions.TryFindResource(app, "Icon." + key, out object? g) ? g as Geometry : null;

		// What a right-click menu offers is offered here too, from the same list, so the two cannot disagree.
		static IEnumerable<SearchItem> From(IEnumerable<ItemAction> actions, string about, string? words, string fallbackIcon)
		{
			foreach (ItemAction action in actions)
			{
				if (!action.IsEnabled)
					continue;

				if (action.Children is { } inside)
				{
					foreach (SearchItem i in From(inside, about, words, fallbackIcon))
						yield return i;
					continue;
				}

				ItemAction a = action;
				yield return new SearchItem(a.Text, about, Icon(a.Icon ?? (a.Group == ActionGroup.Copy ? "Copy" : fallbackIcon)), () => _ = a.RunAsync())
				{
					Kind = a.Group == ActionGroup.GoTo ? "Go to" : "Do",
					IsDanger = a.IsDanger,
					Words = words,
				};
			}
		}

		yield return new SearchItem("Inbox", "What needs you", Icon("Attention"), this.ShowAttention);
		yield return new SearchItem("Neurons", "The list of all Neurons", Icon("Fleet"), this.ShowFleet);
		yield return new SearchItem("Events", "Problems, and events as they come", Icon("Events"), this.ShowEvents);
		yield return new SearchItem("Chat", "People and rooms", Icon("Chat"), this.ShowChat);
		yield return new SearchItem("Console", "Send commands to Neurons", Icon("Console"), this.ShowConsole);
		yield return new SearchItem("Map", "How the Neurons connect", Icon("Topology"), this.ShowTopology);
		yield return new SearchItem("Accounts", "Your accounts and their connections", Icon("Accounts"), this.ShowAccounts);
		yield return new SearchItem("Devices", "Devices your account owns", Icon("Devices"), this.ShowDevices);
		yield return new SearchItem("Identities", "Your legal identities", Icon("Identities"), this.ShowIdentities);
		yield return new SearchItem("Storage", "Your account's personal storage", Icon("PubSub"), this.ShowPubSub);
		yield return new SearchItem("Settings", "Status, look, pop-ups and sounds", Icon("Settings"), this.ShowSettings);

		foreach (SearchItem i in this.CompareSearchItems(Icon))
			yield return i;

		foreach (NeuronChipViewModel chip in this.NeuronChips)
		{
			string jid = chip.BareJid;
			yield return new SearchItem(chip.Label, "Open its page", Icon("Fleet"), () => this.OpenNeuron(jid)) { Kind = "Neuron", Words = jid };
		}

		// People and rooms. A Neuron is found as a Neuron, above.
		foreach (PersonViewModel person in this.Chat.Everyone)
		{
			if (person.IsNeuron)
				continue;

			PersonViewModel p = person;
			yield return new SearchItem(p.Label, p.IsRoom ? "Open the room" : p.IsPrivate ? "Open the conversation in the room" : "Open the conversation", Icon(p.IsRoom ? "Room" : "Person"),
				() => this.ShowChat(p.AccountId, p.BareJid))
			{
				Kind = p.IsRoom ? "Room" : "Person",
				Words = p.BareJid + " " + p.Name,
			};
		}

		foreach (IssueItemViewModel issue in this.Events.Issues)
		{
			string fingerprint = issue.Fingerprint;
			yield return new SearchItem(issue.Title, issue.Meta, Icon("Alert"), () => this.ShowIssue(fingerprint)) { Kind = "Problem" };
		}

		// The publish/subscribe nodes that have been read: the ones on the service or in the storage looked at last.
		if (this.PubSub.Account is { } nodesOf)
		{
			foreach (PubSubNodeRow node in this.PubSub.Nodes)
			{
				PubSubNodeRow n = node;
				yield return new SearchItem(n.Label, string.IsNullOrEmpty(n.Name) ? n.Info.Service : n.Name + ", on " + n.Info.Service, Icon("PubSub"), () => this.ShowNode(nodesOf.Id, n.Info.Service, n.Node)) { Kind = "Node", Words = n.Node + " " + n.Name };
			}
		}

		foreach (AccountItemViewModel account in this.Accounts)
		{
			AccountItemViewModel a = account;
			yield return new SearchItem(a.Label, a.StatusText, Icon("Accounts"), () => this.ShowAccount(a.Session.Profile.Id)) { Kind = "Account" };
		}

		// Things to do: with a Neuron, with an account, and with the app itself.
		foreach (NeuronChipViewModel chip in this.NeuronChips)
		{
			foreach (SearchItem i in From(this.NeuronActions(chip.BareJid, null, WorkspaceKind.Fleet), chip.Label, chip.BareJid, "Fleet"))
				yield return i;
		}

		foreach (AccountItemViewModel account in this.Accounts)
		{
			foreach (SearchItem i in From(this.ActionsFor([account]), account.Label, null, "Accounts"))
				yield return i;
		}

		yield return new SearchItem(this.Settings.IsDoNotDisturb ? "Available" : "Do not disturb", this.Settings.IsDoNotDisturb ? "Let pop-ups and sounds through again" : "Hold back pop-ups and sounds",
			Icon(this.Settings.IsDoNotDisturb ? "Bell" : "BellOff"), () => this.Settings.SetStatus(this.Settings.IsDoNotDisturb ? OwnStatus.Available : OwnStatus.DoNotDisturb)) { Kind = "Do" };
		yield return new SearchItem(this.IsDark ? "Light look" : "Dark look", "Switch between light and dark", Icon(this.IsDark ? "Sun" : "Moon"), this.ToggleTheme) { Kind = "Do" };
		yield return new SearchItem("New window", "Open what is showing in a window of its own", Icon("Window"), () => this.NewWindowCommand.Execute(null)) { Kind = "Do", Words = "second monitor screen" };
		yield return new SearchItem("Big screen", "The Neurons and open problems, large", Icon("Fleet"), () => this.OpenBigScreenCommand.Execute(null)) { Kind = "Do", Words = "full screen monitor tv dashboard pages" };
		yield return new SearchItem("Larger text", "One text size up", Icon("Settings"), () => this.Settings.StepTextSize(1)) { Kind = "Do", Words = "zoom font size bigger" };
		yield return new SearchItem("Smaller text", "One text size down", Icon("Settings"), () => this.Settings.StepTextSize(-1)) { Kind = "Do", Words = "zoom font size" };
		if (this.CanGoBack)
			yield return new SearchItem("Back", this.BackTip, Icon("Back"), () => this.GoBackCommand.Execute(null)) { Kind = "Do" };
		if (!this.IsAllNeurons)
			yield return new SearchItem("Show all Neurons", "Showing " + this.Picker.Text, Icon("Filter"), () => this.SetLens([])) { Kind = "Do" };
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
				if (alert.NeuronJid is string neuron && this.RowOf(neuron, alert.AccountId) is not null)
					this.OpenNeuron(neuron, alert.AccountId);
				else
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
	[NotifyPropertyChangedFor(nameof(IsAccounts), nameof(IsEvents), nameof(IsFleet), nameof(IsAttention), nameof(IsSources), nameof(IsChat), nameof(IsConsole), nameof(IsIdentities), nameof(IsTopology), nameof(IsUpdates), nameof(IsDevices), nameof(IsPubSub), nameof(IsSettings), nameof(IsCompare), nameof(HasCrumb), nameof(WorkspaceTitle),
		nameof(Place), nameof(IsPlaceInbox), nameof(IsPlaceNeurons), nameof(IsPlaceAccounts), nameof(IsLensVisible),
		nameof(IsNeuronPage), nameof(IsNeuronBoard), nameof(IsNeuronOverview), nameof(IsNeuronEvents), nameof(IsNeuronConsole), nameof(IsAccountIdentities), nameof(IsAccountStorage))]
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

	/// <summary>Publish/subscribe has two homes: an account's Storage, and the nodes on a Neuron's page.</summary>
	[RelayCommand]
	private void ShowPubSub() => this.ShowAccountTab(WorkspaceKind.AccountStorage);

	[RelayCommand]
	private void ShowDevices() => this.ShowAccountTab(WorkspaceKind.Devices);

	[RelayCommand]
	private void ShowUpdates() => this.ShowPageTab(WorkspaceKind.Updates);

	[RelayCommand]
	private void ShowTopology()
	{
		// From the list to the map: the Neuron you had picked stays picked.
		bool fromList = this.Workspace == WorkspaceKind.Fleet;
		this.Topology.Activate();
		if (fromList && this.Fleet.SelectedRow is FleetRowViewModel row)
			this.Topology.SelectNeuron(row.BareJid);

		this.Workspace = WorkspaceKind.Topology;
	}

	/// <summary>Your own identities, on the account's page. A Neuron's identity is a tab on its page.</summary>
	[RelayCommand]
	private void ShowIdentities() => this.ShowAccountTab(WorkspaceKind.AccountIdentities);

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
	private void ShowSources() => this.ShowPageTab(WorkspaceKind.Sources);

	[RelayCommand]
	private void ShowAttention()
	{
		this.Inbox.Refresh();
		this.Workspace = WorkspaceKind.Attention;
	}

	[RelayCommand]
	private void ShowFleet()
	{
		// From the map to the list: the Neuron you had picked stays picked, when the list shows it.
		if (this.Workspace == WorkspaceKind.Topology && this.Topology.SelectedId is string id && this.Topology.NeuronAt(id) is string jid &&
			this.Fleet.Shown.FirstOrDefault(r => string.Equals(r.BareJid, jid, StringComparison.OrdinalIgnoreCase)) is FleetRowViewModel row)
		{
			this.Fleet.SelectedRow = row;
		}

		this.Workspace = WorkspaceKind.Fleet;
	}

	/// <summary>The name of the current place, shown in the top bar. On a Neuron's page, the Neuron.</summary>
	public string WorkspaceTitle => this.IsNeuronPage && this.Page is NeuronPageViewModel p ? p.Label : this.IsCompare ? "Compare" : this.Place.ToString();

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

	/// <summary>"2 accounts, 1 online"</summary>
	public string AccountSummary
	{
		get
		{
			int n = this.Accounts.Count;
			int online = this.Accounts.Count(a => a.IsOnline);
			return n == 0 ? "No accounts" : $"{n} account{(n == 1 ? "" : "s")}, {online} online";
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
		this.OpenWhereLeft();
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
				AccountItemViewModel item = new(s, this.RemoveAsync, (id, secret) => accounts.SetSecretAsync(id, secret), (id, password) => accounts.ChangePasswordAsync(id, password));
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
