using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Identity;
using Waher.Networking.XMPP.Contracts;
using Waher.Security;

namespace Neuro.Nat.App.ViewModels;

/// <summary>What the Identities list shows.</summary>
public enum IdentitiesMode
{
	/// <summary>Your accounts' own legal identities, and requests to see them.</summary>
	Mine,

	/// <summary>The Neurons' own identities and the applications waiting for their operators.</summary>
	Neurons,
}

/// <summary>What a row in the Identities list stands for.</summary>
public enum IdentityRowKind
{
	Account,
	Identity,
	Petition,
	Neuron,
}

/// <summary>One row in the Identities list.</summary>
public sealed partial class IdentityRowViewModel(string key, IdentityRowKind kind, Guid accountId) : ObservableObject
{
	public string Key { get; } = key;
	public IdentityRowKind Kind { get; } = kind;
	public Guid AccountId { get; } = accountId;

	public bool IsAccount => this.Kind == IdentityRowKind.Account;
	public bool IsItem => this.Kind != IdentityRowKind.Account;

	[ObservableProperty] private string title = string.Empty;
	[ObservableProperty] private string meta = string.Empty;
	[ObservableProperty] private string stateLabel = string.Empty;

	/// <summary>ok, busy, bad or idle: picks the State.* brush.</summary>
	[ObservableProperty] private string tone = "idle";

	[ObservableProperty] private bool hasState;
	[ObservableProperty] private string accountLabel = string.Empty;
	[ObservableProperty] private int accountColorIndex;
	[ObservableProperty] private string? badge;
	[ObservableProperty] private bool hasBadge;

	public LegalIdentity? Identity { get; set; }
	public IdentityPetition? Petition { get; set; }
	public string? NeuronJid { get; set; }
}

/// <summary>A labelled value in the details of an identity.</summary>
public sealed record IdentityFact(string Name, string Value, bool Mono = false);

/// <summary>An identity application waiting on a Neuron, with where to review it.</summary>
public sealed record PendingApplicationRow(PendingApplication Model, string WhenText, string ReviewUrl)
{
	public string Name => this.Model.Name ?? this.Model.Organization ?? "No name given";
	public string Details => string.Join(" · ", new[] { this.Model.Account, this.Model.Country, this.WhenText }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>A field of the application form.</summary>
public sealed partial class ApplyField(string name, string label, string hint, bool required = false) : ObservableObject
{
	public string Name { get; } = name;
	public string Label { get; } = required ? label + " *" : label;
	public string Hint { get; } = hint;
	public bool Required { get; } = required;

	[ObservableProperty] private string value = string.Empty;
}

/// <summary>
/// The Identities workspace: each account's legal identities on its Neuron (apply, check, retire,
/// answer requests to see them), and for the Neurons you operate, their own identity and the
/// applications waiting for review.
/// </summary>
public sealed partial class IdentitiesViewModel : ObservableObject, IDisposable
{
	private readonly IdentityService identities;
	private readonly MonitorService monitors;
	private readonly AccountManager accounts;
	private readonly DispatcherTimer timer;
	private readonly Dictionary<Guid, IReadOnlyList<LegalIdentity>> mine = [];
	private readonly Dictionary<Guid, string> loadErrors = [];
	private readonly Dictionary<(Guid, string), NeuronIdentity> neuronIds = [];
	private readonly Dictionary<(Guid, string), IReadOnlyList<PendingApplication>?> pending = [];
	private readonly HashSet<Guid> reload = [];
	private readonly Dictionary<Guid, int> loadVersions = [];
	private DateTime neuronsLoaded = DateTime.MinValue;
	private int dirty = 1;
	private bool loadingNeurons;

	public IdentitiesViewModel(IdentityService identities, MonitorService monitors, AccountManager accounts)
	{
		this.identities = identities;
		this.monitors = monitors;
		this.accounts = accounts;

		identities.Changed += this.Service_Changed;
		identities.PetitionsChanged += this.Service_PetitionsChanged;
		monitors.NeuronsChanged += this.Monitors_NeuronsChanged;

		this.timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => this.Tick());
		this.timer.Start();

		this.ApplyFields =
		[
			new("FIRST", "First name", "Kim", true),
			new("MIDDLE", "Middle names", string.Empty),
			new("LAST", "Last name", "Lindqvist", true),
			new("PNR", "Personal number", "As the Neuron's operators expect it"),
			new("ADDR", "Address", string.Empty),
			new("ZIP", "Postal code", string.Empty),
			new("CITY", "City", string.Empty),
			new("COUNTRY", "Country", "Two letters, like SE", true),
			new("ORGNAME", "Organization", "If you apply on behalf of one"),
			new("ORGNR", "Organization number", string.Empty),
			new("ORGROLE", "Role", "Your role in the organization"),
		];
	}

	public ObservableCollection<IdentityRowViewModel> Rows { get; } = [];
	public IReadOnlyList<ApplyField> ApplyFields { get; }
	public ObservableCollection<IdentityFact> Facts { get; } = [];
	public ObservableCollection<IdentityFact> Properties { get; } = [];
	public ObservableCollection<IdentityFact> Attachments { get; } = [];
	public ObservableCollection<PendingApplicationRow> PendingRows { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMine), nameof(IsNeurons), nameof(ShowsRetiredToggle))]
	private IdentitiesMode mode = IdentitiesMode.Mine;

	public bool IsMine => this.Mode == IdentitiesMode.Mine;
	public bool IsNeurons => this.Mode == IdentitiesMode.Neurons;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowsAccount), nameof(ShowsIdentity), nameof(ShowsPetition), nameof(ShowsNeuron), nameof(IsApplication), nameof(RetireLabel))]
	private IdentityRowViewModel? selected;

	public bool HasSelection => this.Selected is not null;
	public bool ShowsAccount => this.Selected?.Kind == IdentityRowKind.Account;
	public bool ShowsIdentity => this.Selected?.Kind == IdentityRowKind.Identity;
	public bool ShowsPetition => this.Selected?.Kind == IdentityRowKind.Petition;
	public bool ShowsNeuron => this.Selected?.Kind == IdentityRowKind.Neuron;

	[ObservableProperty] private bool hasRows;

	/// <summary>Shows retired, rejected and compromised identities too.</summary>
	[ObservableProperty] private bool showRetired;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasRetired), nameof(RetiredText), nameof(ShowsRetiredToggle))]
	private int retiredCount;

	public bool HasRetired => this.RetiredCount > 0;

	/// <summary>The "show retired" toggle only makes sense under Mine, when there are any.</summary>
	public bool ShowsRetiredToggle => this.IsMine && this.HasRetired;
	public string RetiredText => $"Show {this.RetiredCount} retired or rejected";

	partial void OnShowRetiredChanged(bool value) => this.Refresh();

	/// <summary>What the filter box over the list holds: part of a name, an account or a state.</summary>
	[ObservableProperty] private string filterText = string.Empty;

	partial void OnFilterTextChanged(string value) => this.Refresh();

	/// <summary>The list is empty because nothing matches the filter box.</summary>
	public bool IsEmptyFiltered => !this.HasRows && this.FilterText.Trim().Length > 0;

	/// <summary>The list is empty because there is nothing to list yet.</summary>
	public bool IsEmptyUnfiltered => !this.HasRows && this.FilterText.Trim().Length == 0;
	[ObservableProperty] private string summary = string.Empty;

	/// <summary>Requests to see an identity or have something signed, for the rail badge.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasPetitions))]
	private int petitionCount;

	public bool HasPetitions => this.PetitionCount > 0;

	// ---- The detail pane -------------------------------------------------------------------

	[ObservableProperty] private string detailTitle = string.Empty;
	[ObservableProperty] private string detailSubtitle = string.Empty;
	[ObservableProperty] private string detailState = string.Empty;
	[ObservableProperty] private string detailTone = "idle";
	[ObservableProperty] private string? detailNote;
	[ObservableProperty] private string? detailError;
	[ObservableProperty] private string? validation;
	[ObservableProperty] private string validationTone = "idle";
	[ObservableProperty] private bool isBusy;
	[ObservableProperty] private bool canApply;
	[ObservableProperty] private bool canRetire;
	[ObservableProperty] private bool canReportCompromised;
	[ObservableProperty] private bool hasProperties;
	[ObservableProperty] private bool hasAttachments;
	[ObservableProperty] private bool hasPending;
	[ObservableProperty] private string pendingSummary = string.Empty;
	[ObservableProperty] private string? neuronWebBase;

	/// <summary>"obsolete" or "compromised" while the operator confirms it.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsConfirming), nameof(ConfirmText), nameof(ConfirmButton))]
	private string? confirming;

	public bool IsConfirming => this.Confirming is not null;

	/// <summary>True when the selected identity is an application nobody has reviewed yet.</summary>
	public bool IsApplication => this.Selected?.Identity?.State == IdentityState.Created;

	public string RetireLabel => this.IsApplication ? "Withdraw" : "Retire";

	public string ConfirmText => this.Confirming == "compromised"
		? "Report this identity as compromised? Use it when its keys may be in someone else's hands. Contracts it signed are flagged, and it can never sign again."
		: this.IsApplication
			? "Withdraw this application? The operators will no longer see it, and it can never be approved."
			: "Retire this identity? It stays on record, but can never sign again. Apply for a new one to replace it.";

	public string ConfirmButton => this.Confirming == "compromised" ? "Report compromised" : this.IsApplication ? "Withdraw application" : "Retire identity";

	/// <summary>Set by the view: puts text on the clipboard.</summary>
	public Func<string, Task>? CopyToClipboard { get; set; }

	/// <summary>Set by the view: opens a link in the browser.</summary>
	public Func<string, Task>? OpenUrl { get; set; }

	partial void OnModeChanged(IdentitiesMode value)
	{
		this.Selected = null;
		if (value == IdentitiesMode.Neurons && DateTime.UtcNow - this.neuronsLoaded > TimeSpan.FromMinutes(2))
			_ = this.LoadNeuronsAsync();
		this.Refresh();
	}

	partial void OnSelectedChanged(IdentityRowViewModel? value)
	{
		this.Confirming = null;
		this.Validation = null;
		this.DetailError = null;
		this.ShowDetail();

		if (value?.Kind == IdentityRowKind.Neuron && value.NeuronJid is string jid && !this.pending.ContainsKey((value.AccountId, jid.ToLowerInvariant())))
			_ = this.LoadNeuronAsync(value.AccountId, jid);
	}

	private void Service_Changed(object? sender, Guid accountId)
	{
		lock (this.reload)
			this.reload.Add(accountId);
		Interlocked.Exchange(ref this.dirty, 1);
	}

	private void Service_PetitionsChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref this.dirty, 1);
	private void Monitors_NeuronsChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref this.dirty, 1);

	private void Tick()
	{
		Guid[] load;
		lock (this.reload)
		{
			load = [.. this.reload];
			this.reload.Clear();
		}

		foreach (Guid id in load)
			_ = this.LoadMineAsync(id);

		if (Interlocked.Exchange(ref this.dirty, 0) == 1)
			this.Refresh();
	}

	private Guid? onlyAccount;
	private (Guid Account, string Jid)? onlyNeuron;

	/// <summary>True on an account's page: only that account's identities, with no switch to the Neurons.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOpen), nameof(ShowsList))]
	private bool isOneAccount;

	/// <summary>True on a Neuron's page: only that Neuron's identity and the applications it has waiting.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOpen), nameof(ShowsList))]
	private bool isOneNeuron;

	/// <summary>True when the screen shows everything and offers the switch between Mine and Neurons.</summary>
	public bool IsOpen => !this.IsOneAccount && !this.IsOneNeuron;

	/// <summary>A Neuron's page has one row, so it needs no list.</summary>
	public bool ShowsList => !this.IsOneNeuron;

	/// <summary>Shows one account's identities. Used by the account's page.</summary>
	public void ShowAccount(Guid accountId)
	{
		this.onlyNeuron = null;
		this.onlyAccount = accountId;
		this.IsOneNeuron = false;
		this.IsOneAccount = true;
		this.Selected = null;
		if (this.Mode != IdentitiesMode.Mine)
			this.Mode = IdentitiesMode.Mine;
		this.Activate();
	}

	/// <summary>Shows one Neuron's identity and its waiting applications. Used by the Neuron's page.</summary>
	public void ShowNeuron(Guid accountId, string neuronJid)
	{
		this.onlyAccount = null;
		this.onlyNeuron = (accountId, neuronJid);
		this.IsOneAccount = false;
		this.IsOneNeuron = true;
		this.Selected = null;
		if (this.Mode != IdentitiesMode.Neurons)
			this.Mode = IdentitiesMode.Neurons;
		this.Activate();
		this.Selected = this.Rows.FirstOrDefault();
	}

	/// <summary>Loads the identities of every account that has the service. Call when the workspace opens.</summary>
	public void Activate()
	{
		foreach (AccountSession s in this.accounts.Sessions)
		{
			if (this.identities.IsAvailable(s.Profile.Id))
				_ = this.LoadMineAsync(s.Profile.Id);
		}

		if (this.IsNeurons && DateTime.UtcNow - this.neuronsLoaded > TimeSpan.FromMinutes(2))
			_ = this.LoadNeuronsAsync();

		this.Refresh();
	}

	private async Task LoadMineAsync(Guid accountId)
	{
		if (!this.identities.IsAvailable(accountId))
		{
			this.mine.Remove(accountId);
			this.Refresh();
			return;
		}

		// Only the newest request may set the list: an older answer can arrive after a newer one.
		int version;
		lock (this.loadVersions)
			version = this.loadVersions[accountId] = (this.loadVersions.TryGetValue(accountId, out int v) ? v : 0) + 1;

		bool Latest()
		{
			lock (this.loadVersions)
				return this.loadVersions[accountId] == version;
		}

		try
		{
			IReadOnlyList<LegalIdentity> list = await this.identities.GetIdentitiesAsync(accountId);
			Dispatcher.UIThread.Post(() =>
			{
				if (!Latest())
					return;

				this.mine[accountId] = list;
				this.loadErrors.Remove(accountId);
				this.Refresh();
			});
		}
		catch (Exception ex)
		{
			Dispatcher.UIThread.Post(() =>
			{
				if (!Latest())
					return;

				this.loadErrors[accountId] = ex.Message;
				this.Refresh();
			});
		}
	}

	private async Task LoadNeuronsAsync()
	{
		if (this.loadingNeurons)
			return;

		this.loadingNeurons = true;
		this.neuronsLoaded = DateTime.UtcNow;
		try
		{
			foreach (WatchedNeuron n in this.monitors.Neurons.Where(n => n.State == WatchState.Live).ToList())
				await this.LoadNeuronAsync(n.AccountId, n.BareJid);
		}
		finally
		{
			this.loadingNeurons = false;
		}
	}

	private async Task LoadNeuronAsync(Guid accountId, string neuronJid)
	{
		NeuronIdentity id;
		IReadOnlyList<PendingApplication>? apps;
		try
		{
			id = await this.identities.NeuronIdentityAsync(accountId, neuronJid);
		}
		catch (Exception ex)
		{
			id = new NeuronIdentity(accountId, neuronJid, null, null, null, ex.Message);
		}

		try
		{
			apps = await this.identities.PendingApplicationsAsync(accountId, neuronJid);
		}
		catch (Exception)
		{
			apps = null;
		}

		Dispatcher.UIThread.Post(() =>
		{
			this.neuronIds[(accountId, neuronJid.ToLowerInvariant())] = id;
			this.pending[(accountId, neuronJid.ToLowerInvariant())] = apps;
			this.Refresh();
		});
	}

	/// <summary>Rebuilds the list. UI thread.</summary>
	public void Refresh()
	{
		DateTime now = DateTime.Now;
		Dictionary<Guid, AccountProfile> profiles = this.accounts.Sessions.ToDictionary(s => s.Profile.Id, s => s.Profile);
		IReadOnlyList<IdentityPetition> petitions = this.identities.Petitions;
		this.PetitionCount = petitions.Count;

		List<IdentityRowViewModel> keep = [];
		IdentityRowViewModel Row(string key, IdentityRowKind kind, Guid accountId)
		{
			IdentityRowViewModel r = this.Rows.FirstOrDefault(x => x.Key == key) ?? new IdentityRowViewModel(key, kind, accountId);
			AccountProfile? p = profiles.TryGetValue(accountId, out AccountProfile? x) ? x : null;
			r.AccountLabel = p?.Label ?? string.Empty;
			r.AccountColorIndex = p?.ColorIndex ?? 0;
			keep.Add(r);
			return r;
		}

		if (this.IsMine)
		{
			foreach (IdentityPetition pt in petitions.Where(pt => this.onlyAccount is not Guid only || pt.AccountId == only))
			{
				IdentityRowViewModel r = Row("petition:" + pt.PetitionId, IdentityRowKind.Petition, pt.AccountId);
				r.Petition = pt;
				r.Title = (pt.RequestorName ?? pt.From) + (pt.Kind == "signature" ? " asks for your signature" : " asks to see your identity");
				r.Meta = string.IsNullOrWhiteSpace(pt.Purpose) ? pt.From : pt.Purpose;
				r.StateLabel = "Waiting for you";
				r.Tone = "busy";
				r.HasState = true;
			}

			foreach (AccountSession s in this.accounts.Sessions.Where(s => this.onlyAccount is not Guid only || s.Profile.Id == only).OrderBy(s => s.Profile.Label, StringComparer.OrdinalIgnoreCase))
			{
				Guid id = s.Profile.Id;
				IdentityRowViewModel a = Row("account:" + id, IdentityRowKind.Account, id);
				a.Title = s.Profile.Label;
				IReadOnlyList<LegalIdentity> list = this.mine.TryGetValue(id, out IReadOnlyList<LegalIdentity>? l) ? l : [];
				int approved = list.Count(i => i.State == IdentityState.Approved);
				int waiting = list.Count(i => i.State == IdentityState.Created);
				int retired = list.Count(i => !InUse(i));
				string history = retired == 0 ? string.Empty : $"{retired} retired or rejected";
				a.Meta = s.Status != ConnectionStatus.Connected ? "Offline"
					: this.identities.Problem(id) is string problem ? problem
					: !this.identities.IsAvailable(id) ? "Looking for the identity service…"
					: this.loadErrors.TryGetValue(id, out string? err) ? err
					: list.Count == 0 ? "No legal identity yet"
					: string.Join(" · ", new[] { approved > 0 ? $"{approved} approved" : null, waiting > 0 ? $"{waiting} waiting for review" : null,
						approved + waiting == 0 ? "None in use" : null, history.Length > 0 ? history : null }.Where(x => x is not null));
				a.HasState = false;

				foreach (LegalIdentity i in list.Where(i => this.ShowRetired || InUse(i)).OrderBy(i => StateOrder(i.State)).ThenByDescending(i => i.Created))
				{
					IdentityRowViewModel r = Row("identity:" + i.Id, IdentityRowKind.Identity, id);
					r.Identity = i;
					r.Title = NameOf(i) ?? "No name given";
					(r.StateLabel, r.Tone) = StateText(i);
					r.Meta = i.State switch
					{
						IdentityState.Approved => i.To < now.AddDays(30) ? $"Expires {RelativeDay(i.To, now)}" : $"Valid until {i.To:yyyy-MM-dd}",
						IdentityState.Created => $"Applied {RelativeTime.Format(i.Created.ToLocalTime(), now)}",
						_ => $"{r.StateLabel} {i.Updated.ToLocalTime():yyyy-MM-dd HH:mm}",
					};
					r.HasState = true;
				}
			}

			this.RetiredCount = this.mine.Where(m => this.onlyAccount is not Guid only || m.Key == only).Sum(m => m.Value.Count(i => !InUse(i)));
		}
		else
		{
			foreach (WatchedNeuron n in this.monitors.Neurons
				.Where(n => this.onlyNeuron is not (Guid a, string j) || (n.AccountId == a && string.Equals(n.BareJid, j, StringComparison.OrdinalIgnoreCase)))
				.OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase))
			{
				string jid = n.BareJid.ToLowerInvariant();
				IdentityRowViewModel r = Row("neuron:" + n.AccountId + ":" + jid, IdentityRowKind.Neuron, n.AccountId);
				r.NeuronJid = n.BareJid;
				r.Title = n.Label;
				NeuronIdentity? nid = this.neuronIds.TryGetValue((n.AccountId, jid), out NeuronIdentity? x) ? x : null;
				IReadOnlyList<PendingApplication>? apps = this.pending.TryGetValue((n.AccountId, jid), out IReadOnlyList<PendingApplication>? y) ? y : null;

				if (n.State != WatchState.Live)
				{
					r.Meta = "Not connected as an administrator";
					(r.StateLabel, r.Tone, r.HasState) = ("Offline", "idle", true);
				}
				else if (nid is null)
				{
					r.Meta = "Asking the Neuron…";
					(r.StateLabel, r.Tone, r.HasState) = (string.Empty, "idle", false);
				}
				else if (nid.Id is null)
				{
					r.Meta = nid.Error ?? "Has no legal identity of its own";
					(r.StateLabel, r.Tone, r.HasState) = ("No ID", "bad", true);
				}
				else
				{
					DateTime? to = nid.Expires ?? nid.Identity?.To;
					r.Meta = to is DateTime t ? (t < now.AddDays(60) ? $"Its ID expires {RelativeDay(t, now)}" : $"Its ID is valid until {t:yyyy-MM-dd}") : "Has a legal identity";
					(r.StateLabel, r.Tone, r.HasState) = to is DateTime t2 && t2 < now ? ("Expired", "bad", true)
						: to is DateTime t3 && t3 < now.AddDays(60) ? ("Expires soon", "busy", true)
						: ("ID ok", "ok", true);
				}

				int count = apps?.Count ?? 0;
				r.Badge = count > 0 ? count.ToString(CultureInfo.CurrentCulture) : null;
				r.HasBadge = count > 0;
			}
		}

		// The filter box: rows that say what was typed, and the account each of them belongs to.
		string filter = this.FilterText.Trim();
		if (filter.Length > 0)
		{
			bool Says(IdentityRowViewModel r) => r.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Meta.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
				r.StateLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.AccountLabel.Contains(filter, StringComparison.OrdinalIgnoreCase);
			HashSet<Guid> withMatches = [.. keep.Where(r => !r.IsAccount && Says(r)).Select(r => r.AccountId)];
			keep.RemoveAll(r => r.IsAccount ? !withMatches.Contains(r.AccountId) && !Says(r) : !Says(r));
		}

		IdentityRowViewModel? selected = this.Selected;
		for (int i = this.Rows.Count - 1; i >= 0; i--)
		{
			if (!keep.Contains(this.Rows[i]))
				this.Rows.RemoveAt(i);
		}

		for (int i = 0; i < keep.Count; i++)
		{
			int existing = this.Rows.IndexOf(keep[i]);
			if (existing < 0)
				this.Rows.Insert(i, keep[i]);
			else if (existing != i)
				this.Rows.Move(existing, i);
		}

		if (selected is not null && !keep.Contains(selected))
			this.Selected = null;
		else if (this.Selected is null && keep.Count > 0 && this.IsOneNeuron)
			this.Selected = keep[0];
		else if (this.Selected is null && keep.Count > 0 && this.IsMine)
			this.Selected = keep.FirstOrDefault(r => r.Kind == IdentityRowKind.Petition) ?? keep.FirstOrDefault(r => r.Kind == IdentityRowKind.Identity && r.Identity?.State == IdentityState.Approved) ?? keep[0];
		else
			this.ShowDetail();

		this.HasRows = keep.Count > 0;
		this.OnPropertyChanged(nameof(this.IsEmptyFiltered));
		this.OnPropertyChanged(nameof(this.IsEmptyUnfiltered));
		int total = this.mine.Values.Sum(l => l.Count(i => i.State is IdentityState.Approved or IdentityState.Created));
		this.Summary = this.IsMine
			? (petitions.Count > 0 ? $"{petitions.Count} request{(petitions.Count == 1 ? "" : "s")}" : total == 0 ? "None in use" : $"{total} in use")
			: $"{this.pending.Values.Sum(p => p?.Count ?? 0)} waiting for review";
	}

	private void ShowDetail()
	{
		IdentityRowViewModel? r = this.Selected;
		this.Facts.Clear();
		this.Properties.Clear();
		this.Attachments.Clear();
		this.PendingRows.Clear();
		this.DetailNote = null;
		this.CanApply = false;
		this.CanRetire = false;
		this.CanReportCompromised = false;
		this.HasProperties = false;
		this.HasAttachments = false;
		this.HasPending = false;
		this.NeuronWebBase = null;

		if (r is null)
			return;

		AccountSession? session = this.accounts.Sessions.FirstOrDefault(s => s.Profile.Id == r.AccountId);
		string accountText = session is null ? string.Empty : $"{session.Profile.Label} · {session.BareJid ?? session.Profile.UserName + "@" + session.Profile.Host}";

		switch (r.Kind)
		{
			case IdentityRowKind.Account:
				this.DetailTitle = "Apply for a legal identity";
				this.DetailSubtitle = accountText;
				(this.DetailState, this.DetailTone) = (string.Empty, "idle");
				this.CanApply = this.identities.IsAvailable(r.AccountId);
				this.DetailNote = !this.CanApply
					? r.Meta
					: EngineRuntime.KeepsKeys
						? "The Neuron's operators review the application. Its signing keys are made on this computer and kept encrypted here, so this is the computer that can sign with it."
						: "The Neuron's operators review the application. On this platform the signing keys are kept for this session only: after a restart the identity can no longer sign.";
				break;

			case IdentityRowKind.Identity when r.Identity is LegalIdentity i:
				this.DetailTitle = NameOf(i) ?? "No name given";
				this.DetailSubtitle = accountText;
				(this.DetailState, this.DetailTone) = StateText(i);
				this.CanRetire = i.State is IdentityState.Approved or IdentityState.Created;
				this.CanReportCompromised = i.State == IdentityState.Approved;
				this.OnPropertyChanged(nameof(this.IsApplication));
				this.OnPropertyChanged(nameof(this.RetireLabel));

				this.Facts.Add(new IdentityFact("Identity", i.Id, true));
				this.Facts.Add(new IdentityFact("Issued by", i.Provider, true));
				this.Facts.Add(new IdentityFact("Applied", i.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)));
				if (i.Updated > i.Created)
					this.Facts.Add(new IdentityFact("Last change", i.Updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)));
				this.Facts.Add(new IdentityFact("Valid", $"{i.From:yyyy-MM-dd} to {i.To:yyyy-MM-dd}"));
				if (!string.IsNullOrEmpty(i.ClientKeyName))
					this.Facts.Add(new IdentityFact("Signing key", i.ClientKeyName));

				foreach (Property p in i.Properties ?? [])
					this.Properties.Add(new IdentityFact(PropertyLabel(p.Name), p.Value));
				foreach (Attachment at in i.Attachments ?? [])
					this.Attachments.Add(new IdentityFact(at.FileName, at.ContentType));

				this.HasProperties = this.Properties.Count > 0;
				this.HasAttachments = this.Attachments.Count > 0;
				this.DetailNote = i.State == IdentityState.Created ? "Waiting for the Neuron's operators to review it." : null;
				break;

			case IdentityRowKind.Petition when r.Petition is IdentityPetition p:
				this.DetailTitle = r.Title;
				this.DetailSubtitle = accountText;
				(this.DetailState, this.DetailTone) = ("Waiting for you", "busy");
				this.Facts.Add(new IdentityFact("From", p.From, true));
				if (p.RequestorName is not null)
					this.Facts.Add(new IdentityFact("Name", p.RequestorName));
				this.Facts.Add(new IdentityFact("Purpose", string.IsNullOrWhiteSpace(p.Purpose) ? "Not given" : p.Purpose));
				this.Facts.Add(new IdentityFact(p.Kind == "signature" ? "Signs with" : "Identity", p.IdentityId, true));
				this.Facts.Add(new IdentityFact("Received", RelativeTime.Format(p.Received, DateTime.Now)));
				this.DetailNote = p.Kind == "signature"
					? "Accepting signs what they sent with your newest approved identity."
					: "Accepting shows them this identity, with every property in it.";
				break;

			case IdentityRowKind.Neuron when r.NeuronJid is string jid:
				this.DetailTitle = r.Title;
				this.DetailSubtitle = $"{jid} · through {session?.Profile.Label ?? "an account"}";
				(this.DetailState, this.DetailTone) = (r.StateLabel, r.Tone);
				string web = this.WebBase(r.AccountId, jid);
				this.NeuronWebBase = web;

				if (this.neuronIds.TryGetValue((r.AccountId, jid.ToLowerInvariant()), out NeuronIdentity? nid))
				{
					if (nid.Id is not null)
					{
						this.Facts.Add(new IdentityFact("Its identity", nid.Id, true));
						if (nid.Expires is DateTime t)
							this.Facts.Add(new IdentityFact("Expires", $"{t:yyyy-MM-dd} ({RelativeDay(t, DateTime.Now)})"));
						if (nid.Identity is LegalIdentity ni)
						{
							this.Facts.Add(new IdentityFact("State", StateText(ni).Label));
							this.Facts.Add(new IdentityFact("Issued by", ni.Provider, true));
						}
					}
					else
					{
						this.Facts.Add(new IdentityFact("Its identity", nid.Error ?? "None. Contracts that need the Neuron's signature cannot be signed."));
					}
				}

				if (this.pending.TryGetValue((r.AccountId, jid.ToLowerInvariant()), out IReadOnlyList<PendingApplication>? apps))
				{
					if (apps is null)
						this.PendingSummary = "The Neuron did not list its applications.";
					else
					{
						foreach (PendingApplication a in apps)
						{
							this.PendingRows.Add(new PendingApplicationRow(a,
								a.Created is DateTime c ? RelativeTime.Format(c, DateTime.Now) : string.Empty,
								web + "/LegalIdentity.md?Id=" + Uri.EscapeDataString(a.Id)));
						}

						this.HasPending = apps.Count > 0;
						this.PendingSummary = apps.Count switch
						{
							0 => "No applications are waiting.",
							1 => "One application is waiting for review.",
							_ => $"{apps.Count} applications are waiting for review.",
						};
					}
				}
				else
					this.PendingSummary = "Asking the Neuron…";

				this.DetailNote = "Approve or reject applications on the Neuron's own admin pages; they open in your browser.";
				break;
		}
	}

	/// <summary>
	/// Where the Neuron's admin pages are: taken from the links in its own notifications when it
	/// sent any, otherwise its domain.
	/// </summary>
	private string WebBase(Guid accountId, string neuronJid)
	{
		foreach (Engine.Attention.NeuronNotification n in this.monitors.Attention.Items)
		{
			if (n.AccountId != accountId || !string.Equals(n.NeuronJid, neuronJid, StringComparison.OrdinalIgnoreCase))
				continue;

			foreach (Engine.Attention.NotificationLink l in n.Links)
			{
				if (Uri.TryCreate(l.Url, UriKind.Absolute, out Uri? u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
					return u.GetLeftPart(UriPartial.Authority);
			}
		}

		AccountSession? s = this.accounts.Sessions.FirstOrDefault(x => x.Profile.Id == accountId);
		string domain = s is null ? neuronJid : !string.IsNullOrEmpty(s.Profile.Domain) ? s.Profile.Domain : s.Client?.Domain is string d && d.Length > 0 ? d : s.Profile.Host;
		return "https://" + domain;
	}

	// ---- Actions ---------------------------------------------------------------------------

	[RelayCommand]
	private void SetMode(IdentitiesMode m) => this.Mode = m;

	[RelayCommand]
	private async Task Apply()
	{
		if (this.Selected is not { Kind: IdentityRowKind.Account } r)
			return;

		ApplyField? missing = this.ApplyFields.FirstOrDefault(f => f.Required && string.IsNullOrWhiteSpace(f.Value));
		if (missing is not null)
		{
			this.DetailError = $"{missing.Label.TrimEnd(' ', '*')} is needed.";
			return;
		}

		List<Property> props = [.. this.ApplyFields
			.Where(f => !string.IsNullOrWhiteSpace(f.Value))
			.Select(f => new Property(f.Name, f.Name == "COUNTRY" ? f.Value.Trim().ToUpperInvariant() : f.Value.Trim()))];

		await this.RunAsync(async () =>
		{
			LegalIdentity id = await this.identities.ApplyAsync(r.AccountId, props);
			foreach (ApplyField f in this.ApplyFields)
				f.Value = string.Empty;

			// Show it at once, and ask for the list again so an answer already on its way cannot hide it.
			this.mine[r.AccountId] = [.. this.mine.TryGetValue(r.AccountId, out IReadOnlyList<LegalIdentity>? l) ? l.Where(x => x.Id != id.Id) : [], id];
			_ = this.LoadMineAsync(r.AccountId);

			this.Refresh();
			this.Selected = this.Rows.FirstOrDefault(x => x.Key == "identity:" + id.Id) ?? this.Selected;
		});
	}

	[RelayCommand]
	private async Task Validate()
	{
		if (this.Selected is not { Identity: LegalIdentity i } r)
			return;

		await this.RunAsync(async () =>
		{
			IdentityStatus status = await this.identities.ValidateAsync(r.AccountId, i);
			(this.Validation, this.ValidationTone) = status switch
			{
				IdentityStatus.Valid => ("Valid: the signatures check out, and the Neuron vouches for it.", "ok"),
				IdentityStatus.NotApproved => ("Not approved yet.", "busy"),
				IdentityStatus.NotValidYet => ("Not valid yet.", "busy"),
				IdentityStatus.NotValidAnymore => ("No longer valid: it has expired.", "bad"),
				_ => ($"Does not check out: {SplitWords(status.ToString())}.", "bad"),
			};
		});
	}

	[RelayCommand]
	private void AskRetire(string? kind) => this.Confirming = kind is "compromised" ? "compromised" : "obsolete";

	[RelayCommand]
	private void CancelConfirm() => this.Confirming = null;

	[RelayCommand]
	private async Task Confirm()
	{
		if (this.Selected is not { Identity: LegalIdentity i } r || this.Confirming is not string what)
			return;

		this.Confirming = null;
		await this.RunAsync(async () =>
		{
			LegalIdentity updated = what == "compromised"
				? await this.identities.CompromisedAsync(r.AccountId, i.Id)
				: await this.identities.ObsoleteAsync(r.AccountId, i.Id);
			r.Identity = updated;
			await this.LoadMineAsync(r.AccountId);
		});
	}

	[RelayCommand]
	private async Task Respond(string? answer)
	{
		if (this.Selected is not { Petition: IdentityPetition p })
			return;

		await this.RunAsync(async () =>
		{
			await this.identities.RespondAsync(p, answer == "accept");
			this.Refresh();
		});
	}

	[RelayCommand]
	private async Task RefreshNeuron()
	{
		if (this.Selected is { Kind: IdentityRowKind.Neuron, NeuronJid: string jid } r)
		{
			this.PendingSummary = "Asking the Neuron…";
			await this.RunAsync(() => this.LoadNeuronAsync(r.AccountId, jid));
		}
		else
			await this.LoadNeuronsAsync();
	}

	[RelayCommand]
	private async Task OpenReview(PendingApplicationRow? row)
	{
		if (row is not null && this.OpenUrl is not null)
			await this.OpenUrl(row.ReviewUrl);
	}

	[RelayCommand]
	private async Task OpenPending()
	{
		if (this.NeuronWebBase is string web && this.OpenUrl is not null)
			await this.OpenUrl(web + "/PendingLegalIdentities.md");
	}

	[RelayCommand]
	private async Task CopyId()
	{
		string? id = this.Selected?.Identity?.Id ?? this.Selected?.Petition?.IdentityId;
		if (id is not null && this.CopyToClipboard is not null)
			await this.CopyToClipboard(id);
	}

	private async Task RunAsync(Func<Task> action)
	{
		this.IsBusy = true;
		this.DetailError = null;
		try
		{
			await action();
		}
		catch (Exception ex)
		{
			this.DetailError = ex.Message;
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	// ---- Text ------------------------------------------------------------------------------

	private static bool InUse(LegalIdentity i) => i.State is IdentityState.Approved or IdentityState.Created;

	private static int StateOrder(IdentityState s) => s switch
	{
		IdentityState.Approved => 0,
		IdentityState.Created => 1,
		IdentityState.Compromised => 2,
		_ => 3,
	};

	internal static (string Label, string Tone) StateText(LegalIdentity i) => i.State switch
	{
		IdentityState.Approved when i.To < DateTime.UtcNow => ("Expired", "bad"),
		IdentityState.Approved => ("Approved", "ok"),
		IdentityState.Created => ("Waiting for review", "busy"),
		IdentityState.Rejected => ("Rejected", "idle"),
		IdentityState.Obsoleted => ("Retired", "idle"),
		IdentityState.Compromised => ("Compromised", "bad"),
		_ => (i.State.ToString(), "idle"),
	};

	internal static string? NameOf(LegalIdentity i)
	{
		string? full = i["FULLNAME"];
		if (!string.IsNullOrWhiteSpace(full))
			return full;

		string name = string.Join(" ", new[] { i["FIRST"], i["MIDDLE"], i["LAST"] }.Where(s => !string.IsNullOrWhiteSpace(s)));
		if (name.Length > 0)
			return name;

		string? org = i["ORGNAME"];
		return string.IsNullOrWhiteSpace(org) ? null : org;
	}

	private static string PropertyLabel(string name) => name switch
	{
		"FIRST" => "First name",
		"MIDDLE" => "Middle names",
		"LAST" => "Last name",
		"FULLNAME" => "Full name",
		"PNR" => "Personal number",
		"ADDR" => "Address",
		"ADDR2" => "Address, line 2",
		"ZIP" => "Postal code",
		"AREA" => "Area",
		"CITY" => "City",
		"REGION" => "Region",
		"COUNTRY" => "Country",
		"NATIONALITY" => "Nationality",
		"GENDER" => "Gender",
		"BDAY" => "Birth day",
		"BMONTH" => "Birth month",
		"BYEAR" => "Birth year",
		"ORGNAME" => "Organization",
		"ORGNR" => "Organization number",
		"ORGROLE" => "Role",
		"ORGDEPT" => "Department",
		"ORGADDR" => "Organization address",
		"ORGCITY" => "Organization city",
		"ORGCOUNTRY" => "Organization country",
		"EMAIL" => "E-mail",
		"PHONE" => "Phone",
		"JID" => "Network address",
		"DEVICE_ID" => "Device",
		_ => name,
	};

	private static string RelativeDay(DateTime when, DateTime now)
	{
		int days = (int)Math.Round((when.Date - now.Date).TotalDays);
		return days switch
		{
			< -1 => $"{-days} days ago",
			-1 => "yesterday",
			0 => "today",
			1 => "tomorrow",
			_ => $"in {days} days",
		};
	}

	private static string SplitWords(string s)
		=> string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : (i == 0 ? char.ToLowerInvariant(c).ToString() : c.ToString())));

	public void Dispose()
	{
		this.timer.Stop();
		this.identities.Changed -= this.Service_Changed;
		this.identities.PetitionsChanged -= this.Service_PetitionsChanged;
		this.monitors.NeuronsChanged -= this.Monitors_NeuronsChanged;
	}
}
