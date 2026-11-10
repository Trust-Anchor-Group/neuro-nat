using System.Collections.Concurrent;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Chat;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Contracts;
using Waher.Networking.XMPP.Contracts.EventArguments;
using Waher.Networking.XMPP.ServiceDiscovery;

namespace Neuro.AA.Engine.Identity;

/// <summary>Someone asks to see one of the account's identities, or to have it peer reviewed.</summary>
public sealed record IdentityPetition(Guid AccountId, string PetitionId, string Kind, string From, string? RequestorName, string Purpose, string IdentityId, DateTime Received)
{
	internal byte[]? ContentToSign { get; init; }
	internal LegalIdentity? RequestorIdentity { get; init; }
}

/// <summary>An identity application waiting for a Neuron's operators.</summary>
public sealed record PendingApplication(string Id, string Account, DateTime? Created, string? Name, string? Organization, string? Country);

/// <summary>A Neuron's own legal identity, as its chat admin reports it.</summary>
public sealed record NeuronIdentity(Guid AccountId, string NeuronJid, string? Id, DateTime? Expires, LegalIdentity? Identity, string? Error);

/// <summary>
/// Legal identities through each account's server: the account's own identities, applying for
/// new ones, retiring them, answering petitions, and the Neurons' own identities. Signing keys
/// live in the runtime's encrypted database (see <see cref="EngineRuntime.InitializeAsync"/>), one
/// set per account, so identities stay usable between runs.
/// </summary>
public sealed class IdentityService : IAsyncDisposable
{
	private readonly AccountManager accounts;
	private readonly ChatService chat;
	private readonly ConcurrentDictionary<Guid, ContractsClient> clients = new();
	private readonly ConcurrentDictionary<Guid, string> problems = new();
	private readonly HashSet<AccountSession> watched = [];
	private readonly List<IdentityPetition> petitions = [];

	public IdentityService(AccountManager accounts, ChatService chat)
	{
		this.accounts = accounts;
		this.chat = chat;
		accounts.SessionsChanged += (_, _) => this.Sync();
		this.Sync();
	}

	/// <summary>Raised when an account's identities change, or its identity service becomes available. Any thread.</summary>
	public event EventHandler<Guid>? Changed;

	/// <summary>Raised when petitions arrive or are answered. Any thread.</summary>
	public event EventHandler? PetitionsChanged;

	/// <summary>True when the account's server has a legal identity service and keys are loaded.</summary>
	public bool IsAvailable(Guid accountId) => this.clients.ContainsKey(accountId);

	/// <summary>Why identities are not available for an account, when they are not.</summary>
	public string? Problem(Guid accountId) => this.problems.TryGetValue(accountId, out string? p) ? p : null;

	public IReadOnlyList<IdentityPetition> Petitions
	{
		get
		{
			lock (this.petitions)
				return [.. this.petitions];
		}
	}

	private ContractsClient Client(Guid accountId)
		=> this.clients.TryGetValue(accountId, out ContractsClient? c) ? c : throw new InvalidOperationException(this.Problem(accountId) ?? "The account is not connected.");

	// ---- The account's own identities ------------------------------------------------------

	public async Task<IReadOnlyList<LegalIdentity>> GetIdentitiesAsync(Guid accountId)
		=> (await this.Client(accountId).GetLegalIdentitiesAsync().WaitAsync(TimeSpan.FromSeconds(30))) ?? [];

	/// <summary>Checks an identity's signatures and attachments.</summary>
	public async Task<IdentityStatus> ValidateAsync(Guid accountId, LegalIdentity identity)
		=> (await this.Client(accountId).ValidateAsync(identity).WaitAsync(TimeSpan.FromSeconds(30))).Status;

	/// <summary>Applies for a new legal identity. The Neuron's operators approve or reject it.</summary>
	public async Task<LegalIdentity> ApplyAsync(Guid accountId, IReadOnlyList<Property> properties)
	{
		LegalIdentity id = await this.Client(accountId).ApplyAsync([.. properties]).WaitAsync(TimeSpan.FromSeconds(60));
		this.Changed?.Invoke(this, accountId);
		return id;
	}

	public async Task<LegalIdentity> ObsoleteAsync(Guid accountId, string identityId)
	{
		LegalIdentity id = await this.Client(accountId).ObsoleteLegalIdentityAsync(identityId).WaitAsync(TimeSpan.FromSeconds(30));
		this.Changed?.Invoke(this, accountId);
		return id;
	}

	public async Task<LegalIdentity> CompromisedAsync(Guid accountId, string identityId)
	{
		LegalIdentity id = await this.Client(accountId).CompromisedLegalIdentityAsync(identityId).WaitAsync(TimeSpan.FromSeconds(30));
		this.Changed?.Invoke(this, accountId);
		return id;
	}

	/// <summary>Answers a petition to see one of the account's identities.</summary>
	public async Task RespondAsync(IdentityPetition p, bool accept)
	{
		ContractsClient c = this.Client(p.AccountId);
		switch (p.Kind)
		{
			case "identity":
				await c.PetitionIdentityResponseAsync(p.IdentityId, p.PetitionId, p.From, accept);
				break;

			case "signature":
				if (accept && p.ContentToSign is byte[] content)
				{
					byte[] signature = await c.SignAsync(content, SignWith.LatestApprovedId);
					await c.PetitionSignatureResponseAsync(p.IdentityId, content, signature, p.PetitionId, p.From, true);
				}
				else
					await c.PetitionSignatureResponseAsync(p.IdentityId, p.ContentToSign ?? [], [], p.PetitionId, p.From, false);
				break;
		}

		lock (this.petitions)
			this.petitions.Remove(p);
		this.PetitionsChanged?.Invoke(this, EventArgs.Empty);
	}

	// ---- The Neurons' own identities -------------------------------------------------------

	/// <summary>A Neuron's own legal identity and its expiry, through its chat admin.</summary>
	public async Task<NeuronIdentity> NeuronIdentityAsync(Guid accountId, string neuronJid)
	{
		string? answer = await this.chat.Console(accountId, neuronJid).AskAsync("LegalId", TimeSpan.FromSeconds(20));
		if (answer is null)
			return new NeuronIdentity(accountId, neuronJid, null, null, null, "The Neuron did not answer.");

		(string? id, DateTime? expires) = ParseLegalIdAnswer(answer);

		LegalIdentity? identity = null;
		string? error = null;
		if (id is not null && this.clients.TryGetValue(accountId, out ContractsClient? c))
		{
			try
			{
				identity = await c.GetLegalIdentityAsync(id).WaitAsync(TimeSpan.FromSeconds(20));
				expires ??= identity.To;
			}
			catch (Exception ex)
			{
				error = ex.Message;
			}
		}

		return new NeuronIdentity(accountId, neuronJid, id, expires, identity, error);
	}

	/// <summary>
	/// Reads the chat admin's answer to <c>LegalId</c>: a Markdown table with ID and Expires, or
	/// tab-separated lines when asked without Markdown. N/A when the Neuron has no identity.
	/// </summary>
	internal static (string? Id, DateTime? Expires) ParseLegalIdAnswer(string answer)
	{
		IReadOnlyList<string[]> rows = MarkdownTable.Rows(answer);
		string[] data = rows.Count > 1 ? rows[1] : [];
		string? id = data.Length > 0 && data[0].Length > 0 && data[0] != "N/A" ? data[0] : null;
		DateTime? expires = data.Length > 1 ? MarkdownTable.Date(data[1]) : null;
		return (id, expires);
	}

	/// <summary>
	/// Identity applications waiting for the operators of a Neuron, through its chat admin.
	/// Needs the account to be an administrator there. Null when the Neuron did not answer.
	/// </summary>
	public async Task<IReadOnlyList<PendingApplication>?> PendingApplicationsAsync(Guid accountId, string neuronJid)
	{
		string? answer = await this.chat.Console(accountId, neuronJid).AskAsync(
			"select top 100 Id, Account, Created, this.FIRST First, this.LAST Last, this.ORGNAME Organization, this.COUNTRY Country " +
			"from Waher.Service.IoTBroker.Legal.Identity.LegalIdentity " +
			"where State=Waher.Security.IdentityState.Created order by Created desc",
			TimeSpan.FromSeconds(30));

		return answer is null ? null : ParsePendingApplications(answer);
	}

	internal static IReadOnlyList<PendingApplication> ParsePendingApplications(string answer)
	{
		List<PendingApplication> list = [];
		foreach (IReadOnlyDictionary<string, string> r in MarkdownTable.Records(answer))
		{
			string id = r.GetValueOrDefault("Id") ?? string.Empty;
			if (id.Length == 0)
				continue;

			string name = string.Join(" ", new[] { r.GetValueOrDefault("First"), r.GetValueOrDefault("Last") }.Where(x => !string.IsNullOrWhiteSpace(x)));
			list.Add(new PendingApplication(id, r.GetValueOrDefault("Account") ?? string.Empty, MarkdownTable.Date(r.GetValueOrDefault("Created")),
				name.Length > 0 ? name : null, NullIfEmpty(r.GetValueOrDefault("Organization")), NullIfEmpty(r.GetValueOrDefault("Country"))));
		}

		return list;
	}

	private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

	// ---- Connections -----------------------------------------------------------------------

	private void Sync()
	{
		foreach (AccountSession s in this.accounts.Sessions)
		{
			bool added;
			lock (this.watched)
				added = this.watched.Add(s);
			if (!added)
				continue;

			s.StatusChanged += (_, status) =>
			{
				if (status == ConnectionStatus.Connected)
					_ = this.StartAsync(s);
				else if (this.clients.TryRemove(s.Profile.Id, out ContractsClient? old))
				{
					old.Dispose();
					this.Changed?.Invoke(this, s.Profile.Id);
				}
			};

			if (s.Status == ConnectionStatus.Connected)
				_ = this.StartAsync(s);
		}
	}

	private async Task StartAsync(AccountSession s)
	{
		XmppClient? client = s.Client;
		if (client is null)
			return;

		Guid id = s.Profile.Id;
		try
		{
			string? component = await FindLegalComponentAsync(client);
			if (component is null)
			{
				this.problems[id] = "The account's server has no legal identity service.";
				this.Changed?.Invoke(this, id);
				return;
			}

			ContractsClient c = new(client, component);

			// Keys are the account's signature. Each account keeps its own set in the database,
			// so several accounts on one Neuron, or on several, never share or overwrite keys.
			c.SetKeySettingsInstance("NeuroAA." + id.ToString("N"), true);
			await c.LoadKeys(true);

			c.IdentityUpdated += (_, _) => { this.Changed?.Invoke(this, id); return Task.CompletedTask; };
			c.PetitionForIdentityReceived += (_, e) => this.AddPetition(id, "identity", e, e.RequestedIdentityId, null);
			c.PetitionForSignatureReceived += (_, e) => this.AddPetition(id, "signature", e, e.SignatoryIdentityId, e.ContentToSign);
			c.PetitionForPeerReviewIDReceived += (_, e) => this.AddPetition(id, "signature", e, e.SignatoryIdentityId, e.ContentToSign);

			if (this.clients.TryRemove(id, out ContractsClient? old))
				old.Dispose();

			this.clients[id] = c;
			this.problems.TryRemove(id, out _);
		}
		catch (Exception ex)
		{
			this.problems[id] = "Legal identities are not available: " + ex.Message;
		}

		this.Changed?.Invoke(this, id);
	}

	private Task AddPetition(Guid accountId, string kind, PetitionEventArgs e, string identityId, byte[]? content)
	{
		string? name = e.RequestorIdentity?.Properties?
			.Where(p => p.Name is "FIRST" or "LAST" or "ORGNAME")
			.Select(p => p.Value)
			.DefaultIfEmpty()
			.Aggregate((a, b) => a + " " + b);

		lock (this.petitions)
		{
			this.petitions.Add(new IdentityPetition(accountId, e.PetitionId, kind, e.RequestorBareJid, string.IsNullOrWhiteSpace(name) ? null : name,
				e.Purpose, identityId, DateTime.Now)
			{
				ContentToSign = content,
				RequestorIdentity = e.RequestorIdentity,
			});
		}

		this.PetitionsChanged?.Invoke(this, EventArgs.Empty);
		return Task.CompletedTask;
	}

	private static async Task<string?> FindLegalComponentAsync(XmppClient client)
	{
		ServiceItemsDiscoveryEventArgs items = await client.ServiceItemsDiscoveryAsync(client.Domain).WaitAsync(TimeSpan.FromSeconds(20));
		foreach (Item item in items.Items ?? [])
		{
			ServiceDiscoveryEventArgs info = await client.ServiceDiscoveryAsync(item.JID).WaitAsync(TimeSpan.FromSeconds(10));
			if (info.Ok && ContractsClient.NamespacesLegalIdentities.Any(info.HasFeature))
				return item.JID;
		}

		return null;
	}

	public ValueTask DisposeAsync()
	{
		foreach (ContractsClient c in this.clients.Values)
			c.Dispose();
		this.clients.Clear();
		return ValueTask.CompletedTask;
	}
}
