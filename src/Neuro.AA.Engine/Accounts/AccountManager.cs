namespace Neuro.AA.Engine.Accounts;

/// <summary>
/// Owns every account session. Profiles go to <see cref="IAccountStore"/>, secrets to
/// <see cref="ICredentialVault"/>.
/// </summary>
public sealed class AccountManager(IAccountStore store, ICredentialVault vault) : IAsyncDisposable
{
	private readonly object sync = new();
	private readonly List<AccountSession> sessions = [];

	/// <summary>Current sessions, in the order the operator arranged them.</summary>
	public IReadOnlyList<AccountSession> Sessions
	{
		get
		{
			lock (this.sync)
				return [.. this.sessions];
		}
	}

	/// <summary>Raised when sessions are added, removed or reordered.</summary>
	public event EventHandler? SessionsChanged;

	/// <summary>Loads saved profiles and creates their sessions (does not connect).</summary>
	public async Task LoadAsync(CancellationToken cancel = default)
	{
		IReadOnlyList<AccountProfile> profiles = await store.LoadAsync(cancel);

		lock (this.sync)
		{
			foreach (AccountProfile p in profiles)
			{
				if (!this.sessions.Exists(s => s.Profile.Id == p.Id))
					this.sessions.Add(new AccountSession(p, vault));
			}
		}

		this.SessionsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Connects every account marked to connect on startup.</summary>
	public Task ConnectStartupAccountsAsync(CancellationToken cancel = default)
		=> Task.WhenAll(this.Sessions.Where(s => s.Profile.ConnectOnStartup).Select(s => s.ConnectAsync(cancel)));

	/// <summary>Adds an account, stores its secret, and optionally connects it.</summary>
	public async Task<AccountSession> AddAsync(AccountProfile profile, AccountSecret secret, bool connect, CancellationToken cancel = default)
	{
		AccountSession session;

		lock (this.sync)
		{
			if (this.sessions.Exists(s => s.Profile.Id == profile.Id))
				throw new InvalidOperationException("An account with this id already exists.");

			profile = profile with { ColorIndex = this.sessions.Count == 0 ? 0 : this.sessions.Max(s => s.Profile.ColorIndex) + 1 };
			session = new AccountSession(profile, vault);
			this.sessions.Add(session);
		}

		await vault.SetAsync(profile.Id, secret, cancel);
		await this.PersistAsync(cancel);
		this.SessionsChanged?.Invoke(this, EventArgs.Empty);

		if (connect)
			await session.ConnectAsync(cancel);

		return session;
	}

	/// <summary>
	/// Creates a new account on a Neuron with an API key and secret (issued by the Neuron's
	/// operators), then keeps it like any other account. Nothing is kept when the Neuron refuses:
	/// the error says why (for example that the name is taken or the key is not valid).
	/// </summary>
	public async Task<AccountSession> CreateAsync(AccountProfile profile, string password, AccountRegistration registration, CancellationToken cancel = default)
	{
		AccountSession session = await this.AddAsync(profile, AccountSecret.FromPassword(password), connect: false, cancel);
		session.PendingRegistration = registration;

		TaskCompletionSource<ConnectionStatus> settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnStatus(object? sender, ConnectionStatus status)
		{
			if (status is ConnectionStatus.Connected or ConnectionStatus.Failed)
				settled.TrySetResult(status);
		}

		session.StatusChanged += OnStatus;
		try
		{
			await session.ConnectAsync(cancel);
			ConnectionStatus result = session.Status is ConnectionStatus.Connected or ConnectionStatus.Failed
				? session.Status
				: await settled.Task.WaitAsync(TimeSpan.FromSeconds(45), cancel);

			if (result == ConnectionStatus.Connected)
				return session;

			string why = session.LoginProblem == LoginProblem.WrongCredentials
				? "That user name is already taken on this Neuron."
				: session.LastError ?? "The Neuron did not create the account.";
			await this.RemoveAsync(profile.Id, cancel);
			throw new InvalidOperationException(why);
		}
		catch (TimeoutException)
		{
			await this.RemoveAsync(profile.Id, cancel);
			throw new InvalidOperationException("The Neuron did not answer in time.");
		}
		finally
		{
			session.StatusChanged -= OnStatus;
		}
	}

	/// <summary>Replaces a profile. Reconnects if connection settings changed and it was online.</summary>
	public async Task UpdateAsync(AccountProfile profile, CancellationToken cancel = default)
	{
		AccountSession session = this.Get(profile.Id) ?? throw new KeyNotFoundException("Unknown account.");
		AccountProfile old = session.Profile;
		session.UpdateProfile(profile);
		await this.PersistAsync(cancel);
		this.SessionsChanged?.Invoke(this, EventArgs.Empty);

		bool connectionChanged = old.Host != profile.Host || old.Port != profile.Port || old.Domain != profile.Domain
			|| old.UserName != profile.UserName || old.TrustServerCertificate != profile.TrustServerCertificate
			|| old.AllowInsecureMechanisms != profile.AllowInsecureMechanisms;

		if (connectionChanged && session.Status != ConnectionStatus.Offline)
		{
			await session.DisconnectAsync();
			await session.ConnectAsync(cancel);
		}
	}

	/// <summary>Replaces the stored secret, for example after a password change.</summary>
	public Task SetSecretAsync(Guid accountId, AccountSecret secret, CancellationToken cancel = default)
		=> vault.SetAsync(accountId, secret, cancel);

	/// <summary>
	/// Changes the account's password on its Neuron, and keeps the new one for the next sign-in.
	/// The account must be connected: the Neuron is the one that changes it.
	/// </summary>
	public async Task ChangePasswordAsync(Guid accountId, string newPassword, CancellationToken cancel = default)
	{
		AccountSession session = this.Get(accountId) ?? throw new KeyNotFoundException("Unknown account.");
		if (session.Client is not Waher.Networking.XMPP.XmppClient client || session.Status != ConnectionStatus.Connected)
			throw new InvalidOperationException("Connect the account first. The password is changed on the Neuron.");

		TaskCompletionSource<string?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		await client.ChangePassword(newPassword, (_, e) =>
		{
			done.TrySetResult(e.Ok ? null : string.IsNullOrWhiteSpace(e.ErrorText) ? "The Neuron did not change the password." : e.ErrorText);
			return Task.CompletedTask;
		}, null);

		if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20), cancel)) != done.Task)
			throw new TimeoutException("The Neuron did not answer in time. The password may or may not have changed.");

		if (await done.Task is string error)
			throw new InvalidOperationException(error);

		await vault.SetAsync(accountId, AccountSecret.FromPassword(newPassword), cancel);
	}

	/// <summary>Disconnects and removes an account and its secret.</summary>
	public async Task RemoveAsync(Guid accountId, CancellationToken cancel = default)
	{
		AccountSession? session;

		lock (this.sync)
		{
			session = this.sessions.Find(s => s.Profile.Id == accountId);
			if (session is null)
				return;
			this.sessions.Remove(session);
		}

		await session.DisposeAsync();
		await vault.RemoveAsync(accountId, cancel);
		await this.PersistAsync(cancel);
		this.SessionsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Finds a session by account id.</summary>
	public AccountSession? Get(Guid accountId)
	{
		lock (this.sync)
			return this.sessions.Find(s => s.Profile.Id == accountId);
	}

	private Task PersistAsync(CancellationToken cancel)
		=> store.SaveAsync(this.Sessions.Select(s => s.Profile).ToList(), cancel);

	public async ValueTask DisposeAsync()
	{
		foreach (AccountSession s in this.Sessions)
			await s.DisposeAsync();

		lock (this.sync)
			this.sessions.Clear();
	}
}
