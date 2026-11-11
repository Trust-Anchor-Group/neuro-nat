using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Neuro.AA.App.ViewModels;

/// <summary>What the Back and Forward buttons hand to the right-click menu: the stops behind or ahead.</summary>
public sealed class HistoryMenu
{
	public static HistoryMenu Back { get; } = new();
	public static HistoryMenu Forward { get; } = new();
}

/// <summary>
/// Back and forward. Every screen you land on is a stop: the screen, and the Neuron, account or
/// conversation it showed. Back walks through the stops the way a browser does, and going
/// somewhere new from the middle drops what was ahead.
/// </summary>
public sealed partial class MainWindowViewModel
{
	private const int MaxStops = 60;

	private sealed record Stop(WorkspaceKind Screen, Guid? Account, string? Jid, string Name);

	private readonly List<Stop> stops = [];
	private int stopAt = -1;
	private bool stopQueued;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
	private bool canGoBack;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
	private bool canGoForward;

	/// <summary>"Back to Events (Alt+Left)"</summary>
	[ObservableProperty] private string backTip = "Back (Alt+Left)";

	[ObservableProperty] private string forwardTip = "Forward (Alt+Right)";

	private static bool Same(Stop a, Stop b)
		=> a.Screen == b.Screen && a.Account == b.Account && string.Equals(a.Jid, b.Jid, StringComparison.OrdinalIgnoreCase);

	/// <summary>Where the window is right now.</summary>
	private Stop Here()
	{
		WorkspaceKind screen = this.Workspace;
		if (IsPageScreen(screen) && this.Page is NeuronPageViewModel p)
		{
			string tab = screen switch
			{
				WorkspaceKind.NeuronEvents => ", Events",
				WorkspaceKind.Sources => ", Sources",
				WorkspaceKind.NeuronConsole => ", Console",
				WorkspaceKind.Updates => ", Software",
				WorkspaceKind.PubSub => ", Publish/subscribe",
				WorkspaceKind.Identities => ", Identity",
				_ => string.Empty,
			};
			return new Stop(screen, p.AccountId, p.BareJid, p.Label + tab);
		}

		switch (PlaceOf(screen))
		{
			case PlaceKind.Accounts:
				string what = screen switch
				{
					WorkspaceKind.Devices => ", Devices",
					WorkspaceKind.AccountIdentities => ", Identities",
					WorkspaceKind.AccountStorage => ", Storage",
					_ => string.Empty,
				};
				return this.SelectedAccount is AccountItemViewModel a
					? new Stop(screen, a.Session.Profile.Id, null, a.Label + what)
					: new Stop(screen, null, null, "Accounts");

			case PlaceKind.Chat:
				return this.Chat.SelectedPerson is PersonViewModel who
					? new Stop(screen, who.AccountId, who.BareJid, (who.IsRoom ? "the room " : "the chat with ") + who.Label)
					: new Stop(screen, null, null, "Chat");

			default:
				return new Stop(screen, null, null, screen switch
				{
					WorkspaceKind.Attention => "the Inbox",
					WorkspaceKind.Fleet => "the Neurons list",
					WorkspaceKind.Topology => "the map",
					WorkspaceKind.Events => "Events",
					WorkspaceKind.Console => "the Console",
					WorkspaceKind.Settings => "Settings",
					_ => this.Place.ToString(),
				});
		}
	}

	/// <summary>
	/// Something that makes up a stop changed. One gesture often changes several of them (the
	/// Neuron, then the screen), so the stop is written once, after the gesture.
	/// </summary>
	private void NoteStop()
	{
		if (this.stopQueued)
			return;

		this.stopQueued = true;
		Dispatcher.UIThread.Post(this.WriteStop);
	}

	private void WriteStop()
	{
		this.stopQueued = false;
		Stop now = this.Here();

		if (this.stopAt >= 0)
		{
			Stop last = this.stops[this.stopAt];
			if (Same(last, now))
			{
				this.stops[this.stopAt] = now;
				this.DescribeStops();
				return;
			}

			// Chat opening its first conversation on its own is still the same stop.
			if (last.Screen == now.Screen && last.Account is null && last.Jid is null)
			{
				this.stops[this.stopAt] = now;
				this.DescribeStops();
				return;
			}
		}

		this.stops.RemoveRange(this.stopAt + 1, this.stops.Count - this.stopAt - 1);
		this.stops.Add(now);
		if (this.stops.Count > MaxStops)
			this.stops.RemoveRange(0, this.stops.Count - MaxStops);

		this.stopAt = this.stops.Count - 1;
		this.DescribeStops();
	}

	private void DescribeStops()
	{
		this.CanGoBack = this.stopAt > 0;
		this.CanGoForward = this.stopAt >= 0 && this.stopAt < this.stops.Count - 1;
		this.BackTip = this.CanGoBack ? "Back to " + this.stops[this.stopAt - 1].Name + " (Alt+Left)" : "Back (Alt+Left)";
		this.ForwardTip = this.CanGoForward ? "Forward to " + this.stops[this.stopAt + 1].Name + " (Alt+Right)" : "Forward (Alt+Right)";
	}

	/// <summary>Shows a stop again. False when what it showed is gone: a Neuron or an account that was removed.</summary>
	private bool Restore(Stop stop)
	{
		if (IsPageScreen(stop.Screen))
		{
			if (stop.Jid is null || this.RowOf(stop.Jid, stop.Account) is null)
				return false;

			this.OpenNeuron(stop.Jid, stop.Account, stop.Screen);
			return true;
		}

		switch (PlaceOf(stop.Screen))
		{
			case PlaceKind.Accounts:
				if (stop.Account is Guid id)
				{
					if (this.Accounts.FirstOrDefault(a => a.Session.Profile.Id == id) is not AccountItemViewModel account)
						return false;

					this.SelectedAccount = account;
				}

				this.Show(stop.Screen);
				return true;

			case PlaceKind.Chat:
				this.ShowChat();
				if (stop.Account is Guid chatAccount && stop.Jid is string who)
					this.Chat.Open(chatAccount, who);
				return true;

			default:
				this.Show(stop.Screen);
				return true;
		}
	}

	private void Walk(int step)
	{
		// What is on screen is a stop before it is left.
		this.WriteStop();

		int at = this.stopAt + step;
		while (at >= 0 && at < this.stops.Count)
		{
			if (this.Restore(this.stops[at]))
			{
				this.stopAt = at;
				this.stops[at] = this.Here();
				this.DescribeStops();
				return;
			}

			// Gone: drop it and try the one after.
			this.stops.RemoveAt(at);
			if (step < 0)
			{
				this.stopAt--;
				at--;
			}
		}

		this.DescribeStops();
	}

	/// <summary>Alt+Left, the Back button, the mouse's back button.</summary>
	[RelayCommand(CanExecute = nameof(CanGoBack))]
	private void GoBack() => this.Walk(-1);

	/// <summary>Alt+Right, the Forward button, the mouse's forward button.</summary>
	[RelayCommand(CanExecute = nameof(CanGoForward))]
	private void GoForward() => this.Walk(1);

	/// <summary>The stops behind or ahead, nearest first, for the right-click menu of the two buttons.</summary>
	private IReadOnlyList<ItemAction> StopsMenu(bool back)
	{
		this.WriteStop();
		ActionList list = [];
		for (int i = 1; i <= 12; i++)
		{
			int at = this.stopAt + (back ? -i : i);
			if (at < 0 || at >= this.stops.Count)
				break;

			Stop target = this.stops[at];
			string name = char.ToUpper(target.Name[0], System.Globalization.CultureInfo.CurrentCulture) + target.Name[1..];
			list.GoTo(name, () =>
			{
				// The list may have changed since the menu opened: walk to the same stop, not the same number.
				int steps = this.stops.IndexOf(target) - this.stopAt;
				for (int n = Math.Abs(steps); n > 0; n--)
					this.Walk(Math.Sign(steps));
			});
		}

		return list;
	}

	partial void OnPageChanged(NeuronPageViewModel? value)
	{
		this.NoteStop();
		if (this.IsExtra)
			this.OnPropertyChanged(nameof(this.WindowTitle));
	}

	private void StartHistory()
	{
		this.Chat.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(ChatViewModel.SelectedPerson) && this.IsChat)
				this.NoteStop();
		};
		this.NoteStop();
	}
}
