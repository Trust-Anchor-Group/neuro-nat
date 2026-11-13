using Neuro.Nat.Engine.Sniffing;
using Waher.Networking.XMPP;

namespace Neuro.Nat.Engine.Accounts;

/// <summary>
/// One live XMPP account: owns the <see cref="XmppClient"/>, reports status, keeps a sniffer,
/// and reconnects with backoff when an established connection drops.
/// </summary>
public sealed class AccountSession : IAsyncDisposable
{
	private static readonly TimeSpan[] backoff =
	[
		TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
		TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
	];

	private readonly ICredentialVault vault;
	private readonly SemaphoreSlim lifecycle = new(1, 1);
	private XmppClient? client;
	private CancellationTokenSource? reconnectCancel;
	private XmppState lastXmppState = XmppState.Offline;
	private bool wantOnline;
	private bool everConnected;
	private int attempt;

	internal AccountSession(AccountProfile profile, ICredentialVault vault)
	{
		this.Profile = profile;
		this.vault = vault;
		this.Sniffer = new StreamSniffer(profile.Label);
	}

	/// <summary>The account this session runs.</summary>
	public AccountProfile Profile { get; private set; }

	/// <summary>Current status.</summary>
	public ConnectionStatus Status { get; private set; } = ConnectionStatus.Offline;

	/// <summary>Last connection error, cleared on a successful login.</summary>
	public string? LastError { get; private set; }

	/// <summary>
	/// Set while the account is being created on the Neuron with an API key: the next connection
	/// registers the account with a registration form signed by the key. Kept in memory only.
	/// </summary>
	internal AccountRegistration? PendingRegistration { get; set; }

	/// <summary>Why the last login was refused, if the server said so. Cleared on a successful login.</summary>
	public LoginProblem LoginProblem { get; private set; }

	/// <summary>When the current connection was established (UTC).</summary>
	public DateTime? ConnectedSinceUtc { get; private set; }

	/// <summary>Bare JID once connected.</summary>
	public string? BareJid => this.client?.BareJID;

	/// <summary>Everything sent and received on this account.</summary>
	public StreamSniffer Sniffer { get; }

	/// <summary>The underlying client, for services built on top of the session. Null while offline.</summary>
	public XmppClient? Client => this.client;

	/// <summary>Raised when <see cref="Status"/> changes. May be raised on any thread.</summary>
	public event EventHandler<ConnectionStatus>? StatusChanged;

	/// <summary>
	/// Raised when a new client has been created, before it connects. Services attach their
	/// handlers here so they see the first presence and roster stanzas.
	/// </summary>
	public event EventHandler<XmppClient>? ClientCreated;

	/// <summary>Raised when a connected client is ready for services to attach to it.</summary>
	public event EventHandler<XmppClient>? ClientReady;

	internal void UpdateProfile(AccountProfile profile) => this.Profile = profile;

	/// <summary>Connects, or does nothing if already online or connecting.</summary>
	public async Task ConnectAsync(CancellationToken cancel = default)
	{
		await this.lifecycle.WaitAsync(cancel);
		try
		{
			this.wantOnline = true;
			this.attempt = 0;
			this.CancelReconnect();

			if (this.client is not null && this.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting or ConnectionStatus.Authenticating)
				return;

			await this.StartClientAsync(cancel);
		}
		finally
		{
			this.lifecycle.Release();
		}
	}

	/// <summary>Goes offline and stops reconnecting.</summary>
	public async Task DisconnectAsync()
	{
		await this.lifecycle.WaitAsync();
		try
		{
			this.wantOnline = false;
			this.CancelReconnect();
			await this.StopClientAsync();
			this.SetStatus(ConnectionStatus.Offline);
		}
		finally
		{
			this.lifecycle.Release();
		}
	}

	private async Task StartClientAsync(CancellationToken cancel)
	{
		await this.StopClientAsync();

		AccountSecret? secret = await this.vault.GetAsync(this.Profile.Id, cancel);
		if (secret is null)
		{
			this.LoginProblem = LoginProblem.NoStoredPassword;
			this.LastError = "No password is stored for this account.";
			this.SetStatus(ConnectionStatus.Failed);
			return;
		}

		XmppCredentials credentials = new()
		{
			Host = this.Profile.Host,
			Port = this.Profile.Port,
			Account = this.Profile.UserName,
			Password = secret.Value,
			PasswordType = secret.HashMethod,
			TrustServer = this.Profile.TrustServerCertificate,
			AllowPlain = this.Profile.AllowInsecureMechanisms,
			AllowCramMD5 = this.Profile.AllowInsecureMechanisms,
			AllowDigestMD5 = this.Profile.AllowInsecureMechanisms,
		};

		AccountRegistration? registration = this.PendingRegistration;
		if (registration is not null)
		{
			credentials.AllowRegistration = true;
			credentials.FormSignatureKey = registration.ApiKey;
			credentials.FormSignatureSecret = registration.ApiSecret;
		}

		// The ordinary sign-in, not the library's quick login. Quick login skips binding a resource, and
		// the client then never learns its own full address: FullJID stays "user@domain". End-to-end
		// encryption mixes the sender's full address into every message, so the far end, which sees the
		// real one, could not decrypt anything this client sent.
		XmppClient c = new(credentials, "en", typeof(AccountSession).Assembly, this.Sniffer)
		{
			AllowQuickLogin = false,
		};

		if (registration is not null)
		{
			// The Neuron's form asks for user name and password (filled in by the client), and
			// optionally an e-mail address and phone number. The client signs it with the key.
			c.OnRegistrationForm += async (_, form) =>
			{
				if (!string.IsNullOrWhiteSpace(registration.EMail) && form["email"] is Waher.Networking.XMPP.DataForms.Field email)
					await email.SetValue(registration.EMail.Trim());
				if (!string.IsNullOrWhiteSpace(registration.Phone) && form["phone"] is Waher.Networking.XMPP.DataForms.Field phone)
					await phone.SetValue(registration.Phone.Trim());
				await form.Submit();
			};
		}

		c.OnStateChanged += this.Client_OnStateChanged;
		c.OnConnectionError += this.Client_OnConnectionError;
		c.OnError += this.Client_OnConnectionError;
		this.client = c;
		this.everConnected = false;
		this.LastError = null;
		this.LoginProblem = LoginProblem.None;

		this.ClientCreated?.Invoke(this, c);
		this.SetStatus(ConnectionStatus.Connecting);

		if (string.IsNullOrWhiteSpace(this.Profile.Domain))
			await c.Connect();
		else
			await c.Connect(this.Profile.Domain);
	}

	private async Task StopClientAsync()
	{
		XmppClient? c = this.client;
		if (c is null)
			return;

		this.client = null;
		this.ConnectedSinceUtc = null;
		c.OnStateChanged -= this.Client_OnStateChanged;
		c.OnConnectionError -= this.Client_OnConnectionError;
		c.OnError -= this.Client_OnConnectionError;

		try
		{
			await c.OfflineAndDisposeAsync(false);
		}
		catch (Exception ex)
		{
			this.Sniffer.Exception(ex);
		}
	}

	private async Task Client_OnStateChanged(object sender, XmppState newState)
	{
		if (!ReferenceEquals(sender, this.client))
			return;

		XmppState previous = this.lastXmppState;
		this.lastXmppState = newState;

		switch (newState)
		{
			case XmppState.Connected:
				this.PendingRegistration = null;   // Created: from now on it is an ordinary login.
				this.everConnected = true;
				this.attempt = 0;
				this.LastError = null;
				this.LoginProblem = LoginProblem.None;
				this.ConnectedSinceUtc = DateTime.UtcNow;
				await this.KeepHashInsteadOfPassword();
				this.SetStatus(ConnectionStatus.Connected);
				if (this.client is XmppClient c)
					this.ClientReady?.Invoke(this, c);
				break;

			case XmppState.Error:
			case XmppState.Offline:
				this.ConnectedSinceUtc = null;

				// A failure while logging in is almost always wrong credentials: retrying would
				// only lock the account in the server's login auditor.
				bool authFailure = !this.everConnected &&
					(previous == XmppState.Authenticating || this.LoginProblem != LoginProblem.None);

				if (this.wantOnline && !authFailure)
					this.ScheduleReconnect();
				else
				{
					if (authFailure && this.LoginProblem == LoginProblem.None)
						this.LoginProblem = LoginProblem.Other;
					if (authFailure)
						this.LastError ??= "Login failed. Check the user name and password.";
					this.SetStatus(newState == XmppState.Error ? ConnectionStatus.Failed : ConnectionStatus.Offline);
				}
				break;

			default:
				this.SetStatus(ConnectionStatusMapping.FromXmpp(newState));
				break;
		}
	}

	private Task Client_OnConnectionError(object sender, Exception ex)
	{
		if (ReferenceEquals(sender, this.client))
		{
			this.LastError = ex.Message;
			if (LoginProblems.Classify(ex) is LoginProblem problem)
				this.LoginProblem = problem;
		}

		return Task.CompletedTask;
	}

	private async Task KeepHashInsteadOfPassword()
	{
		XmppClient? c = this.client;
		if (c is null || string.IsNullOrEmpty(c.PasswordHash) || string.IsNullOrEmpty(c.PasswordHashMethod))
			return;

		AccountSecret? current = await this.vault.GetAsync(this.Profile.Id);
		if (current is not null && !current.IsHash)
			await this.vault.SetAsync(this.Profile.Id, AccountSecret.FromHash(c.PasswordHash, c.PasswordHashMethod));
	}

	private void ScheduleReconnect()
	{
		this.CancelReconnect();

		TimeSpan delay = backoff[Math.Min(this.attempt, backoff.Length - 1)];
		this.attempt++;
		this.SetStatus(ConnectionStatus.Reconnecting);

		CancellationTokenSource cts = new();
		this.reconnectCancel = cts;

		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(delay, cts.Token);
				await this.lifecycle.WaitAsync(cts.Token);
				try
				{
					if (this.wantOnline && !cts.IsCancellationRequested)
						await this.StartClientAsync(cts.Token);
				}
				finally
				{
					this.lifecycle.Release();
				}
			}
			catch (OperationCanceledException)
			{
				// Superseded by a manual connect or disconnect.
			}
			catch (Exception ex)
			{
				this.LastError = ex.Message;
				this.Sniffer.Exception(ex);
				if (this.wantOnline)
					this.ScheduleReconnect();
			}
		});
	}

	private void CancelReconnect()
	{
		CancellationTokenSource? cts = this.reconnectCancel;
		this.reconnectCancel = null;
		if (cts is not null)
		{
			cts.Cancel();
			cts.Dispose();
		}
	}

	private void SetStatus(ConnectionStatus status)
	{
		if (this.Status == status)
			return;

		this.Status = status;
		this.StatusChanged?.Invoke(this, status);
	}

	public async ValueTask DisposeAsync()
	{
		await this.DisconnectAsync();
		this.lifecycle.Dispose();
		await this.Sniffer.DisposeAsync();
	}
}
