using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.PubSub;

namespace Neuro.AA.App.ViewModels;

/// <summary>One of a few answers to pick from, in words.</summary>
public sealed record Choice<T>(T Value, string Text)
{
	public override string ToString() => this.Text;
}

/// <summary>
/// A node's settings as a short form in plain words. The same form shows an existing node's
/// settings and the ones a new node will get.
/// </summary>
public sealed partial class NodeSettingsForm : ObservableObject
{
	public static IReadOnlyList<Choice<NodeAccess>> AccessList { get; } =
	[
		new(NodeAccess.Open, "Anyone"),
		new(NodeAccess.Presence, "The owner's contacts"),
		new(NodeAccess.Roster, "The owner's contacts in certain groups"),
		new(NodeAccess.Authorize, "Whoever an owner says yes to, one request at a time"),
		new(NodeAccess.Whitelist, "Only the people on the node's list"),
	];

	public static IReadOnlyList<Choice<NodePublishers>> PublisherList { get; } =
	[
		new(NodePublishers.Publishers, "Owners, and the people named as publishers"),
		new(NodePublishers.Subscribers, "Everyone who subscribes"),
		new(NodePublishers.Open, "Anyone"),
	];

	public static IReadOnlyList<Choice<NodeLastItem>> LastList { get; } =
	[
		new(NodeLastItem.OnSubscribe, "Yes, when they subscribe"),
		new(NodeLastItem.OnSubscribeAndPresence, "Yes, when they subscribe and each time they come online"),
		new(NodeLastItem.Never, "No"),
	];

	public IReadOnlyList<Choice<NodeAccess>> AccessChoices => AccessList;
	public IReadOnlyList<Choice<NodePublishers>> PublisherChoices => PublisherList;
	public IReadOnlyList<Choice<NodeLastItem>> LastChoices => LastList;

	private NodeSettings loaded = new();

	[ObservableProperty] private string title = string.Empty;
	[ObservableProperty] private string description = string.Empty;
	[ObservableProperty] private Choice<NodeAccess> access = AccessList[0];
	[ObservableProperty] private Choice<NodePublishers> publishers = PublisherList[0];
	[ObservableProperty] private bool keepItems = true;

	/// <summary>The most items kept, as typed. Empty leaves it to the service.</summary>
	[ObservableProperty] private string maxItems = string.Empty;

	[ObservableProperty] private Choice<NodeLastItem> sendLast = LastList[0];
	[ObservableProperty] private bool tellWhenRemoved = true;

	/// <summary>Fills the form from a node's settings.</summary>
	public void Load(NodeSettings s)
	{
		this.loaded = s;
		this.Title = s.Title;
		this.Description = s.Description;
		this.Access = AccessList.First(c => c.Value == s.Access);
		this.Publishers = PublisherList.First(c => c.Value == s.Publishers);
		this.KeepItems = s.KeepItems;
		this.MaxItems = s.MaxItems?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
		this.SendLast = LastList.First(c => c.Value == s.SendLast);
		this.TellWhenRemoved = s.TellWhenRemoved;
	}

	/// <summary>What is wrong with what was typed. Null when it can be sent.</summary>
	public string? Problem
		=> this.MaxItems.Trim().Length > 0 && (!int.TryParse(this.MaxItems.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) || n < 1)
			? "How many items to keep has to be a whole number, 1 or more. Leave it empty to let the service decide."
			: null;

	/// <summary>The settings as the form has them now.</summary>
	public NodeSettings Read() => this.loaded with
	{
		Title = this.Title.Trim(),
		Description = this.Description.Trim(),
		Access = this.Access.Value,
		Publishers = this.Publishers.Value,
		KeepItems = this.KeepItems,
		MaxItems = int.TryParse(this.MaxItems.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) && n > 0 ? n : null,
		SendLast = this.SendLast.Value,
		TellWhenRemoved = this.TellWhenRemoved,
	};

	/// <summary>Something on the form differs from what the node has.</summary>
	public bool IsChanged => this.Read() != this.loaded;
}

/// <summary>Somebody with a role on the open node.</summary>
public sealed record NodePersonRow(NodePerson Person, bool IsMe)
{
	public string Jid => this.Person.Jid;
	public string Label => this.IsMe ? this.Person.Jid + " (you)" : this.Person.Jid;
	public string RoleText => PubSubViewModel.RoleWords(this.Person.Role);
	public bool IsBlocked => this.Person.Role == NodeRole.Blocked;
}

/// <summary>Somebody who subscribes to the open node, or has asked to.</summary>
public sealed record NodeSubscriberRow(NodeSubscriber Subscriber, bool IsMe)
{
	public string Jid => this.Subscriber.Jid;
	public string Label => this.IsMe ? this.Subscriber.Jid + " (you)" : this.Subscriber.Jid;
	public bool IsPending => this.Subscriber.State == SubscriberState.Pending;
	public bool IsSettled => !this.IsPending;

	public string StateText => this.Subscriber.State switch
	{
		SubscriberState.Pending => "Asked to subscribe",
		SubscriberState.Unconfigured => "Subscribes, but has not chosen its options yet",
		_ => "Subscribes",
	};
}

// Managing a node: its settings, its people, making one and removing one.
public sealed partial class PubSubViewModel
{
	// ----- Settings -----

	private bool settingsLoaded;

	/// <summary>The open node's settings.</summary>
	public NodeSettingsForm Settings { get; }

	[ObservableProperty] private bool isSettingsLoading;

	/// <summary>The settings are there to change: the account is one of the node's owners.</summary>
	[ObservableProperty] private bool hasSettings;

	[ObservableProperty] private string? settingsError;

	/// <summary>"Saved." after a save, until something is changed again.</summary>
	[ObservableProperty] private string? settingsNote;

	private async Task LoadSettingsAsync()
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return;

		this.IsSettingsLoading = true;
		this.SettingsError = null;
		this.HasSettings = false;
		try
		{
			NodeSettings s = await this.pubsub.SettingsAsync(a.Id, n.Info);
			if (!ReferenceEquals(n, this.SelectedNode))
				return;

			this.Settings.Load(s);
			this.SettingsNote = null;
			this.HasSettings = true;
			this.settingsLoaded = true;
		}
		catch (Exception ex)
		{
			if (ReferenceEquals(n, this.SelectedNode))
				this.SettingsError = Words(ex);
		}
		finally
		{
			this.IsSettingsLoading = false;
		}
	}

	[RelayCommand]
	private async Task SaveSettings()
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return;

		if (this.Settings.Problem is string problem)
		{
			this.SettingsError = problem;
			return;
		}

		this.SettingsError = null;
		try
		{
			NodeSettings s = this.Settings.Read();
			await this.pubsub.SaveSettingsAsync(a.Id, n.Info, s);
			this.Settings.Load(s);
			this.SettingsNote = "Saved.";
			Ui.Say("Saved the settings of " + n.Node);
		}
		catch (Exception ex)
		{
			this.SettingsError = Words(ex);
		}
	}

	/// <summary>Puts the form back to what the node has.</summary>
	[RelayCommand]
	private Task ResetSettings() => this.LoadSettingsAsync();

	// ----- Removing for good: the node's name has to be typed -----

	/// <summary>Asks before every item on the node is removed.</summary>
	[RelayCommand]
	private void AskClear()
	{
		if (this.Account is not { } a || this.SelectedNode is not { IsCollection: false } n)
			return;

		this.Ask = new AskViewModel("Remove all items from " + n.Node + "?", async _ =>
		{
			await this.pubsub.ClearAsync(a.Id, n.Info);
			this.ForgetUndo();
			Ui.Say("Removed all items from " + n.Node);
			if (ReferenceEquals(n, this.SelectedNode))
				await this.LoadItemsAsync();
		})
		{
			Text = "Everything published on the node goes, for everyone. The node, its settings and its subscribers stay. This cannot be undone. Type the node's name to go on.",
			Placeholder = n.Node,
			MustMatch = n.Node,
			ConfirmText = "Remove all items",
			IsDanger = true,
		};
	}

	/// <summary>Asks before the node is deleted.</summary>
	[RelayCommand]
	private void AskDelete()
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return;

		this.Ask = new AskViewModel("Delete the node " + n.Node + "?", async _ =>
		{
			await this.pubsub.DeleteNodeAsync(a.Id, n.Info);
			Ui.Say("Deleted " + n.Node);
			this.Forget(n);
		})
		{
			Text = (n.IsCollection ? "The collection and the nodes inside it go" : "The node and everything published on it go") + ", and its subscribers are told. This cannot be undone. Type the node's name to go on.",
			Placeholder = n.Node,
			MustMatch = n.Node,
			ConfirmText = "Delete node",
			IsDanger = true,
		};
	}

	/// <summary>Takes a deleted node out of the tree.</summary>
	private void Forget(PubSubNodeRow n)
	{
		this.roots.Remove(n);
		this.strays.Remove(n);
		foreach (PubSubNodeRow r in Tree(this.roots))
			r.Children?.Remove(n);

		this.watched.Remove(n.Node);
		if (ReferenceEquals(n, this.SelectedNode))
			this.SelectedNode = null;
		this.ShowTree();
	}

	// ----- A new node -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowsPicker))]
	private bool isCreating;

	[ObservableProperty] private string newNodeName = string.Empty;

	/// <summary>The new node holds other nodes.</summary>
	[ObservableProperty] private bool newIsCollection;

	/// <summary>The collection the new node goes into, when one was open.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNewParent), nameof(NewParentText))]
	private string? newParent;

	public bool HasNewParent => this.NewParent is not null;
	public string NewParentText => "Put it inside " + this.NewParent;

	[ObservableProperty] private bool newInsideParent = true;

	/// <summary>Said when the service would not tell what a new node gets.</summary>
	[ObservableProperty] private string? newNote;

	[ObservableProperty] private string? createError;

	/// <summary>The settings the new node gets: the service's own, until changed.</summary>
	public NodeSettingsForm NewSettings { get; }

	/// <summary>Raised when the form for a new node opens, so the view can put the keyboard in it.</summary>
	public event EventHandler? CreateOpened;

	/// <summary>Opens the form for a new node, filled with what the service gives a new node.</summary>
	[RelayCommand]
	private async Task OpenCreate()
	{
		if (this.Account is not { } a || this.IsPersonal)
			return;

		string? parent = this.SelectedNode is { IsCollection: true } c ? c.Node : null;
		this.SelectedNode = null;
		this.NewNodeName = string.Empty;
		this.NewIsCollection = false;
		this.NewParent = parent;
		this.NewInsideParent = true;
		this.NewNote = null;
		this.CreateError = null;
		this.NewSettings.Load(new NodeSettings());
		this.IsCreating = true;
		this.CreateOpened?.Invoke(this, EventArgs.Empty);

		try
		{
			this.NewSettings.Load(await this.pubsub.SettingsAsync(a.Id, null));
		}
		catch (Exception)
		{
			this.NewNote = "The service did not say what it gives a new node, so these are the usual settings.";
		}
	}

	[RelayCommand]
	private void CancelCreate() => this.IsCreating = false;

	[RelayCommand]
	private async Task CreateNode()
	{
		if (this.Account is not { } a || string.IsNullOrWhiteSpace(this.NewNodeName))
			return;

		NodeSettings? settings = null;
		string? parent = null;
		if (this.IsCreating)
		{
			if (this.NewSettings.Problem is string problem)
			{
				this.CreateError = problem;
				return;
			}

			settings = this.NewSettings.Read() with { IsCollection = this.NewIsCollection };
			parent = this.NewInsideParent ? this.NewParent : null;
		}

		this.IsBusy = true;
		this.Error = null;
		this.CreateError = null;
		try
		{
			PubSubNodeInfo n = await this.pubsub.CreateNodeAsync(a.Id, this.NewNodeName.Trim(), settings, parent);
			this.NewNodeName = string.Empty;

			PubSubNodeRow? inside = parent is null ? null : Tree(this.roots).FirstOrDefault(r => r.IsCollection && r.Node == parent);
			PubSubNodeRow row = new(n, inside is null ? 0 : inside.Depth + 1);
			if (inside is null)
				this.roots.Add(row);
			else
			{
				// Read what is inside first, so the new node is not the only one that shows.
				if (inside.Children is null)
				{
					try
					{
						inside.Children = [.. (await this.pubsub.NodesAsync(a.Id, inside.Node)).Where(x => x.Node != n.Node).Select(x => new PubSubNodeRow(x, inside.Depth + 1) { IsWatched = this.watched.Contains(x.Node) })];
					}
					catch (Exception)
					{
						inside.Children = [];
					}
				}

				inside.Children.Add(row);
				inside.IsExpanded = true;
			}

			this.NodeFilter = string.Empty;
			this.WatchingOnly = false;
			this.IsCreating = false;
			this.ShowTree();
			this.SelectedNode = row;
			Ui.Say("Made " + n.Node);
		}
		catch (Exception ex)
		{
			this.Error = Words(ex);
			this.CreateError = this.Error;
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	// ----- People -----

	private bool peopleLoaded;

	public static IReadOnlyList<Choice<NodeRole>> RoleChoices { get; } =
	[
		new(NodeRole.Publisher, "May publish and read"),
		new(NodeRole.PublishOnly, "May publish, but not read"),
		new(NodeRole.Member, "May read and subscribe"),
		new(NodeRole.Owner, "Owner: may do everything"),
		new(NodeRole.Blocked, "Blocked: kept out"),
	];

	public IReadOnlyList<Choice<NodeRole>> Roles => RoleChoices;

	internal static string RoleWords(NodeRole role) => role switch
	{
		NodeRole.Owner => "Owner",
		NodeRole.Publisher => "May publish and read",
		NodeRole.PublishOnly => "May publish, but not read",
		NodeRole.Member => "May read and subscribe",
		NodeRole.Blocked => "Blocked",
		_ => "No role",
	};

	/// <summary>Who has a role on the open node.</summary>
	public ObservableCollection<NodePersonRow> People { get; } = [];

	/// <summary>Who subscribes to the open node, with those who asked to first.</summary>
	public ObservableCollection<NodeSubscriberRow> Subscribers { get; } = [];

	[ObservableProperty] private bool hasPeople;
	[ObservableProperty] private bool hasSubscribers;
	[ObservableProperty] private bool isPeopleLoading;

	/// <summary>The lists are there: the account is one of the node's owners.</summary>
	[ObservableProperty] private bool canSeePeople;

	[ObservableProperty] private string? peopleError;
	[ObservableProperty] private string newPersonJid = string.Empty;
	[ObservableProperty] private Choice<NodeRole> newPersonRole;

	private async Task LoadPeopleAsync()
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return;

		this.IsPeopleLoading = true;
		this.PeopleError = null;
		try
		{
			(IReadOnlyList<NodePerson> people, IReadOnlyList<NodeSubscriber> subscribers) = await this.pubsub.PeopleAsync(a.Id, n.Info);
			if (!ReferenceEquals(n, this.SelectedNode))
				return;

			string? me = this.Me;
			this.People.Clear();
			foreach (NodePerson p in people)
				this.People.Add(new NodePersonRow(p, string.Equals(p.Jid, me, StringComparison.OrdinalIgnoreCase)));

			this.Subscribers.Clear();
			foreach (NodeSubscriber s in subscribers)
				this.Subscribers.Add(new NodeSubscriberRow(s, string.Equals(s.Jid, me, StringComparison.OrdinalIgnoreCase)));

			this.HasPeople = this.People.Count > 0;
			this.HasSubscribers = this.Subscribers.Count > 0;
			this.CanSeePeople = true;
			this.peopleLoaded = true;
		}
		catch (Exception ex)
		{
			if (ReferenceEquals(n, this.SelectedNode))
			{
				this.CanSeePeople = false;
				this.PeopleError = Words(ex);
			}
		}
		finally
		{
			this.IsPeopleLoading = false;
		}
	}

	private async Task ChangePeople(Func<Guid, PubSubNodeRow, Task> change, string said)
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return;

		this.PeopleError = null;
		try
		{
			await change(a.Id, n);
			Ui.Say(said);
			await this.LoadPeopleAsync();
		}
		catch (Exception ex)
		{
			this.PeopleError = Words(ex);
		}
	}

	/// <summary>Gives the address in the box the role picked beside it.</summary>
	[RelayCommand]
	private Task AddPerson()
	{
		string jid = this.NewPersonJid.Trim();
		if (jid.Length == 0)
			return Task.CompletedTask;

		if (!jid.Contains('@', StringComparison.Ordinal) || jid.Contains(' ', StringComparison.Ordinal))
		{
			this.PeopleError = "That does not look like an address. An address looks like kim@example.org.";
			return Task.CompletedTask;
		}

		NodeRole role = this.NewPersonRole.Value;
		return this.ChangePeople(async (a, n) =>
		{
			await this.pubsub.SetRoleAsync(a, n.Info, jid, role);
			this.NewPersonJid = string.Empty;
		}, jid + ": " + RoleWords(role).ToLower(CultureInfo.CurrentCulture));
	}

	private Task SetRole(NodePersonRow row, NodeRole role)
		=> this.ChangePeople((a, n) => this.pubsub.SetRoleAsync(a, n.Info, row.Jid, role),
			role == NodeRole.None ? "Took " + row.Jid + " off the list" : row.Jid + ": " + RoleWords(role).ToLower(CultureInfo.CurrentCulture));

	/// <summary>Lets somebody subscribe who asked to.</summary>
	[RelayCommand]
	private Task AllowSubscriber(NodeSubscriberRow? row)
		=> row is null ? Task.CompletedTask : this.ChangePeople((a, n) => this.pubsub.SetSubscriberAsync(a, n.Info, row.Jid, SubscriberState.Subscribed), "Let " + row.Jid + " subscribe");

	/// <summary>Says no to somebody who asked, or ends a subscription.</summary>
	[RelayCommand]
	private Task EndSubscriber(NodeSubscriberRow? row)
		=> row is null ? Task.CompletedTask : this.ChangePeople((a, n) => this.pubsub.SetSubscriberAsync(a, n.Info, row.Jid, SubscriberState.None), row.IsPending ? "Said no to " + row.Jid : "Ended the subscription of " + row.Jid);

	private void PersonActions(ActionList list, NodePersonRow row)
	{
		list.Copy("Copy address", () => row.Jid, "the address", "Ctrl+C");
		foreach (Choice<NodeRole> c in RoleChoices.Where(c => c.Value != row.Person.Role))
			list.Do(c.Text, () => this.SetRole(row, c.Value), danger: c.Value == NodeRole.Blocked);
		list.Do("Take off the list", () => this.SetRole(row, NodeRole.None), danger: true, icon: "Trash");
	}

	private void SubscriberActions(ActionList list, NodeSubscriberRow row)
	{
		list.Copy("Copy address", () => row.Jid, "the address", "Ctrl+C");
		if (row.IsPending)
		{
			list.Do("Allow", () => this.AllowSubscriberCommand.ExecuteAsync(row), icon: "Check");
			list.Do("Deny", () => this.EndSubscriberCommand.ExecuteAsync(row), danger: true);
		}
		else
			list.Do("End the subscription", () => this.EndSubscriberCommand.ExecuteAsync(row), danger: true, icon: "Trash");
	}
}
