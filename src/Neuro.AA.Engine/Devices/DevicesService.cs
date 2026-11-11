using System.Collections.Concurrent;
using Neuro.AA.Engine.Accounts;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Events;
using Waher.Networking.XMPP.Provisioning;
using Waher.Networking.XMPP.Provisioning.Events;
using Waher.Networking.XMPP.Provisioning.SearchOperators;
using Waher.Networking.XMPP.ServiceDiscovery;
using Waher.Things;

namespace Neuro.AA.Engine.Devices;

/// <summary>What a device's owner is asked.</summary>
public enum DeviceQuestionKind
{
	/// <summary>May someone become the device's friend (contact)?</summary>
	Friendship,

	/// <summary>May someone read the device's sensor data?</summary>
	Readout,

	/// <summary>May someone control the device?</summary>
	Control,
}

/// <summary>How widely an answer applies.</summary>
public enum AnswerScope
{
	/// <summary>Only the one who asked.</summary>
	Caller,

	/// <summary>Everyone on the asker's domain.</summary>
	Domain,

	/// <summary>Everyone (friendship only).</summary>
	Everyone,
}

/// <summary>A question to the owner of a device, from its Neuron's provisioning service.</summary>
public sealed record DeviceQuestion
{
	public required string Id { get; init; }
	public required Guid AccountId { get; init; }
	public required DeviceQuestionKind Kind { get; init; }

	/// <summary>The device.</summary>
	public required string DeviceJid { get; init; }

	/// <summary>Who wants access.</summary>
	public required string RemoteJid { get; init; }

	public string? NodeId { get; init; }
	public string? SourceId { get; init; }

	/// <summary>Fields (readout) or parameters (control) asked for; empty means all.</summary>
	public IReadOnlyList<string> Names { get; init; } = [];

	public DateTime Received { get; init; }

	internal object Args { get; init; } = null!;
}

/// <summary>A device in the thing registry.</summary>
public sealed record RegistryThing(string Jid, string? NodeId, string? SourceId, IReadOnlyList<KeyValuePair<string, string>> Tags)
{
	/// <summary>A friendly name from the tags (NAME, MODEL, MAN), or the address.</summary>
	public string Label
	{
		get
		{
			if (this.Tag("NAME") is { Length: > 0 } name)
				return name;
			string model = string.Join(" ", new[] { this.Tag("MAN"), this.Tag("MODEL") }.Where(s => !string.IsNullOrEmpty(s)));
			return model.Length > 0 ? model : this.Jid;
		}
	}

	public string? Tag(string name) => this.Tags.FirstOrDefault(t => string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>
/// Devices through each account's Neuron: the devices the account owns, searching the thing
/// registry, claiming a device with its key, giving one up, and answering the provisioning
/// service's questions on the owner's behalf (friendship, readout, control).
/// </summary>
public sealed class DevicesService : IAsyncDisposable
{
	private readonly AccountManager accounts;
	private readonly ConcurrentDictionary<Guid, (ThingRegistryClient Registry, ProvisioningClient Provisioning)> clients = new();
	private readonly ConcurrentDictionary<Guid, string> problems = new();
	private readonly HashSet<AccountSession> watched = [];
	private readonly List<DeviceQuestion> questions = [];

	public DevicesService(AccountManager accounts)
	{
		this.accounts = accounts;
		accounts.SessionsChanged += (_, _) => this.Sync();
		this.Sync();
	}

	/// <summary>Raised when the service becomes available for an account, or goes away. Any thread.</summary>
	public event EventHandler<Guid>? Changed;

	/// <summary>Raised when questions arrive or are answered. Any thread.</summary>
	public event EventHandler? QuestionsChanged;

	public bool IsAvailable(Guid accountId) => this.clients.ContainsKey(accountId);

	public string? Problem(Guid accountId) => this.problems.TryGetValue(accountId, out string? p) ? p : null;

	public IReadOnlyList<DeviceQuestion> Questions
	{
		get
		{
			lock (this.questions)
				return [.. this.questions];
		}
	}

	private (ThingRegistryClient Registry, ProvisioningClient Provisioning) Client(Guid accountId)
		=> this.clients.TryGetValue(accountId, out var c) ? c : throw new InvalidOperationException(this.Problem(accountId) ?? "The account is not connected.");

	// ---- Devices ---------------------------------------------------------------------------

	/// <summary>Devices the account owns.</summary>
	public async Task<IReadOnlyList<RegistryThing>> MyDevicesAsync(Guid accountId)
	{
		SearchResultEventArgs r = await this.Client(accountId).Provisioning.GetDevicesAsync(0, 500).WaitAsync(TimeSpan.FromSeconds(30));
		return r.Ok ? Things(r) : throw new InvalidOperationException(r.ErrorText ?? "The registry refused.");
	}

	/// <summary>Public devices whose tags contain the text (any tag, wildcards allowed).</summary>
	public async Task<IReadOnlyList<RegistryThing>> SearchAsync(Guid accountId, string text)
	{
		string value = string.IsNullOrWhiteSpace(text) ? "*" : (text.Contains('*') ? text.Trim() : "*" + text.Trim() + "*");
		SearchResultEventArgs r = await this.Client(accountId).Registry.SearchAsync(0, 200, new StringTagMask("*", "*", value, "*")).WaitAsync(TimeSpan.FromSeconds(30));
		return r.Ok ? Things(r) : throw new InvalidOperationException(r.ErrorText ?? "The registry refused the search.");
	}

	/// <summary>
	/// Claims a device: the tags must match what the device registered, including its secret
	/// KEY (usually printed on the device or shown in its QR code).
	/// </summary>
	public async Task<RegistryThing> ClaimAsync(Guid accountId, IReadOnlyList<KeyValuePair<string, string>> tags, bool makePublic)
	{
		TaskCompletionSource<NodeResultEventArgs> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		MetaDataTag[] meta = [.. tags.Select(t => (MetaDataTag)new MetaDataStringTag(t.Key, t.Value))];
		await this.Client(accountId).Registry.Mine(makePublic, meta, (_, e) => { done.TrySetResult(e); return Task.CompletedTask; }, null);
		NodeResultEventArgs e = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
		if (!e.Ok)
			throw new InvalidOperationException(e.ErrorText ?? "The registry did not accept the claim. Check the tags and the key.");

		string jid = e.From ?? string.Empty;
		if (e.Node is ThingReference n)
			return new RegistryThing(jid, n.NodeId, n.SourceId, [.. tags]);
		return new RegistryThing(jid, null, null, [.. tags]);
	}

	/// <summary>Gives up ownership of a device; it goes back to the registry, unclaimed.</summary>
	public async Task DisownAsync(Guid accountId, RegistryThing thing)
	{
		TaskCompletionSource<IqResultEventArgs> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		await this.Client(accountId).Registry.Disown(thing.Jid, thing.NodeId ?? string.Empty, thing.SourceId ?? string.Empty, string.Empty,
			(_, e) => { done.TrySetResult(e); return Task.CompletedTask; }, null);
		IqResultEventArgs r = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
		if (!r.Ok)
			throw new InvalidOperationException(r.ErrorText ?? "The registry refused.");
	}

	/// <summary>Makes the account's devices ask again: clears the rules they have cached.</summary>
	public Task ClearDeviceCachesAsync(Guid accountId) => this.Client(accountId).Provisioning.ClearDeviceCaches();

	private static List<RegistryThing> Things(SearchResultEventArgs r)
		=> [.. (r.Things ?? []).Select(t => new RegistryThing(t.Jid, t.Node?.NodeId is { Length: > 0 } id ? id : null, t.Node?.SourceId is { Length: > 0 } s ? s : null,
			[.. (t.Tags ?? []).Select(x => new KeyValuePair<string, string>(x.Name, x.StringValue))]))];

	// ---- Questions -------------------------------------------------------------------------

	/// <summary>Answers a question. Readout and control answers cover all fields or parameters.</summary>
	public async Task AnswerAsync(DeviceQuestion q, bool accept, AnswerScope scope)
	{
		TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task Done(object? _, IqResultEventArgs e)
		{
			if (e.Ok)
				done.TrySetResult(true);
			else
				done.TrySetException(new InvalidOperationException(e.ErrorText ?? "The provisioning service refused the answer."));
			return Task.CompletedTask;
		}

		switch (q.Args)
		{
			case IsFriendEventArgs f:
				await ((scope, accept) switch
				{
					(AnswerScope.Everyone, true) => f.AcceptForAll(Done, null),
					(AnswerScope.Everyone, false) => f.RejectForAll(Done, null),
					(AnswerScope.Domain, true) => f.AcceptForEntireDomain(Done, null),
					(AnswerScope.Domain, false) => f.RejectForEntireDomain(Done, null),
					(_, true) => f.Accept(Done, null),
					_ => f.Reject(Done, null),
				});
				break;

			case CanReadEventArgs r:
				await ((scope, accept) switch
				{
					(AnswerScope.Domain or AnswerScope.Everyone, true) => r.AcceptAllFromDomain(Done, null),
					(AnswerScope.Domain or AnswerScope.Everyone, false) => r.RejectAllFromDomain(Done, null),
					(_, true) => r.AcceptAllFromJID(Done, null),
					_ => r.RejectAllFromJID(Done, null),
				});
				break;

			case CanControlEventArgs c:
				await ((scope, accept) switch
				{
					(AnswerScope.Domain or AnswerScope.Everyone, true) => c.AcceptAllFromDomain(Done, null),
					(AnswerScope.Domain or AnswerScope.Everyone, false) => c.RejectAllFromDomain(Done, null),
					(_, true) => c.AcceptAllFromJID(Done, null),
					_ => c.RejectAllFromJID(Done, null),
				});
				break;
		}

		await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

		lock (this.questions)
			this.questions.RemoveAll(x => x.Id == q.Id);
		this.QuestionsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Adds a question as it is, for previews and UI tests.</summary>
	internal void Preview(DeviceQuestion q)
	{
		lock (this.questions)
			this.questions.Add(q);

		this.QuestionsChanged?.Invoke(this, EventArgs.Empty);
	}

	private Task Ask(Guid accountId, DeviceQuestionKind kind, QuestionEventArgs e, NodeQuestionEventArgs? node, IReadOnlyList<string> names)
	{
		DeviceQuestion q = new()
		{
			Id = accountId.ToString("N") + ":" + e.Key,
			AccountId = accountId,
			Kind = kind,
			DeviceJid = e.JID,
			RemoteJid = e.RemoteJID,
			NodeId = node?.NodeId is { Length: > 0 } n ? n : null,
			SourceId = node?.SourceId is { Length: > 0 } s ? s : null,
			Names = names,
			Received = DateTime.Now,
			Args = e,
		};

		lock (this.questions)
		{
			// The service asks again until someone answers; keep one of each.
			this.questions.RemoveAll(x => x.AccountId == accountId && x.Kind == kind && x.DeviceJid == q.DeviceJid && x.RemoteJid == q.RemoteJid && x.NodeId == q.NodeId);
			this.questions.Add(q);
		}

		this.QuestionsChanged?.Invoke(this, EventArgs.Empty);
		return Task.CompletedTask;
	}

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
				else if (this.clients.TryRemove(s.Profile.Id, out var old))
				{
					old.Registry.Dispose();
					old.Provisioning.Dispose();
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
			string? component = await FindRegistryAsync(client);
			if (component is null)
			{
				this.problems[id] = "The account's server has no thing registry.";
				this.Changed?.Invoke(this, id);
				return;
			}

			ThingRegistryClient registry = new(client, component);
			ProvisioningClient provisioning = new(client, component) { ManagePresenceSubscriptionRequests = false };
			provisioning.IsFriendQuestion += (_, e) => this.Ask(id, DeviceQuestionKind.Friendship, e, null, []);
			provisioning.CanReadQuestion += (_, e) => this.Ask(id, DeviceQuestionKind.Readout, e, e, e.Fields ?? []);
			provisioning.CanControlQuestion += (_, e) => this.Ask(id, DeviceQuestionKind.Control, e, e, e.Parameters ?? []);

			if (this.clients.TryRemove(id, out var old))
			{
				old.Registry.Dispose();
				old.Provisioning.Dispose();
			}

			this.clients[id] = (registry, provisioning);
			this.problems.TryRemove(id, out _);
		}
		catch (Exception ex)
		{
			this.problems[id] = "Devices are not available: " + ex.Message;
		}

		this.Changed?.Invoke(this, id);
	}

	private static async Task<string?> FindRegistryAsync(XmppClient client)
	{
		ServiceItemsDiscoveryEventArgs items = await client.ServiceItemsDiscoveryAsync(client.Domain).WaitAsync(TimeSpan.FromSeconds(20));
		foreach (Item item in items.Items ?? [])
		{
			ServiceDiscoveryEventArgs info = await client.ServiceDiscoveryAsync(item.JID).WaitAsync(TimeSpan.FromSeconds(10));
			if (info.Ok && (ThingRegistryClient.NamespacesDiscovery.Any(info.HasFeature) || ProvisioningClient.NamespacesProvisioningOwner.Any(info.HasFeature)))
				return item.JID;
		}

		return null;
	}

	public ValueTask DisposeAsync()
	{
		foreach (var c in this.clients.Values)
		{
			c.Registry.Dispose();
			c.Provisioning.Dispose();
		}
		this.clients.Clear();
		return ValueTask.CompletedTask;
	}
}
