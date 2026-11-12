using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Chat;
using Waher.Networking.XMPP.DataForms;

namespace Neuro.Nat.App.ViewModels;

/// <summary>Someone in the open room, as a row of the panel beside it.</summary>
public sealed class OccupantViewModel(Guid accountId, string roomJid, RoomOccupant occupant, bool isMe)
{
	public Guid AccountId { get; } = accountId;
	public string RoomJid { get; } = roomJid;
	public RoomOccupant Occupant { get; } = occupant;
	public bool IsMe { get; } = isMe;

	public string Nick => this.Occupant.NickName;

	/// <summary>Their own address, when the room shows it.</summary>
	public string? Address => this.Occupant.BareJid;

	public bool HasAddress => this.Address is not null;

	/// <summary>"Owner" or "Admin", for the ones who run the room.</summary>
	public string? Standing => this.Occupant.IsOwner ? "Owner" : this.Occupant.IsAdmin ? "Admin" : null;

	public bool HasStanding => this.Standing is not null;

	/// <summary>Set on the first row of each part of the list: "Moderators", "Participants", "Visitors".</summary>
	public string? Header { get; init; }

	public bool HasHeader => this.Header is not null;
}

// Rooms: who is in, what you may do to them, and the room's own menu.
public sealed partial class ChatViewModel
{
	private string occupantsShown = string.Empty;

	private void RefreshOccupants()
	{
		RoomInfo? room = this.SelectedPerson is { IsRoom: true } p ? this.chat.Room(p.AccountId, p.BareJid) : null;
		IReadOnlyList<RoomOccupant> all = room?.Occupants ?? [];

		// Rebuilt only when somebody came, went or changed, so an open menu is not pulled away.
		string now = room is null ? string.Empty : room.RoomJid + "|" + room.NickName + "|" + string.Join(',', all.Select(o => o.NickName + ":" + o.Role + ":" + o.Affiliation + ":" + o.RealJid));
		if (now == this.occupantsShown)
			return;

		this.occupantsShown = now;
		this.Occupants.Clear();
		if (room is null)
			return;

		(string Title, Func<RoomOccupant, bool> Is)[] parts =
		[
			("Moderators", o => o.IsModerator),
			("Participants", o => !o.IsModerator && !o.IsVisitor),
			("Visitors · can only read", o => o.IsVisitor),
		];

		foreach ((string title, Func<RoomOccupant, bool> isIn) in parts)
		{
			bool first = true;
			foreach (RoomOccupant o in all.Where(isIn))
			{
				bool me = string.Equals(o.NickName, room.NickName, StringComparison.OrdinalIgnoreCase);
				this.Occupants.Add(new OccupantViewModel(room.AccountId, room.RoomJid, o, me) { Header = first ? title : null });
				first = false;
			}
		}
	}

	/// <summary>In a room where you may only read: asks its moderators to let you write.</summary>
	[RelayCommand]
	private async Task AskToSpeak()
	{
		if (this.SelectedPerson is not { IsRoom: true } p)
			return;

		try
		{
			await this.chat.AskToSpeakAsync(p.AccountId, p.BareJid);
			Ui.Say("Asked the moderators of " + p.Label + " to let you write");
		}
		catch (Exception ex)
		{
			this.SendError = ex.Message;
		}
	}

	// ----- What a room's row and its "more" button offer -----

	private void RoomActions(ActionList list, PersonViewModel p)
	{
		RoomInfo? room = this.chat.Room(p.AccountId, p.BareJid);
		RoomOccupant? me = room?.Me;
		bool isIn = room?.Joined == true;

		if (isIn)
		{
			list.Do("Change subject…", () => this.AskSubject(p), icon: "Edit");
			list.Do("Invite someone…", () => this.AskInvite(p), icon: "Plus");

			if (me?.IsVisitor == true)
			{
				list.Do("Ask to speak", async () =>
				{
					await this.chat.AskToSpeakAsync(p.AccountId, p.BareJid);
					Ui.Say("Asked the moderators of " + p.Label + " to let you write");
				}, icon: "Mention");
			}

			// The room refuses these to anyone but the ones who run it, so the others are not offered them.
			if (me?.IsOwner == true)
				list.Do("Room settings…", () => this.OpenRoomSettings(p), icon: "Settings");
			if (me?.IsAdmin == true)
				list.Do("Banned list…", () => this.OpenBanned(p), icon: "Lock");
		}

		list.Do("Leave room…", () => this.AskLeave(p), danger: true, icon: "Unplug");
		if (isIn && me?.IsOwner == true)
			list.Do("Delete room…", () => this.AskDeleteRoom(p), danger: true, icon: "Trash");
	}

	private void AskSubject(PersonViewModel p)
	{
		this.Show(p);
		this.Ask = new AskViewModel("Change the subject of " + p.Label, async subject =>
		{
			await this.chat.SetSubjectAsync(p.AccountId, p.BareJid, subject);
			Ui.Say(subject.Length == 0 ? "Cleared the subject" : "Changed the subject");
			this.Refresh();
		})
		{
			Text = "The subject is a line everyone in the room sees at its top.",
			Placeholder = "Subject",
			Input = p.Subject ?? string.Empty,
			ConfirmText = "Change subject",
		};
	}

	private void AskInvite(PersonViewModel p)
	{
		this.Show(p);
		this.Ask = new AskViewModel("Invite someone to " + p.Label, async jid =>
		{
			await this.chat.InviteAsync(p.AccountId, p.BareJid, jid);
			Ui.Say("Invited " + jid);
		})
		{
			Text = "They get an invitation they can accept or decline.",
			Placeholder = "Address, such as kim@lab.local",
			NeedsText = true,
			ConfirmText = "Invite",
		};
	}

	private void AskLeave(PersonViewModel p)
	{
		this.Show(p);
		this.Ask = new AskViewModel("Leave " + p.Label + "?", async _ =>
		{
			await this.chat.LeaveRoomAsync(p.AccountId, p.BareJid);
			this.SelectedPerson = null;
			this.Refresh();
			Ui.Say("Left " + p.Label);
		})
		{
			Text = "You stop getting its messages, and the ones kept in this app go. You can join again later.",
			ConfirmText = "Leave room",
			IsDanger = true,
		};
	}

	private void AskDeleteRoom(PersonViewModel p)
	{
		this.Show(p);
		int others = Math.Max(0, p.OccupantCount - 1);
		this.Ask = new AskViewModel("Delete the room " + p.Label + " for everyone?", async _ =>
		{
			await this.chat.DeleteRoomAsync(p.AccountId, p.BareJid);
			this.SelectedPerson = null;
			this.Refresh();
			Ui.Say("Deleted the room " + p.Label);
		})
		{
			Text = "The room goes, with everything written in it" + (others > 0 ? ", and " + Ui.Count(others, "other person", "other people") + " in it " + (others == 1 ? "is" : "are") + " put out" : string.Empty)
				+ ". This cannot be undone. Type the room's name, " + p.Label + ", to go ahead.",
			Placeholder = p.Label,
			MustMatch = p.Label,
			ConfirmText = "Delete room",
			IsDanger = true,
		};
	}

	// ----- What can be done to someone in the room -----

	private static int Rank(RoomOccupant o) => o.IsOwner ? 2 : o.IsAdmin ? 1 : 0;

	private void OccupantActions(ActionList list, OccupantViewModel who)
	{
		RoomOccupant o = who.Occupant;
		RoomOccupant? me = this.chat.Room(who.AccountId, who.RoomJid)?.Me;

		if (!who.IsMe)
			list.Open("Message privately", () => this.MessagePrivately(who), key: null, icon: "Chat");

		list.Copy("Copy name", () => o.NickName, "the name");
		if (who.Address is string address)
			list.Copy("Copy address", () => address, "the address");

		if (who.IsMe || me is null)
			return;

		if (who.Address is string jid && !this.chat.Contacts(who.AccountId).Any(c => string.Equals(c.BareJid, jid, StringComparison.OrdinalIgnoreCase)))
		{
			list.Do("Add to contacts", async () =>
			{
				await this.chat.AddContactAsync(who.AccountId, jid, o.NickName);
				Ui.Say("Asked " + o.NickName + " to share presence");
			}, icon: "Plus");
		}

		// A moderator runs the room for everyone below them; the ones who own or administer it
		// answer only to those above. What you may not do is left out, not greyed.
		bool above = Rank(me) > Rank(o) || Rank(o) == 0;
		if (!me.IsModerator || !above)
			return;

		List<ItemAction> roles = [];
		void Role(string text, RoomRole role, bool current)
			=> roles.Add(new ItemAction(text, ActionGroup.Do, () => this.SetRole(who, role)) { IsEnabled = !current, Icon = current ? "Check" : null });

		Role("May only read (visitor)", RoomRole.Visitor, o.IsVisitor);
		Role("May read and write (participant)", RoomRole.Participant, !o.IsVisitor && !o.IsModerator);
		if (me.IsAdmin)
			Role("May also run the room (moderator)", RoomRole.Moderator, o.IsModerator);

		list.Sub("Change what they may do", ActionGroup.Do, roles, "Key");
		list.Do("Remove from room…", () => this.AskRemove(who), danger: true, icon: "Unplug");

		if (me.IsAdmin && who.Address is not null)
			list.Do("Ban…", () => this.AskBan(who), danger: true, icon: "Lock");
	}

	private async Task SetRole(OccupantViewModel who, RoomRole role)
	{
		await this.chat.SetRoleAsync(who.AccountId, who.RoomJid, who.Nick, role);
		Ui.Say(who.Nick + (role switch
		{
			RoomRole.Visitor => " may now only read",
			RoomRole.Moderator => " is now a moderator",
			_ => " may now read and write",
		}));
		this.Refresh();
	}

	private void AskRemove(OccupantViewModel who)
	{
		this.Ask = new AskViewModel("Remove " + who.Nick + " from the room?", async reason =>
		{
			await this.chat.RemoveFromRoomAsync(who.AccountId, who.RoomJid, who.Nick, reason);
			Ui.Say("Removed " + who.Nick + " from the room");
			this.Refresh();
		})
		{
			Text = who.Nick + " is put out now, and can come back in. To keep someone out, ban them.",
			Placeholder = "Reason (optional, they see it)",
			ConfirmText = "Remove from room",
			IsDanger = true,
		};
	}

	private void AskBan(OccupantViewModel who)
	{
		if (who.Address is not string address)
			return;

		this.Ask = new AskViewModel("Ban " + who.Nick + " from the room?", async reason =>
		{
			await this.chat.BanAsync(who.AccountId, who.RoomJid, address, reason);
			Ui.Say("Banned " + address);
			this.Refresh();
		})
		{
			Text = who.Nick + " (" + address + ") is put out and not let in again until the ban is lifted, from the room's banned list.",
			Placeholder = "Reason (optional, they see it)",
			ConfirmText = "Ban",
			IsDanger = true,
		};
	}

	/// <summary>Conversations inside a room that were opened here before anything was written in them.</summary>
	private readonly HashSet<string> started = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Opens a conversation with one person in the room. It goes through the room, to them alone.</summary>
	private void MessagePrivately(OccupantViewModel who)
	{
		string jid = ChatService.PrivateJid(who.RoomJid, who.Nick);
		Conversation c = this.chat.Get(who.AccountId, jid);
		this.started.Add(c.AccountId.ToString("N") + "|" + c.BareJid);
		this.Open(who.AccountId, jid);
		this.ComposerFocusWanted?.Invoke();
	}

	// ----- The banned list -----

	public ObservableCollection<string> Banned { get; } = [];

	[ObservableProperty] private bool isBannedOpen;
	[ObservableProperty] private string? bannedNote;

	private async Task OpenBanned(PersonViewModel p)
	{
		this.Show(p);
		this.ShowPanel();
		this.IsBannedOpen = true;
		this.Banned.Clear();
		this.BannedNote = "Asking the room…";
		try
		{
			IReadOnlyList<string> banned = await this.chat.BannedAsync(p.AccountId, p.BareJid);
			foreach (string jid in banned)
				this.Banned.Add(jid);

			this.BannedNote = banned.Count == 0 ? "Nobody is banned from this room." : null;
		}
		catch (Exception ex)
		{
			this.BannedNote = ex is TimeoutException ? "The room did not answer in time." : ex.Message;
		}
	}

	[RelayCommand]
	private void CloseBanned() => this.IsBannedOpen = false;

	/// <summary>Lifts a ban: the address may come into the room again.</summary>
	[RelayCommand]
	private async Task LetBackIn(string? jid)
	{
		if (jid is null || this.SelectedPerson is not { IsRoom: true } p)
			return;

		try
		{
			await this.chat.UnbanAsync(p.AccountId, p.BareJid, jid);
			this.Banned.Remove(jid);
			this.BannedNote = this.Banned.Count == 0 ? "Nobody is banned from this room." : null;
			Ui.Say(jid + " may come in again");
		}
		catch (Exception ex)
		{
			this.BannedNote = ex.Message;
		}
	}

	// ----- The room's settings: the form its service gives -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsRoomFormOpen))]
	private DataForm? roomForm;

	[ObservableProperty] private string? roomFormError;
	[ObservableProperty] private bool isRoomFormBusy;
	[ObservableProperty] private string roomFormTitle = string.Empty;

	public bool IsRoomFormOpen => this.RoomForm is not null;

	private PersonViewModel? roomFormOf;

	private async Task OpenRoomSettings(PersonViewModel p)
	{
		this.Show(p);
		this.RoomFormError = null;
		this.RoomForm = await this.chat.RoomSettingsAsync(p.AccountId, p.BareJid);
		this.RoomFormTitle = "Settings of " + p.Label;
		this.roomFormOf = p;
	}

	[RelayCommand]
	private async Task SaveRoomForm()
	{
		if (this.RoomForm is not DataForm form || this.roomFormOf is not PersonViewModel p)
			return;

		this.IsRoomFormBusy = true;
		try
		{
			await this.chat.SaveRoomSettingsAsync(p.AccountId, p.BareJid, form);
			this.RoomForm = null;
			Ui.Say("Saved the settings of " + p.Label);
		}
		catch (Exception ex)
		{
			this.RoomFormError = ex.Message;
		}
		finally
		{
			this.IsRoomFormBusy = false;
		}
	}

	[RelayCommand]
	private void CancelRoomForm() => this.RoomForm = null;
}
