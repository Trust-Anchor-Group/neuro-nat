using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Alerts;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.ViewModels;

/// <summary>
/// One pop-up: who and what, and what you can do about it right there. A message can be answered
/// from it, a request has its answer buttons, and anything opens the right place when clicked.
/// The same pop-up shows inside the window and on the desktop. It closes by itself after a
/// while, but stays while the pointer is on it or a reply is being typed.
/// </summary>
public sealed partial class NoticeViewModel : ObservableObject
{
	private readonly DispatcherTimer life;
	private int holds;

	public NoticeViewModel(Alert alert)
	{
		this.Alert = alert;
		this.title = alert.Title;
		this.detail = alert.Detail;
		this.life = new DispatcherTimer(Lifetime(alert), DispatcherPriority.Background, (_, _) => this.TimeOut());
	}

	private static TimeSpan Lifetime(Alert a)
		=> a.IsRequest || a.Severity >= EventSeverity.Error ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(10);

	public Alert Alert { get; private set; }

	/// <summary>Pop-ups with the same group are one pop-up: a conversation, for instance.</summary>
	public string? Group => this.Alert.Group;

	[ObservableProperty] private string title;
	[ObservableProperty] private string? detail;

	/// <summary>How many more arrived while this one was showing.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Caption))]
	private int more;

	public string KindText => this.Alert.Kind switch
	{
		AlertKind.Notification => "Notification",
		AlertKind.Error => "Error",
		AlertKind.NeuronDown => "Neuron down",
		AlertKind.NeuronBack => "Neuron back",
		AlertKind.ContactRequest => "Contact request",
		AlertKind.RoomInvitation => "Room invitation",
		AlertKind.DeviceQuestion => "Device question",
		AlertKind.IdentityRequest => "Identity request",
		_ => this.Alert.IsRoom ? "Room message" : "Message",
	};

	/// <summary>"MESSAGE · 3 new"</summary>
	public string Caption => this.KindText.ToUpperInvariant() + (this.More > 0 ? $"  ·  {this.More + 1} new" : string.Empty);

	// The colour of the bar on the left: red for what is broken, amber for what needs you,
	// green for good news, the accent for people.
	public bool IsBad => this.Alert.Kind is AlertKind.Error or AlertKind.NeuronDown || (this.Alert.Kind == AlertKind.Notification && this.Alert.Severity >= EventSeverity.Error);
	public bool IsOk => this.Alert.Kind == AlertKind.NeuronBack;
	public bool IsBusy => !this.IsBad && this.Alert.Kind is AlertKind.Notification or AlertKind.DeviceQuestion or AlertKind.IdentityRequest;
	public bool IsAccent => !this.IsBad && !this.IsOk && !this.IsBusy;

	// ----- What you can do -----

	/// <summary>Opens what the pop-up is about. Set by the shell.</summary>
	public Action? Open { get; init; }

	/// <summary>Sends a reply; returns what went wrong, if anything. Set for messages.</summary>
	public Func<string, Task<string?>>? Reply { get; init; }

	public bool CanReply => this.Reply is not null;

	public string? PrimaryText { get; init; }
	public string? SecondaryText { get; init; }
	public Func<Task>? Primary { get; init; }
	public Func<Task>? Secondary { get; init; }
	public bool HasPrimary => this.Primary is not null && this.PrimaryText is not null;
	public bool HasSecondary => this.Secondary is not null && this.SecondaryText is not null;
	public bool HasActions => this.HasPrimary || this.HasSecondary;

	/// <summary>Mutes who the pop-up is from until a time. Set for messages.</summary>
	public Action<DateTime>? Mute { get; init; }

	public bool CanMute => this.Mute is not null;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SendReplyCommand))]
	private string replyText = string.Empty;

	[ObservableProperty] private string? error;

	partial void OnReplyTextChanged(string value)
	{
		// A reply being typed keeps the pop-up open.
		if (value.Length > 0)
			this.life.Stop();
	}

	private bool CanSendReply() => !string.IsNullOrWhiteSpace(this.ReplyText);

	[RelayCommand(CanExecute = nameof(CanSendReply))]
	private async Task SendReply()
	{
		if (this.Reply is null)
			return;

		string text = this.ReplyText.TrimEnd();
		try
		{
			this.Error = await this.Reply(text);
		}
		catch (Exception ex)
		{
			this.Error = ex.Message;
		}

		if (this.Error is null)
			this.Close();
	}

	[RelayCommand]
	private Task RunPrimary() => this.Run(this.Primary);

	[RelayCommand]
	private Task RunSecondary() => this.Run(this.Secondary);

	private async Task Run(Func<Task>? action)
	{
		if (action is null)
			return;

		try
		{
			await action();
			this.Close();
		}
		catch (Exception ex)
		{
			this.Error = ex.Message;
			this.life.Stop();
		}
	}

	[RelayCommand]
	private void OpenIt()
	{
		this.Close();
		this.Open?.Invoke();
	}

	[RelayCommand]
	private void MuteHour() => this.MuteUntil(DateTime.Now.AddHours(1));

	[RelayCommand]
	private void MuteTomorrow() => this.MuteUntil(DateTime.Today.AddDays(1).AddHours(8));

	[RelayCommand]
	private void MuteForGood() => this.MuteUntil(DateTime.MaxValue);

	private void MuteUntil(DateTime until)
	{
		this.Mute?.Invoke(until);
		this.Close();
	}

	// ----- Its life -----

	public bool IsClosed { get; private set; }

	/// <summary>Raised once, when the pop-up closes for any reason. UI thread.</summary>
	public event EventHandler? Closed;

	/// <summary>Starts the clock. Called when the pop-up is shown.</summary>
	public void Start()
	{
		if (!this.IsClosed && this.holds == 0 && this.ReplyText.Length == 0)
			this.life.Start();
	}

	/// <summary>The pointer is on it, or its reply box has focus: do not close yet.</summary>
	public void Hold()
	{
		this.holds++;
		this.life.Stop();
	}

	public void Release()
	{
		if (this.holds > 0)
			this.holds--;
		this.Start();
	}

	/// <summary>One more of the same arrived: show the newest, and count.</summary>
	public void Absorb(Alert newer)
	{
		this.Alert = newer;
		this.Title = newer.Title;
		this.Detail = newer.Detail;
		this.More++;
		this.life.Stop();
		this.Start();
	}

	private void TimeOut()
	{
		if (this.holds == 0 && this.ReplyText.Length == 0)
			this.Close();
	}

	[RelayCommand]
	public void Close()
	{
		if (this.IsClosed)
			return;

		this.IsClosed = true;
		this.life.Stop();
		this.Closed?.Invoke(this, EventArgs.Empty);
	}
}
