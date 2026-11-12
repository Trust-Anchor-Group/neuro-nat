using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Devices;

namespace Neuro.Nat.App.ViewModels;

/// <summary>What the Devices list shows.</summary>
public enum DevicesMode
{
	Mine,
	Search,
	Questions,
}

/// <summary>A row in the Devices list: a device, or a question about one.</summary>
public sealed partial class DeviceRowViewModel(string key, Guid accountId) : ObservableObject
{
	public string Key { get; } = key;
	public Guid AccountId { get; } = accountId;

	[ObservableProperty] private string title = string.Empty;
	[ObservableProperty] private string meta = string.Empty;
	[ObservableProperty] private string accountLabel = string.Empty;
	[ObservableProperty] private int accountColorIndex;
	[ObservableProperty] private string stateLabel = string.Empty;
	[ObservableProperty] private string tone = "idle";
	[ObservableProperty] private bool hasState;

	public RegistryThing? Thing { get; set; }
	public DeviceQuestion? Question { get; set; }
	public bool Owned { get; set; }
}

/// <summary>A tag of a device, or a fact about a question.</summary>
public sealed record DeviceFact(string Name, string Value);

/// <summary>An account to pick for searching or claiming.</summary>
public sealed record DeviceAccountChoice(Guid Id, string Label)
{
	public override string ToString() => this.Label;
}

/// <summary>
/// The Devices workspace: devices your accounts own, searching the thing registry, claiming a
/// device with its key, and the provisioning questions only an owner can answer.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
	private readonly DevicesService devices;
	private readonly AccountManager accounts;
	private readonly Dictionary<Guid, IReadOnlyList<RegistryThing>> mine = [];
	private IReadOnlyList<(Guid Account, RegistryThing Thing)> found = [];

	public DevicesViewModel(DevicesService devices, AccountManager accounts)
	{
		this.devices = devices;
		this.accounts = accounts;
		devices.QuestionsChanged += (_, _) => Dispatcher.UIThread.Post(this.Refresh);
		devices.Changed += (_, id) => Dispatcher.UIThread.Post(() => _ = this.LoadMineAsync(id));
	}

	public ObservableCollection<DeviceRowViewModel> Rows { get; } = [];
	public ObservableCollection<DeviceFact> Facts { get; } = [];
	public ObservableCollection<DeviceAccountChoice> AccountChoices { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMine), nameof(IsSearch), nameof(IsQuestions))]
	private DevicesMode mode = DevicesMode.Mine;

	public bool IsMine => this.Mode == DevicesMode.Mine;
	public bool IsSearch => this.Mode == DevicesMode.Search;
	public bool IsQuestions => this.Mode == DevicesMode.Questions;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowsThing), nameof(ShowsQuestion), nameof(CanDisown), nameof(IsFriendship))]
	private DeviceRowViewModel? selected;

	public bool HasSelection => this.Selected is not null || this.IsClaiming;
	public bool ShowsThing => this.Selected?.Thing is not null && !this.IsClaiming;
	public bool ShowsQuestion => this.Selected?.Question is not null && !this.IsClaiming;
	public bool CanDisown => this.Selected is { Owned: true, Thing: not null };
	public bool IsFriendship => this.Selected?.Question?.Kind == DeviceQuestionKind.Friendship;

	[ObservableProperty] private bool hasRows;
	[ObservableProperty] private string emptyText = string.Empty;
	[ObservableProperty] private string searchText = string.Empty;
	[ObservableProperty] private DeviceAccountChoice? account;
	[ObservableProperty] private bool isBusy;
	[ObservableProperty] private string? error;
	[ObservableProperty] private string? note;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasQuestions), nameof(QuestionsLabel))]
	private int questionCount;

	public bool HasQuestions => this.QuestionCount > 0;
	public string QuestionsLabel => this.QuestionCount == 0 ? "Questions" : $"Questions ({this.QuestionCount})";

	[ObservableProperty] private string detailTitle = string.Empty;
	[ObservableProperty] private string detailSubtitle = string.Empty;

	/// <summary>How widely an answer applies: 0 the caller, 1 their domain, 2 everyone.</summary>
	[ObservableProperty] private int scopeIndex;

	[ObservableProperty] private bool isConfirmingDisown;

	// Claim form
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowsThing), nameof(ShowsQuestion))]
	private bool isClaiming;

	[ObservableProperty] private string claimTags = "KEY=\nSN=";
	[ObservableProperty] private bool claimPublic;

	partial void OnModeChanged(DevicesMode value)
	{
		this.Selected = null;
		this.Refresh();
	}

	partial void OnSelectedChanged(DeviceRowViewModel? value)
	{
		if (value is not null)
			this.IsClaiming = false;
		this.IsConfirmingDisown = false;
		this.Error = null;
		this.Note = null;
		this.ScopeIndex = 0;
		this.ShowDetail();
	}

	/// <summary>The account whose devices show. Null shows every account's.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOneAccount))]
	private Guid? onlyAccount;

	/// <summary>True on an account's page, where the account is already chosen.</summary>
	public bool IsOneAccount => this.OnlyAccount is not null;

	/// <summary>Shows one account's devices, registry searches and questions. Used by the account's page.</summary>
	public void ShowAccount(Guid accountId)
	{
		this.OnlyAccount = accountId;
		this.SyncAccounts();
		this.Account = this.AccountChoices.FirstOrDefault(c => c.Id == accountId) ?? this.Account;
		this.Activate();
	}

	private bool Shows(Guid accountId) => this.OnlyAccount is not Guid only || only == accountId;

	/// <summary>Called when the workspace opens.</summary>
	public void Activate()
	{
		this.SyncAccounts();
		foreach (AccountSession s in this.accounts.Sessions.Where(s => this.devices.IsAvailable(s.Profile.Id)))
			_ = this.LoadMineAsync(s.Profile.Id);
		this.Refresh();
	}

	private void SyncAccounts()
	{
		foreach (AccountSession s in this.accounts.Sessions)
		{
			if (!this.AccountChoices.Any(c => c.Id == s.Profile.Id))
				this.AccountChoices.Add(new DeviceAccountChoice(s.Profile.Id, s.Profile.Label));
		}
		this.Account ??= this.AccountChoices.FirstOrDefault(c => this.devices.IsAvailable(c.Id)) ?? this.AccountChoices.FirstOrDefault();
	}

	private async Task LoadMineAsync(Guid accountId)
	{
		if (!this.devices.IsAvailable(accountId))
			return;

		try
		{
			IReadOnlyList<RegistryThing> list = await this.devices.MyDevicesAsync(accountId);
			Dispatcher.UIThread.Post(() =>
			{
				this.mine[accountId] = list;
				this.Refresh();
			});
		}
		catch (Exception ex)
		{
			Dispatcher.UIThread.Post(() => this.Error = ex.Message);
		}
	}

	/// <summary>Rebuilds the list. UI thread.</summary>
	public void Refresh()
	{
		Dictionary<Guid, AccountProfile> profiles = this.accounts.Sessions.ToDictionary(s => s.Profile.Id, s => s.Profile);
		IReadOnlyList<DeviceQuestion> questions = this.devices.Questions;
		this.QuestionCount = questions.Count;

		List<DeviceRowViewModel> keep = [];
		DeviceRowViewModel Row(string key, Guid accountId)
		{
			DeviceRowViewModel r = this.Rows.FirstOrDefault(x => x.Key == key) ?? new DeviceRowViewModel(key, accountId);
			AccountProfile? p = profiles.TryGetValue(accountId, out AccountProfile? x) ? x : null;
			r.AccountLabel = p?.Label ?? string.Empty;
			r.AccountColorIndex = p?.ColorIndex ?? 0;
			keep.Add(r);
			return r;
		}

		switch (this.Mode)
		{
			case DevicesMode.Mine:
				foreach ((Guid id, IReadOnlyList<RegistryThing> list) in this.mine.Where(m => this.Shows(m.Key)))
				{
					foreach (RegistryThing t in list.OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase))
					{
						DeviceRowViewModel r = Row($"mine:{id}:{t.Jid}:{t.NodeId}:{t.SourceId}", id);
						(r.Thing, r.Owned, r.Question) = (t, true, null);
						r.Title = t.Label;
						r.Meta = t.NodeId is null ? t.Jid : $"{t.Jid} · {t.NodeId}";
						r.HasState = false;
					}
				}
				this.EmptyText = this.accounts.Sessions.Any(s => this.devices.IsAvailable(s.Profile.Id))
					? "Your accounts own no devices. Claim one with its key, or search the registry."
					: "None of your accounts' Neurons has a thing registry, or they are not connected.";
				break;

			case DevicesMode.Search:
				foreach ((Guid id, RegistryThing t) in this.found.Where(f => this.Shows(f.Account)))
				{
					DeviceRowViewModel r = Row($"found:{id}:{t.Jid}:{t.NodeId}:{t.SourceId}", id);
					bool owned = this.mine.TryGetValue(id, out IReadOnlyList<RegistryThing>? m) && m.Any(x => x.Jid == t.Jid && x.NodeId == t.NodeId);
					(r.Thing, r.Owned, r.Question) = (t, owned, null);
					r.Title = t.Label;
					r.Meta = t.NodeId is null ? t.Jid : $"{t.Jid} · {t.NodeId}";
					(r.StateLabel, r.Tone, r.HasState) = owned ? ("Yours", "ok", true) : (string.Empty, "idle", false);
				}
				this.EmptyText = "Search public devices by any tag: a name, a model, a serial number. Use * as a wildcard.";
				break;

			case DevicesMode.Questions:
				foreach (DeviceQuestion q in questions.Where(q => this.Shows(q.AccountId)).OrderByDescending(q => q.Received))
				{
					DeviceRowViewModel r = Row("question:" + q.Id, q.AccountId);
					(r.Question, r.Thing, r.Owned) = (q, null, false);
					r.Title = q.Kind switch
					{
						DeviceQuestionKind.Friendship => $"{q.RemoteJid} wants to be a contact",
						DeviceQuestionKind.Readout => $"{q.RemoteJid} wants to read data",
						_ => $"{q.RemoteJid} wants to control it",
					};
					r.Meta = q.NodeId is null ? q.DeviceJid : $"{q.DeviceJid} · {q.NodeId}";
					(r.StateLabel, r.Tone, r.HasState) = ("Waiting for you", "busy", true);
				}
				this.EmptyText = "No questions. When someone wants access to a device you own, its Neuron asks you here.";
				break;
		}

		DeviceRowViewModel? selected = this.Selected;
		for (int i = this.Rows.Count - 1; i >= 0; i--)
		{
			if (!keep.Contains(this.Rows[i]))
				this.Rows.RemoveAt(i);
		}
		for (int i = 0; i < keep.Count; i++)
		{
			int at = this.Rows.IndexOf(keep[i]);
			if (at < 0)
				this.Rows.Insert(i, keep[i]);
			else if (at != i)
				this.Rows.Move(at, i);
		}

		if (selected is not null && !keep.Contains(selected))
			this.Selected = null;
		else
			this.ShowDetail();

		this.HasRows = keep.Count > 0;
	}

	private void ShowDetail()
	{
		this.Facts.Clear();
		DeviceRowViewModel? r = this.Selected;
		AccountSession? s = r is null ? null : this.accounts.Sessions.FirstOrDefault(x => x.Profile.Id == r.AccountId);

		if (this.IsClaiming)
		{
			this.DetailTitle = "Claim a device";
			this.DetailSubtitle = "Give the tags the device registered with, including its KEY. The key is usually printed on the device or in its QR code.";
			return;
		}

		if (r?.Thing is RegistryThing t)
		{
			this.DetailTitle = t.Label;
			this.DetailSubtitle = r.Owned ? $"Owned through {s?.Profile.Label}" : $"Public in the registry · seen through {s?.Profile.Label}";
			this.Facts.Add(new DeviceFact("Address", t.Jid));
			if (t.NodeId is not null)
				this.Facts.Add(new DeviceFact("Node", t.NodeId));
			if (t.SourceId is not null)
				this.Facts.Add(new DeviceFact("Source", t.SourceId));
			foreach (KeyValuePair<string, string> tag in t.Tags.Where(x => x.Key != "KEY"))
				this.Facts.Add(new DeviceFact(TagLabel(tag.Key), tag.Value));
		}
		else if (r?.Question is DeviceQuestion q)
		{
			this.DetailTitle = r.Title;
			this.DetailSubtitle = $"Asked {RelativeTime.Format(q.Received, DateTime.Now)} · you own the device through {s?.Profile.Label}";
			this.Facts.Add(new DeviceFact("Device", q.DeviceJid));
			if (q.NodeId is not null)
				this.Facts.Add(new DeviceFact("Node", q.NodeId + (q.SourceId is null ? string.Empty : " in " + q.SourceId)));
			this.Facts.Add(new DeviceFact("Asked by", q.RemoteJid));
			if (q.Kind != DeviceQuestionKind.Friendship)
				this.Facts.Add(new DeviceFact(q.Kind == DeviceQuestionKind.Readout ? "Fields" : "Parameters", q.Names.Count == 0 ? "All" : string.Join(", ", q.Names)));
		}
	}

	private static string TagLabel(string tag) => tag switch
	{
		"MAN" => "Manufacturer",
		"MODEL" => "Model",
		"SN" => "Serial number",
		"NAME" => "Name",
		"CLASS" => "Class",
		"V" => "Version",
		"ROOM" => "Room",
		"BLD" => "Building",
		"CITY" => "City",
		"COUNTRY" => "Country",
		"LAT" => "Latitude",
		"LON" => "Longitude",
		_ => tag,
	};

	private async Task RunAsync(Func<Task> action)
	{
		this.IsBusy = true;
		this.Error = null;
		try
		{
			await action();
		}
		catch (Exception ex)
		{
			this.Error = ex.Message;
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	// ---- Actions ---------------------------------------------------------------------------

	[RelayCommand]
	private void SetMode(DevicesMode m) => this.Mode = m;

	[RelayCommand]
	private Task Search()
	{
		this.SyncAccounts();
		if (this.Account is not { } a)
			return Task.CompletedTask;

		return this.RunAsync(async () =>
		{
			IReadOnlyList<RegistryThing> list = await this.devices.SearchAsync(a.Id, this.SearchText);
			this.found = [.. list.Select(t => (a.Id, t))];
			this.Mode = DevicesMode.Search;
			this.Refresh();
			this.Note = list.Count == 0 ? "No public device matches." : null;
		});
	}

	[RelayCommand]
	private void StartClaim()
	{
		this.SyncAccounts();
		this.Selected = null;
		this.IsClaiming = true;
		this.Error = null;
		this.ShowDetail();
	}

	[RelayCommand]
	private void CancelClaim()
	{
		this.IsClaiming = false;
		this.ShowDetail();
	}

	[RelayCommand]
	private Task Claim()
	{
		if (this.Account is not { } a)
			return Task.CompletedTask;

		List<KeyValuePair<string, string>> tags = [];
		foreach (string line in this.ClaimTags.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			int i = line.IndexOf('=');
			if (i > 0 && line[(i + 1)..].Trim().Length > 0)
				tags.Add(new(line[..i].Trim().ToUpperInvariant(), line[(i + 1)..].Trim()));
		}

		if (!tags.Any(t => t.Key == "KEY"))
		{
			this.Error = "The KEY tag is needed to claim a device.";
			return Task.CompletedTask;
		}

		return this.RunAsync(async () =>
		{
			RegistryThing t = await this.devices.ClaimAsync(a.Id, tags, this.ClaimPublic);
			this.IsClaiming = false;
			this.ClaimTags = "KEY=\nSN=";
			await this.LoadMineAsync(a.Id);
			this.Mode = DevicesMode.Mine;
			this.Note = $"{t.Label} is yours now.";
		});
	}

	[RelayCommand]
	private void AskDisown() => this.IsConfirmingDisown = true;

	[RelayCommand]
	private void CancelDisown() => this.IsConfirmingDisown = false;

	[RelayCommand]
	private Task Disown()
	{
		if (this.Selected is not { Thing: RegistryThing t } r)
			return Task.CompletedTask;

		this.IsConfirmingDisown = false;
		return this.RunAsync(async () =>
		{
			await this.devices.DisownAsync(r.AccountId, t);
			await this.LoadMineAsync(r.AccountId);
		});
	}

	[RelayCommand]
	private Task Answer(string? verdict)
	{
		if (this.Selected?.Question is not DeviceQuestion q)
			return Task.CompletedTask;

		Engine.Devices.AnswerScope scope = this.ScopeIndex switch
		{
			1 => Engine.Devices.AnswerScope.Domain,
			2 when q.Kind == DeviceQuestionKind.Friendship => Engine.Devices.AnswerScope.Everyone,
			2 => Engine.Devices.AnswerScope.Domain,
			_ => Engine.Devices.AnswerScope.Caller,
		};

		return this.RunAsync(() => this.devices.AnswerAsync(q, verdict == "accept", scope));
	}

	[RelayCommand]
	private Task ClearCaches()
	{
		if (this.Account is not { } a)
			return Task.CompletedTask;
		return this.RunAsync(async () =>
		{
			await this.devices.ClearDeviceCachesAsync(a.Id);
			this.Note = "Your devices will ask again before they share data or accept contacts.";
		});
	}
}
