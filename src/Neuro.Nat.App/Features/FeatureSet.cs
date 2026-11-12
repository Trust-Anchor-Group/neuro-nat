using Avalonia.Controls.Templates;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Software;

namespace Neuro.Nat.App.Features;

/// <summary>
/// The features the desktop host registered at start, and what NAT asks of them. Without any,
/// every list is empty and NAT shows nothing of them. A feature that throws is left out of that
/// answer and logged; it never takes a screen down with it.
/// </summary>
public sealed partial class FeatureSet : IFeatureHost, IAsyncDisposable
{
	private readonly List<INatFeature> features;
	private readonly object sync = new();
	private readonly Dictionary<string, string> software = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<AccountSession> watched = [];
	private readonly Dictionary<Guid, string?> webAddresses = [];
	private AccountManager? accounts;
	private MonitorService? monitors;

	public FeatureSet(IEnumerable<INatFeature> features)
	{
		this.features = [.. features];
		foreach (INatFeature f in this.features)
			f.Changed += (_, n) => this.Changed?.Invoke(this, n);
	}

	/// <summary>No features: NAT as it is without them.</summary>
	public static FeatureSet None() => new([]);

	public IReadOnlyList<INatFeature> Features => this.features;

	public bool IsStarted => this.accounts is not null;

	public AccountManager Accounts => this.accounts ?? throw new InvalidOperationException("The features have not started.");

	public MonitorService Monitors => this.monitors ?? throw new InvalidOperationException("The features have not started.");

	public event EventHandler<Guid>? SignedIn;

	public event EventHandler<FeatureNeuron>? SoftwareChanged;

	/// <summary>Raised when what some feature adds may have changed: for one Neuron, or for all when null. Any thread.</summary>
	public event EventHandler<FeatureNeuron?>? Changed;

	/// <summary>Starts every feature. Called by the first window; later calls do nothing.</summary>
	public void Start(AccountManager accounts, MonitorService monitors)
	{
		lock (this.sync)
		{
			if (this.accounts is not null)
				return;

			this.accounts = accounts;
			this.monitors = monitors;
		}

		foreach (INatFeature f in this.features)
		{
			try
			{
				f.Start(this);
			}
			catch (Exception ex)
			{
				AppLog.Error($"The feature {f.Key} could not start.", ex);
			}
		}

		accounts.SessionsChanged += this.Accounts_SessionsChanged;
		this.WatchSessions();
	}

	private void Accounts_SessionsChanged(object? sender, EventArgs e) => this.WatchSessions();

	private void WatchSessions()
	{
		if (this.accounts is not AccountManager accounts)
			return;

		List<AccountSession> connected = [];
		lock (this.sync)
		{
			IReadOnlyList<AccountSession> now = accounts.Sessions;
			foreach (AccountSession s in now)
			{
				// A new web address: features ask that account's Neuron again, as at sign-in.
				string? web = s.Profile.WebAddress;
				bool known = this.webAddresses.TryGetValue(s.Profile.Id, out string? was);
				this.webAddresses[s.Profile.Id] = web;

				if (!this.watched.Add(s))
				{
					if (known && !string.Equals(was, web, StringComparison.OrdinalIgnoreCase) && s.Status == ConnectionStatus.Connected)
						connected.Add(s);
					continue;
				}

				s.StatusChanged += this.Session_StatusChanged;
				if (s.Status == ConnectionStatus.Connected)
					connected.Add(s);
			}

			foreach (AccountSession gone in this.watched.Where(s => !now.Contains(s)).ToList())
			{
				gone.StatusChanged -= this.Session_StatusChanged;
				this.watched.Remove(gone);
			}
		}

		foreach (AccountSession s in connected)
			this.SignedIn?.Invoke(this, s.Profile.Id);
	}

	private void Session_StatusChanged(object? sender, ConnectionStatus status)
	{
		if (status == ConnectionStatus.Connected && sender is AccountSession s)
			this.SignedIn?.Invoke(this, s.Profile.Id);
	}

	/// <summary>
	/// What NAT read of a Neuron's software. When it differs from what was read before (another
	/// build, other packages installed), the features hear that the software changed.
	/// </summary>
	public void NoteSoftware(NeuronSoftware read)
	{
		if (read.Error is not null)
			return;

		string key = read.AccountId.ToString("N") + "|" + read.NeuronJid;
		string print = (read.BuildTime ?? string.Empty) + "|" + string.Join(";", read.Installed.Order(StringComparer.OrdinalIgnoreCase)) + "|" +
			string.Join(";", read.Packages.Where(p => p.IsInstalled).Select(p => p.FileName + "@" + p.Installed).Order(StringComparer.OrdinalIgnoreCase));

		bool changed;
		lock (this.sync)
		{
			changed = this.software.TryGetValue(key, out string? was) && was != print;
			this.software[key] = print;
		}

		if (changed)
			this.SoftwareChanged?.Invoke(this, new FeatureNeuron(read.AccountId, read.NeuronJid));
	}

	// ----- What the features add -----

	public IReadOnlyList<string> SoftwareLines(Guid accountId, string neuronJid) => this.All(f => f.SoftwareLines(accountId, neuronJid));

	public IReadOnlyList<FeatureTab> NeuronTabs(Guid accountId, string neuronJid) => this.All(f => f.NeuronTabs(accountId, neuronJid));

	public IReadOnlyList<FeaturePlace> Places() => this.All(f => f.Places());

	public IReadOnlyList<FeatureInboxItem> InboxItems() => this.All(f => f.InboxItems());

	public IReadOnlyList<FeatureSearchHit> SearchHits() => this.All(f => f.SearchHits());

	public IReadOnlyList<FeatureScreenPage> BigScreenPages() => this.All(f => f.BigScreenPages());

	public IReadOnlyList<IDataTemplate> Templates() => this.All(f => f.Templates());

	/// <summary>Whether the feature with this key is on a Neuron. Unknown when no such feature is registered.</summary>
	public FeaturePresence PresenceOn(string key, Guid accountId, string neuronJid)
	{
		INatFeature? f = this.features.FirstOrDefault(x => x.Key == key);
		if (f is null)
			return FeaturePresence.Unknown;

		try
		{
			return f.PresenceOn(accountId, neuronJid);
		}
		catch (Exception ex)
		{
			AppLog.Error($"The feature {f.Key} failed.", ex);
			return FeaturePresence.Unknown;
		}
	}

	private List<T> All<T>(Func<INatFeature, IReadOnlyList<T>> ask)
	{
		List<T> list = [];
		foreach (INatFeature f in this.features)
		{
			try
			{
				list.AddRange(ask(f));
			}
			catch (Exception ex)
			{
				AppLog.Error($"The feature {f.Key} failed.", ex);
			}
		}

		return list;
	}

	public async ValueTask DisposeAsync()
	{
		if (this.accounts is AccountManager accounts)
			accounts.SessionsChanged -= this.Accounts_SessionsChanged;

		lock (this.sync)
		{
			foreach (AccountSession s in this.watched)
				s.StatusChanged -= this.Session_StatusChanged;
			this.watched.Clear();
		}

		foreach (INatFeature f in this.features)
		{
			try
			{
				await f.DisposeAsync();
			}
			catch (Exception ex)
			{
				AppLog.Error($"The feature {f.Key} did not stop cleanly.", ex);
			}
		}
	}
}
