using CommunityToolkit.Mvvm.Input;

namespace Neuro.AA.App.ViewModels;

/// <summary>
/// More than one window. Any screen can be opened in a window of its own: the event stream on
/// a second monitor, one Neuron beside another, a chat on its own. Each window is a whole
/// shell over the same accounts, so everything that works in the main window works there.
/// The windows share Settings, and each remembers what it showed.
/// </summary>
public sealed partial class MainWindowViewModel
{
	private readonly MainWindowViewModel? primary;
	private string? wantedAddress;
	private bool joined;
	private bool goingTo;

	/// <summary>True for a window opened beside the main one.</summary>
	public bool IsExtra => this.primary is not null;

	/// <summary>
	/// What the window shows, as a line of text that <see cref="GoTo"/> understands: the screen,
	/// the account and the address it is about, and the Neuron the screens are filtered to.
	/// </summary>
	public string Address
	{
		get
		{
			Stop here = this.Here();
			return string.Join('|', here.Screen, here.Account?.ToString("N") ?? string.Empty, here.Jid ?? string.Empty, this.LensJid ?? string.Empty);
		}
	}

	/// <summary>
	/// Shows what an <see cref="Address"/> names. When it is about a Neuron or an account that
	/// is not there yet, as at start before anything has connected, it is shown as soon as it is.
	/// Returns false while it waits.
	/// </summary>
	public bool GoTo(string? address)
	{
		string[] parts = (address ?? string.Empty).Split('|');
		if (parts.Length == 0 || !Enum.TryParse(parts[0], out WorkspaceKind screen))
			return false;

		Guid? account = parts.Length > 1 && Guid.TryParse(parts[1], out Guid id) ? id : null;
		string? jid = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
		string? lens = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;

		// The main window keeps the Neuron it is filtered to in Settings; the others keep theirs here.
		if (lens is not null && this.IsExtra && !string.Equals(this.LensJid, lens, StringComparison.OrdinalIgnoreCase))
		{
			this.wantedLens = lens;
			this.SyncNeurons();
		}

		// A conversation is found by who it is with, should the account it was in have been added anew.
		if (PlaceOf(screen) == PlaceKind.Chat && jid is not null && !this.Accounts.Any(a => a.Session.Profile.Id == account))
		{
			this.Chat.Refresh();
			account = this.Chat.Everyone.FirstOrDefault(p => string.Equals(p.BareJid, jid, StringComparison.OrdinalIgnoreCase))?.AccountId ?? account;
		}

		bool there;
		this.goingTo = true;
		try
		{
			there = this.Restore(new Stop(screen, account, jid, string.Empty));
			if (there && PlaceOf(screen) == PlaceKind.Chat && account is Guid chatAccount && jid is not null)
				there = this.Chat.SelectedPerson is PersonViewModel p && p.AccountId == chatAccount && string.Equals(p.BareJid, jid, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			this.goingTo = false;
		}

		this.wantedAddress = there ? null : address;
		return there;
	}

	/// <summary>Tries again to show what <see cref="GoTo"/> could not yet. Called as Neurons and accounts appear.</summary>
	private void TryWanted()
	{
		if (this.wantedAddress is not string wanted)
			return;

		// The main window does not wait for long: being moved to another screen a minute into working is worse than starting on the list.
		if (this.wantedUntil is DateTime until && DateTime.UtcNow > until)
		{
			this.wantedAddress = null;
			this.wantedUntil = null;
			return;
		}

		this.GoTo(wanted);
	}

	private DateTime? wantedUntil;

	/// <summary>
	/// The app opens where you left it. The first time, and when that was the empty Accounts
	/// screen, it opens on the Neurons list. Called once the accounts are read.
	/// </summary>
	private void OpenWhereLeft()
	{
		if (this.IsExtra || !this.Settings.HasStore || this.Accounts.Count == 0 || this.Workspace != WorkspaceKind.Accounts)
			return;

		string? left = this.Settings.MainWindow?.Showing;
		if (left is null || this.GoTo(left))
		{
			if (left is null)
				this.ShowFleet();
			return;
		}

		// What it showed is not there yet, as nothing has connected: the list in the meantime, and the screen when it can be shown.
		this.goingTo = true;
		try
		{
			this.ShowFleet();
		}
		finally
		{
			this.goingTo = false;
		}

		this.wantedAddress = left;
		this.wantedUntil = DateTime.UtcNow.AddSeconds(30);
	}

	/// <summary>
	/// Starts a window opened beside the main one. The main window has loaded the accounts
	/// already: this one only follows them.
	/// </summary>
	public void Join()
	{
		if (this.joined)
			return;

		this.joined = true;
		if (Avalonia.Application.Current is Avalonia.Application app)
			app.ActualThemeVariantChanged += (_, _) => this.ThemeChanged();

		this.accounts.SessionsChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
		{
			this.Sync();
			this.TryWanted();
		});
		this.Sync();
		this.Fleet.Refresh();
		this.Fleet.Sort = this.Settings.NeuronSort;
	}

	/// <summary>Ctrl+Shift+N: what this window shows, in a window of its own.</summary>
	[RelayCommand]
	private void NewWindow() => Ui.NewWindow?.Invoke(this.Address);

	/// <summary>F11: the Neurons and their latest problems, large, for a big screen.</summary>
	[RelayCommand]
	private void OpenWall() => Ui.OpenWall?.Invoke();

	/// <summary>What the Wall view shows. One for the app: the main window's.</summary>
	public WallViewModel Wall => this.primary?.Wall ?? (this.wall ??= new WallViewModel(this.Fleet, this.fleet.Monitors.Hub));

	private WallViewModel? wall;

	/// <summary>A Neuron's page in a window of its own.</summary>
	public void OpenNeuronWindow(string neuronJid, Guid? accountId)
	{
		if (this.RowOf(neuronJid, accountId) is FleetRowViewModel row)
			Ui.NewWindow?.Invoke(string.Join('|', WorkspaceKind.Neuron, row.AccountId.ToString("N"), row.BareJid, string.Empty));
	}

	/// <summary>A conversation in a window of its own.</summary>
	public void OpenChatWindow(Guid accountId, string bareJid)
		=> Ui.NewWindow?.Invoke(string.Join('|', WorkspaceKind.Chat, accountId.ToString("N"), bareJid, string.Empty));

	/// <summary>Set by the windows: true when one of the windows opened beside the main one is in front.</summary>
	public Func<bool>? IsAnotherWindowInFront { get; set; }

	/// <summary>A window that is used again starts without the last one's way back.</summary>
	public void ForgetHistory()
	{
		this.stops.Clear();
		this.stopAt = -1;
		this.WriteStop();
	}

	/// <summary>Set by the windows: true when another window has the conversation open in front of you.</summary>
	public Func<Engine.Alerts.Alert, bool>? IsLookedAtElsewhere { get; set; }
}
