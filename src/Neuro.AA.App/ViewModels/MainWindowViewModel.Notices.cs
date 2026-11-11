using System.Collections.ObjectModel;
using Neuro.AA.Engine.Alerts;
using Neuro.AA.Engine.Chat;
using Neuro.AA.Engine.Devices;
using Neuro.AA.Engine.Identity;

namespace Neuro.AA.App.ViewModels;

/// <summary>
/// Pop-ups: what an alert turns into on screen. The shell makes them, with everything they can
/// do wired up; the window shows them in its corner, and the tray agent shows the same ones on
/// the desktop when the window is not in front.
/// </summary>
public sealed partial class MainWindowViewModel
{
	private const int MaxInApp = 3;
	private readonly List<NoticeViewModel> live = [];

	/// <summary>The pop-ups showing inside the window, oldest first.</summary>
	public ObservableCollection<NoticeViewModel> Notices { get; } = [];

	/// <summary>
	/// True when you are looking at what the alert is about: the message arrived in the
	/// conversation that is open in front of you. Then nothing pops up and nothing sounds.
	/// </summary>
	public bool IsLookingAt(Alert a)
		=> a.Kind == AlertKind.Message && this.Chat.IsWatched && a.AccountId is Guid account && this.Chat.SelectedPerson is PersonViewModel p &&
			p.AccountId == account && string.Equals(p.BareJid, a.Target, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// True when the list in front of you already shows it: a request or a notification while
	/// the Inbox is open. It may still make its sound, but needs no pop-up.
	/// </summary>
	public bool ShowsItself(Alert a)
		=> this.IsWindowActive && this.IsAttention && (a.IsRequest || a.Kind == AlertKind.Notification) && !a.Id.StartsWith("test:", StringComparison.Ordinal);

	/// <summary>
	/// Turns an alert into a pop-up, or adds it to the pop-up already showing for the same
	/// conversation (then <paramref name="isNew"/> is false and there is nothing more to show).
	/// </summary>
	public NoticeViewModel Notice(Alert a, out bool isNew)
	{
		if (a.Group is string group && this.live.FirstOrDefault(n => n.Group == group) is NoticeViewModel same)
		{
			same.Absorb(a);
			isNew = false;
			return same;
		}

		isNew = true;
		NoticeViewModel notice = this.Create(a);
		this.live.Add(notice);
		notice.Closed += (_, _) =>
		{
			this.live.Remove(notice);
			this.Notices.Remove(notice);
		};
		return notice;
	}

	/// <summary>Shows a pop-up in the corner of the window. At most three show; the oldest makes room.</summary>
	public void ShowInApp(NoticeViewModel notice)
	{
		while (this.Notices.Count >= MaxInApp)
			this.Notices[0].Close();

		this.Notices.Add(notice);
		notice.Start();
	}

	private NoticeViewModel Create(Alert a)
	{
		void Open() => this.OpenRequested(a);

		switch (a.Kind)
		{
			case AlertKind.Message when a.AccountId is Guid account && a.Target is string jid:
				return new NoticeViewModel(a)
				{
					Open = Open,
					Reply = async text =>
					{
						ChatMessage sent = await this.chatService.SendAsync(account, jid, text);
						if (sent.Error is null && this.Conversation(account, jid, a.IsRoom) is Conversation c)
							this.chatService.MarkRead(c);
						return sent.Error;
					},
					Mute = until =>
					{
						if (this.Conversation(account, jid, a.IsRoom) is Conversation c)
							this.chatService.SetMuted(c, until);
					},
				};

			case AlertKind.ContactRequest when this.chatService.Requests.FirstOrDefault(r => r.AccountId == a.AccountId && string.Equals(r.BareJid, a.Target, StringComparison.OrdinalIgnoreCase)) is SubscriptionRequest request:
				return new NoticeViewModel(a)
				{
					Open = Open,
					PrimaryText = "Accept",
					Primary = () => this.chatService.AcceptAsync(request),
					SecondaryText = "Decline",
					Secondary = () => this.chatService.DeclineAsync(request),
				};

			case AlertKind.RoomInvitation when this.chatService.Invitations.FirstOrDefault(i => i.AccountId == a.AccountId && string.Equals(i.RoomJid, a.Target, StringComparison.OrdinalIgnoreCase)) is RoomInvitation invitation:
				return new NoticeViewModel(a)
				{
					Open = Open,
					PrimaryText = "Join",
					Primary = () => this.chatService.AcceptInvitationAsync(invitation,
						this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == invitation.AccountId)?.Profile.UserName ?? "operator"),
					SecondaryText = "Decline",
					Secondary = () => this.chatService.DeclineInvitationAsync(invitation),
				};

			case AlertKind.DeviceQuestion when this.devicesService.Questions.FirstOrDefault(q => q.Id == a.Target) is DeviceQuestion question:
				return new NoticeViewModel(a)
				{
					Open = Open,
					PrimaryText = "Allow",
					Primary = () => this.devicesService.AnswerAsync(question, true, AnswerScope.Caller),
					SecondaryText = "Deny",
					Secondary = () => this.devicesService.AnswerAsync(question, false, AnswerScope.Caller),
				};

			case AlertKind.IdentityRequest:
				return new NoticeViewModel(a)
				{
					Open = Open,
					PrimaryText = "Review",
					Primary = () =>
					{
						Open();
						return Task.CompletedTask;
					},
				};

			default:
				return new NoticeViewModel(a) { Open = Open };
		}
	}

	private Conversation? Conversation(Guid account, string jid, bool room)
		=> room ? this.chatService.GetRoom(account, jid) : this.chatService.Find(account, jid);

	/// <summary>Set by the tray agent: brings the window forward before the alert opens.</summary>
	public Action? BringForward { get; set; }

	private void OpenRequested(Alert a)
	{
		this.BringForward?.Invoke();
		this.OpenAlert(a);
	}

	/// <summary>
	/// Reading something anywhere clears it everywhere: a pop-up closes once its message is
	/// read, or its request is answered, somewhere else.
	/// </summary>
	private void PruneNotices()
	{
		if (this.live.Count == 0)
			return;

		foreach (NoticeViewModel n in this.live.ToList())
		{
			Alert a = n.Alert;
			if (a.Id.StartsWith("test:", StringComparison.Ordinal))
				continue;

			bool stillThere = a.Kind switch
			{
				AlertKind.Message when a.AccountId is Guid account && a.Target is string jid => this.Conversation(account, jid, a.IsRoom) is Conversation c && c.Unread > 0,
				AlertKind.ContactRequest => this.chatService.Requests.Any(r => r.AccountId == a.AccountId && string.Equals(r.BareJid, a.Target, StringComparison.OrdinalIgnoreCase)),
				AlertKind.RoomInvitation => this.chatService.Invitations.Any(i => i.AccountId == a.AccountId && string.Equals(i.RoomJid, a.Target, StringComparison.OrdinalIgnoreCase)),
				AlertKind.DeviceQuestion => this.devicesService.Questions.Any(q => q.Id == a.Target),
				AlertKind.IdentityRequest => this.identityService.Petitions.Any(p => p.PetitionId == a.Target),
				_ => true,
			};

			if (!stillThere)
				n.Close();
		}
	}
}
