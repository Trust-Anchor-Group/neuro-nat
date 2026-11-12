using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Chat;

namespace Neuro.Nat.App.ViewModels;

/// <summary>A group of contacts, as a button above the people list that narrows the list to it.</summary>
public sealed partial class GroupChip(string name) : ObservableObject
{
	public string Name { get; } = name;

	[ObservableProperty] private bool isSelected;
}

// People: names, groups, and the panel beside a conversation.
public sealed partial class ChatViewModel
{
	// ----- Groups above the list -----

	public ObservableCollection<GroupChip> GroupChoices { get; } = [];

	/// <summary>True when the people half has groups to narrow by.</summary>
	[ObservableProperty] private bool hasGroups;

	/// <summary>The group the people list is narrowed to, when one is picked.</summary>
	[ObservableProperty] private string? groupFilter;

	[RelayCommand]
	private void PickGroup(GroupChip? g)
	{
		this.GroupFilter = g is null || string.Equals(g.Name, this.GroupFilter, StringComparison.CurrentCultureIgnoreCase) ? null : g.Name;
		this.Refresh();
	}

	private void RefreshGroups(IEnumerable<PersonViewModel> everyone)
	{
		List<string> names = [.. everyone.Where(p => !p.IsRoom).SelectMany(p => p.Groups)
			.Distinct(StringComparer.CurrentCultureIgnoreCase)
			.Order(StringComparer.CurrentCultureIgnoreCase)];

		if (this.GroupFilter is string picked && !names.Contains(picked, StringComparer.CurrentCultureIgnoreCase))
			this.GroupFilter = null;

		if (!names.SequenceEqual(this.GroupChoices.Select(g => g.Name)))
		{
			this.GroupChoices.Clear();
			foreach (string n in names)
				this.GroupChoices.Add(new GroupChip(n));
		}

		foreach (GroupChip g in this.GroupChoices)
			g.IsSelected = string.Equals(g.Name, this.GroupFilter, StringComparison.CurrentCultureIgnoreCase);

		this.HasGroups = names.Count > 0 && this.ShowPeople;
	}

	// ----- The panel beside the conversation: a person's details, or who is in the room -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsRoomPanel), nameof(IsPersonPanel))]
	private bool isPanelOpen;

	public bool IsRoomPanel => this.IsPanelOpen && this.SelectedPerson?.IsRoom == true;
	public bool IsPersonPanel => this.IsPanelOpen && this.SelectedPerson is { IsRoom: false };

	// A room shows who is in it unless you close that; a person's details open when asked for.
	private bool roomPanelWanted = true;
	private bool personPanelWanted;
	private bool personPanelChosen;
	private bool narrow;
	private bool wide;

	/// <summary>
	/// Told by the view when the conversation has room for a third column. Then a person's
	/// details open by themselves, until you close them.
	/// </summary>
	public void SetWide(bool wide)
	{
		if (this.wide == wide)
			return;

		this.wide = wide;
		this.ApplyPanel();
	}

	/// <summary>Told by the view when the conversation is too narrow to share its width with a panel.</summary>
	public void SetNarrow(bool narrow)
	{
		if (this.narrow == narrow)
			return;

		this.narrow = narrow;
		this.ApplyPanel();
	}

	private void ApplyPanel()
	{
		this.IsPanelOpen = !this.narrow && this.SelectedPerson is PersonViewModel p && (p.IsRoom ? this.roomPanelWanted : this.personPanelChosen ? this.personPanelWanted : this.wide);
		this.OnPropertyChanged(nameof(this.IsRoomPanel));
		this.OnPropertyChanged(nameof(this.IsPersonPanel));
		this.LoadDetails();
	}

	[RelayCommand]
	private void TogglePanel()
	{
		if (this.SelectedPerson is not PersonViewModel p)
			return;

		this.IsPanelOpen = !this.IsPanelOpen;
		if (!this.narrow)
		{
			if (p.IsRoom)
				this.roomPanelWanted = this.IsPanelOpen;
			else
			{
				this.personPanelWanted = this.IsPanelOpen;
				this.personPanelChosen = true;
			}
		}

		this.LoadDetails();
	}

	private void ShowPanel()
	{
		if (!this.IsPanelOpen)
			this.TogglePanel();
	}

	// ----- A person's details -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanSaveName))]
	private string detailName = string.Empty;

	[ObservableProperty] private string newGroup = string.Empty;
	[ObservableProperty] private string? detailError;

	/// <summary>The open conversation is with someone who wrote without being a contact.</summary>
	[ObservableProperty] private bool canAddOpen;

	[ObservableProperty] private bool hasOtherGroups;

	/// <summary>Groups the account already has that this person is not in: offered while a group is typed.</summary>
	public ObservableCollection<string> OtherGroups { get; } = [];

	public bool CanSaveName => this.SelectedPerson is { InRoster: true } p && this.DetailName.Trim() != p.Name;

	private PersonViewModel? detailsOf;

	private void LoadDetails()
	{
		PersonViewModel? p = this.IsPersonPanel ? this.SelectedPerson : null;
		if (!ReferenceEquals(p, this.detailsOf))
		{
			this.detailsOf = p;
			this.DetailName = p?.Name ?? string.Empty;
			this.NewGroup = string.Empty;
			this.DetailError = null;
		}

		List<string> others = p is null ? [] : [.. this.chat.Groups(p.AccountId).Where(g => !p.Groups.Contains(g, StringComparer.CurrentCultureIgnoreCase))];
		if (!others.SequenceEqual(this.OtherGroups))
		{
			this.OtherGroups.Clear();
			foreach (string g in others)
				this.OtherGroups.Add(g);
		}

		this.HasOtherGroups = this.OtherGroups.Count > 0;
		this.CanAddOpen = p is { IsPerson: true, InRoster: false, IsNeuron: false };
		this.OnPropertyChanged(nameof(this.CanSaveName));
	}

	private async Task Change(Func<PersonViewModel, Task> change)
	{
		if (this.SelectedPerson is not PersonViewModel p)
			return;

		try
		{
			this.DetailError = null;
			await change(p);
			this.Refresh();
		}
		catch (Exception ex)
		{
			this.DetailError = ex.Message;
		}
	}

	/// <summary>Gives the contact the name in the box. An empty name shows their address again.</summary>
	[RelayCommand]
	private Task SaveName() => this.Change(async p =>
	{
		await this.chat.RenameContactAsync(p.AccountId, p.BareJid, this.DetailName);
		p.Name = this.DetailName.Trim();
		this.DetailName = p.Name;
	});

	/// <summary>Puts the contact in the group typed in the box. A new name makes a new group.</summary>
	[RelayCommand]
	private Task AddGroup() => this.Change(async p =>
	{
		string g = this.NewGroup.Trim();
		if (g.Length == 0)
			return;

		await this.chat.SetGroupsAsync(p.AccountId, p.BareJid, [.. p.Groups, g]);
		this.NewGroup = string.Empty;
	});

	/// <summary>Puts the contact in one of the groups the account already has.</summary>
	[RelayCommand]
	private Task AddToGroup(string? group) => this.Change(async p =>
	{
		if (group is not null)
			await this.chat.SetGroupsAsync(p.AccountId, p.BareJid, [.. p.Groups, group]);
	});

	[RelayCommand]
	private Task RemoveGroup(string? group) => this.Change(async p =>
	{
		if (group is not null)
			await this.chat.SetGroupsAsync(p.AccountId, p.BareJid, p.Groups.Where(g => !string.Equals(g, group, StringComparison.CurrentCultureIgnoreCase)));
	});

	[RelayCommand]
	private Task CopyAddress() => this.SelectedPerson is PersonViewModel p ? Ui.Copy(p.BareJid, "the address") : Task.CompletedTask;

	private async Task AddToContacts(PersonViewModel p)
	{
		await this.chat.AddContactAsync(p.AccountId, p.BareJid, null);
		Ui.Say("Asked " + p.Label + " to share presence");
		this.Refresh();
	}

	/// <summary>Makes whoever wrote to you a contact, and asks to see when they are online.</summary>
	[RelayCommand]
	private Task AddOpenToContacts() => this.Change(this.AddToContacts);

	private void AskRename(PersonViewModel p)
	{
		this.Show(p);
		this.Ask = new AskViewModel("Rename " + p.Label, async name =>
		{
			await this.chat.RenameContactAsync(p.AccountId, p.BareJid, name);
			this.Refresh();
			this.detailsOf = null;
			this.LoadDetails();
		})
		{
			Placeholder = "Name",
			Input = p.Name,
			ConfirmText = "Rename",
		};
	}

	private void AskRemoveContact(PersonViewModel p)
	{
		this.Show(p);
		this.Ask = new AskViewModel("Remove " + p.Label + " from your contacts?", async _ =>
		{
			await this.chat.RemoveContactAsync(p.AccountId, p.BareJid);
			Ui.Say("Removed " + p.Label + " from your contacts");
			this.Refresh();
		})
		{
			Text = "What you wrote to each other stays here.",
			ConfirmText = "Remove from contacts",
			IsDanger = true,
		};
	}

	[RelayCommand]
	private void RemoveOpenContact()
	{
		if (this.SelectedPerson is { InRoster: true } p)
			this.AskRemoveContact(p);
	}

	/// <summary>Opens the conversation an action is about, when it is not the open one already.</summary>
	private void Show(PersonViewModel p)
	{
		if (!ReferenceEquals(this.SelectedPerson, p))
			this.Open(p.AccountId, p.BareJid);
	}

	/// <summary>From a conversation inside a room: back to the room itself.</summary>
	[RelayCommand]
	private void OpenRoomOfPrivate()
	{
		if (this.SelectedPerson is { IsPrivate: true } p)
			this.Open(p.AccountId, p.BareJid.Split('/')[0]);
	}

	/// <summary>What a person's row and the conversation's "more" button offer for a person.</summary>
	private void PersonActions(ActionList list, PersonViewModel p)
	{
		if (p.IsPrivate)
		{
			list.GoTo("The room " + p.BareJid.Split('@')[0], () => this.Open(p.AccountId, p.BareJid.Split('/')[0]), "Room");
			return;
		}

		if (p.InRoster)
		{
			list.Do("Rename…", () => this.AskRename(p), icon: "Edit");
			list.Do("Details and groups", () =>
			{
				this.Show(p);
				this.ShowPanel();
			}, icon: "Person");
			list.Do("Remove from contacts…", () => this.AskRemoveContact(p), danger: true, icon: "Trash");
		}
		else if (!p.IsNeuron)
			list.Do("Add to contacts", () => this.AddToContacts(p), icon: "Plus");
	}
}
