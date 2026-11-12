using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Accounts;

namespace Neuro.Nat.App.ViewModels;

/// <summary>One account row: status, last error, and connect/disconnect actions.</summary>
public sealed partial class AccountItemViewModel : ObservableObject, IDisposable
{
	private readonly Func<AccountItemViewModel, Task> remove;
	private readonly Func<Guid, AccountSecret, Task> setSecret;
	private readonly Func<Guid, string, Task>? changePassword;

	public AccountItemViewModel(AccountSession session, Func<AccountItemViewModel, Task> remove, Func<Guid, AccountSecret, Task> setSecret, Func<Guid, string, Task>? changePassword = null)
	{
		this.Session = session;
		this.remove = remove;
		this.setSecret = setSecret;
		this.changePassword = changePassword;
		this.Sniffer = new SnifferViewModel(session.Sniffer, session.Profile.Label);
		session.StatusChanged += this.Session_StatusChanged;
		this.Refresh();
	}

	public AccountSession Session { get; }

	public SnifferViewModel Sniffer { get; }

	// ---- Raw XMPP console: send a stanza by hand, see the answer ---------------------------

	public static IReadOnlyList<string> RawKinds { get; } = ["IQ get", "IQ set", "Message"];

	[ObservableProperty] private string rawKind = "IQ get";
	[ObservableProperty] private string rawTo = string.Empty;
	[ObservableProperty] private string rawXml = "<query xmlns='http://jabber.org/protocol/disco#info'/>";
	[ObservableProperty] private string? rawResult;
	[ObservableProperty] private bool isRawOpen;

	[RelayCommand]
	private void ToggleRaw()
	{
		this.IsRawOpen = !this.IsRawOpen;
		if (string.IsNullOrEmpty(this.RawTo))
			this.RawTo = this.Session.Client?.Domain ?? this.Session.Profile.EffectiveDomain;
	}

	[RelayCommand]
	private async Task SendRaw()
	{
		Waher.Networking.XMPP.XmppClient? c = this.Session.Client;
		if (c is null || c.State != Waher.Networking.XMPP.XmppState.Connected)
		{
			this.RawResult = "The account is not connected.";
			return;
		}

		string to = this.RawTo.Trim();
		try
		{
			// The payload must be well-formed XML before it goes out.
			System.Xml.XmlDocument check = new();
			check.LoadXml("<root>" + this.RawXml + "</root>");

			switch (this.RawKind)
			{
				case "IQ get":
					this.RawResult = Pretty((await c.IqGetAsync(to, this.RawXml).WaitAsync(TimeSpan.FromSeconds(30)))?.OuterXml);
					break;

				case "IQ set":
					this.RawResult = Pretty((await c.IqSetAsync(to, this.RawXml).WaitAsync(TimeSpan.FromSeconds(30)))?.OuterXml);
					break;

				default:
					await c.SendMessage(Waher.Networking.XMPP.MessageType.Normal, to, this.RawXml, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
					this.RawResult = "Sent. Any answer shows in the traffic below.";
					break;
			}
		}
		catch (Exception ex)
		{
			this.RawResult = "Error: " + ex.Message;
		}
	}

	private static string Pretty(string? xml)
	{
		if (string.IsNullOrEmpty(xml))
			return "(empty result)";

		try
		{
			System.Xml.XmlDocument d = new();
			d.LoadXml(xml);
			using StringWriter sw = new();
			using (System.Xml.XmlWriter w = System.Xml.XmlWriter.Create(sw, new System.Xml.XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
				d.Save(w);
			return sw.ToString();
		}
		catch (System.Xml.XmlException)
		{
			return xml;
		}
	}

	public string Label => this.Session.Profile.Label;

	public string Endpoint => $"{this.Session.Profile.Host}:{this.Session.Profile.Port}";

	public int ColorIndex => this.Session.Profile.ColorIndex;

	[ObservableProperty]
	private ConnectionStatus status;

	[ObservableProperty]
	private string statusText = string.Empty;

	[ObservableProperty]
	private string? lastError;

	[ObservableProperty]
	private bool isOnline;

	[ObservableProperty]
	private bool isBusy;

	[ObservableProperty]
	private bool isFailed;

	/// <summary>True when Connect is the available action (otherwise Disconnect is).</summary>
	[ObservableProperty]
	private bool showConnect = true;

	/// <summary>One word for chips and compact layouts.</summary>
	[ObservableProperty]
	private string shortStatus = "Offline";

	/// <summary>Re-evaluates theme-dependent bindings (account hue) after a theme switch.</summary>
	public void RefreshTheme() => this.OnPropertyChanged(nameof(this.ColorIndex));

	private void Session_StatusChanged(object? sender, ConnectionStatus e) => Dispatcher.UIThread.Post(this.Refresh);

	private void Refresh()
	{
		ConnectionStatus s = this.Session.Status;
		this.Status = s;
		this.IsOnline = s == ConnectionStatus.Connected;
		this.IsBusy = s is ConnectionStatus.Connecting or ConnectionStatus.Authenticating or ConnectionStatus.Reconnecting;
		this.IsFailed = s == ConnectionStatus.Failed;
		this.LastError = this.Session.LastError;
		this.Sniffer.EmptyText = string.IsNullOrEmpty(this.LastError)
			? "Connect the account to see everything it sends and receives, live."
			: "Fix the problem above, then connect to see the traffic.";
		this.StatusText = s switch
		{
			ConnectionStatus.Connected => this.Session.ConnectedSinceUtc is DateTime t
				? $"Connected as {this.Session.BareJid} since {t.ToLocalTime():HH:mm}"
				: "Connected",
			ConnectionStatus.Connecting => "Connecting…",
			ConnectionStatus.Authenticating => "Logging in…",
			ConnectionStatus.Reconnecting => "Connection lost, retrying…",
			ConnectionStatus.Failed => "Failed · " + ShortCause(this.Session.LoginProblem, this.Session.LastError),
			_ => "Offline",
		};
		this.ShortStatus = s switch
		{
			ConnectionStatus.Connected => "Online",
			ConnectionStatus.Connecting or ConnectionStatus.Authenticating => "Connecting",
			ConnectionStatus.Reconnecting => "Retrying",
			ConnectionStatus.Failed => "Failed",
			_ => "Offline",
		};
		this.CanEnterPassword = this.IsFailed && this.Session.LoginProblem is LoginProblem.NoStoredPassword or LoginProblem.WrongCredentials
			or LoginProblem.CredentialsExpired or LoginProblem.Other;
		this.RecoveryHint = !this.IsFailed ? null : this.Session.LoginProblem switch
		{
			LoginProblem.TemporarilyBlocked => "The Neuron's login auditor has blocked this computer after repeated failed logins. Retrying only extends the block. Wait until the time above, or ask an administrator of the Neuron to unblock it.",
			LoginProblem.AccountDisabled => "The account is disabled on the Neuron. An administrator of the Neuron can enable it again.",
			_ => null,
		};
		this.ShowConnect = this.CanConnect();
		this.ConnectCommand.NotifyCanExecuteChanged();
		this.DisconnectCommand.NotifyCanExecuteChanged();
	}

	private bool CanConnect() => this.Status is ConnectionStatus.Offline or ConnectionStatus.Failed;

	[RelayCommand(CanExecute = nameof(CanConnect))]
	private Task Connect() => this.Session.ConnectAsync();

	private bool CanDisconnect() => !this.CanConnect();

	[RelayCommand(CanExecute = nameof(CanDisconnect))]
	private Task Disconnect() => this.Session.DisconnectAsync();

	/// <summary>Remove needs two clicks: the first arms it for a few seconds.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(RemoveText))]
	private bool isRemoveArmed;

	public string RemoveText => this.IsRemoveArmed ? $"Click again to remove {this.Label}" : "Remove account";

	[RelayCommand(AllowConcurrentExecutions = true)]
	private async Task Remove()
	{
		if (!this.IsRemoveArmed)
		{
			this.IsRemoveArmed = true;
			await Task.Delay(TimeSpan.FromSeconds(4));
			this.IsRemoveArmed = false;
			return;
		}

		this.IsRemoveArmed = false;
		await this.remove(this);
	}

	/// <summary>True when entering a (new) password can fix the failure.</summary>
	[ObservableProperty]
	private bool canEnterPassword;

	/// <summary>What the operator can do when a password will not help (blocked, disabled).</summary>
	[ObservableProperty]
	private string? recoveryHint;

	/// <summary>A few words on why the account is not connected, for list rows.</summary>
	private static string ShortCause(LoginProblem problem, string? error)
	{
		switch (problem)
		{
			case LoginProblem.NoStoredPassword: return "no stored password";
			case LoginProblem.WrongCredentials: return "wrong user name or password";
			case LoginProblem.TemporarilyBlocked: return "login temporarily blocked";
			case LoginProblem.AccountDisabled: return "account disabled";
			case LoginProblem.CredentialsExpired: return "password expired";
		}

		if (string.IsNullOrWhiteSpace(error))
			return "not connected";
		if (error.StartsWith("Login failed", StringComparison.OrdinalIgnoreCase))
			return "login refused";

		string first = error.Split([". ", "\r", "\n"], 2, StringSplitOptions.None)[0].TrimEnd('.');
		return first.Length <= 48 ? first : first[..47] + "…";
	}

	// Recovering a failed account without leaving the screen: enter a new password.

	[ObservableProperty]
	private bool isPasswordPromptOpen;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SavePasswordCommand))]
	private string newPassword = string.Empty;

	[RelayCommand]
	private void OpenPasswordPrompt() => this.IsPasswordPromptOpen = true;

	[RelayCommand]
	private void CancelPasswordPrompt()
	{
		this.IsPasswordPromptOpen = false;
		this.NewPassword = string.Empty;
	}

	private bool CanSavePassword() => !string.IsNullOrEmpty(this.NewPassword);

	// ---- Change password: on the Neuron and in the saved login, in one go -------------------

	/// <summary>"Signs in as admin on alpha.lab.local:5222. The password is kept encrypted for your user."</summary>
	public string SignInText => $"Signs in as {this.Session.Profile.UserName} on {this.Endpoint}. The password is kept encrypted for your user on this computer.";

	[ObservableProperty] private bool isChangingPassword;
	[ObservableProperty] private bool isPasswordBusy;
	[ObservableProperty] private string? passwordError;
	[ObservableProperty] private string? passwordNote;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(ChangePasswordCommand))]
	private string changedPassword = string.Empty;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(ChangePasswordCommand))]
	private string changedPasswordAgain = string.Empty;

	[RelayCommand]
	private void OpenChangePassword()
	{
		this.PasswordError = null;
		this.PasswordNote = null;
		this.ChangedPassword = string.Empty;
		this.ChangedPasswordAgain = string.Empty;
		this.IsChangingPassword = true;
	}

	[RelayCommand]
	private void CancelChangePassword()
	{
		this.IsChangingPassword = false;
		this.ChangedPassword = string.Empty;
		this.ChangedPasswordAgain = string.Empty;
		this.PasswordError = null;
	}

	private bool CanChangePassword() => this.ChangedPassword.Length > 0 && this.ChangedPasswordAgain.Length > 0 && !this.IsPasswordBusy;

	[RelayCommand(CanExecute = nameof(CanChangePassword))]
	private async Task ChangePassword()
	{
		this.PasswordError = null;
		if (this.ChangedPassword != this.ChangedPasswordAgain)
		{
			this.PasswordError = "The two passwords are not the same.";
			return;
		}

		if (this.changePassword is null || !this.IsOnline)
		{
			this.PasswordError = "Connect the account first. The password is changed on the Neuron.";
			return;
		}

		this.IsPasswordBusy = true;
		try
		{
			await this.changePassword(this.Session.Profile.Id, this.ChangedPassword);
			this.IsChangingPassword = false;
			this.ChangedPassword = string.Empty;
			this.ChangedPasswordAgain = string.Empty;
			this.PasswordNote = "Password changed, on the Neuron and in the saved login.";
			Ui.Say("Password changed");
		}
		catch (Exception ex)
		{
			this.PasswordError = ex.Message;
		}
		finally
		{
			this.IsPasswordBusy = false;
		}
	}

	[RelayCommand(CanExecute = nameof(CanSavePassword))]
	private async Task SavePassword()
	{
		await this.setSecret(this.Session.Profile.Id, AccountSecret.FromPassword(this.NewPassword));
		this.NewPassword = string.Empty;
		this.IsPasswordPromptOpen = false;
		await this.Session.ConnectAsync();
	}

	public void Dispose()
	{
		this.Session.StatusChanged -= this.Session_StatusChanged;
		this.Sniffer.Dispose();
	}
}
