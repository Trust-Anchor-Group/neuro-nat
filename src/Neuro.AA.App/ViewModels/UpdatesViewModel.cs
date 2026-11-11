using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.Events;
using Neuro.AA.Engine.Software;

namespace Neuro.AA.App.ViewModels;

/// <summary>One Neuron in the Updates list.</summary>
public sealed partial class UpdateRowViewModel(Guid accountId, string neuronJid) : ObservableObject
{
	public Guid AccountId { get; } = accountId;
	public string NeuronJid { get; } = neuronJid;

	[ObservableProperty] private string label = string.Empty;
	[ObservableProperty] private string accountLabel = string.Empty;
	[ObservableProperty] private int accountColorIndex;
	[ObservableProperty] private string meta = "Not read yet";
	[ObservableProperty] private string stateLabel = string.Empty;
	[ObservableProperty] private string tone = "idle";
	[ObservableProperty] private bool hasState;
	[ObservableProperty] private bool isLive;

	public NeuronSoftware? Software { get; set; }
}

/// <summary>What asking one Neuron for newer software came to.</summary>
public sealed record UpdateCheck(Guid AccountId, string NeuronJid, string Label, bool Answered, bool HasUpdate, string Text)
{
	/// <summary>False for a Neuron that is not connected: there was nobody to ask.</summary>
	public bool WasAsked { get; init; } = true;
}

/// <summary>A line of what a check or an upgrade reported.</summary>
public sealed record UpdateLogLine(string Time, string Text);

/// <summary>
/// The Updates workspace: each Neuron's build, installed and downloaded packages and automatic
/// update setting, with checking for packages, cancelling a pending update and upgrading.
/// Everything goes through the Neuron's chat admin, which is the only way it offers.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject, IActionSource
{
	private readonly SoftwareService software;
	private readonly MonitorService monitors;
	private readonly AccountManager accounts;

	public UpdatesViewModel(SoftwareService software, MonitorService monitors, AccountManager accounts)
	{
		this.software = software;
		this.monitors = monitors;
		this.accounts = accounts;
		monitors.NeuronsChanged += (_, _) => Dispatcher.UIThread.Post(this.Sync);
	}

	public ObservableCollection<UpdateRowViewModel> Rows { get; } = [];
	public ObservableCollection<SoftwarePackage> Packages { get; } = [];
	public ObservableCollection<string> Installed { get; } = [];
	public ObservableCollection<UpdateLogLine> Log { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelection))]
	private UpdateRowViewModel? selected;

	public bool HasSelection => this.Selected is not null;

	[ObservableProperty] private bool hasRows;
	[ObservableProperty] private bool isBusy;
	[ObservableProperty] private string busyText = string.Empty;
	[ObservableProperty] private string? error;

	[ObservableProperty] private string buildText = string.Empty;
	[ObservableProperty] private string autoText = string.Empty;
	[ObservableProperty] private string autoMinutes = "0";
	[ObservableProperty] private bool contentAuto;
	[ObservableProperty] private bool hasPending;
	[ObservableProperty] private string pendingText = string.Empty;
	[ObservableProperty] private bool hasPackages;
	[ObservableProperty] private bool hasInstalled;
	[ObservableProperty] private bool hasLog;

	/// <summary>True while the operator confirms an upgrade by typing the Neuron's name.</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(UpgradeNowCommand))]
	private bool isConfirmingUpgrade;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(UpgradeNowCommand))]
	private string upgradeConfirmText = string.Empty;

	[ObservableProperty] private bool upgradeBackup = true;
	[ObservableProperty] private string upgradePackage = string.Empty;

	public string UpgradeConfirmHint => this.Selected is null ? string.Empty : $"Type {this.Selected.Label} to upgrade it";

	private bool syncing;
	private bool contentSyncing;

	partial void OnSelectedChanged(UpdateRowViewModel? value)
	{
		this.IsConfirmingUpgrade = false;
		this.UpgradeConfirmText = string.Empty;
		this.Error = null;
		this.Log.Clear();
		this.HasLog = false;
		this.OnPropertyChanged(nameof(this.UpgradeConfirmHint));
		this.ShowDetail();

		if (value is not null && value.Software is null && value.IsLive)
			_ = this.ReadAsync(value);
	}

	partial void OnContentAutoChanged(bool value)
	{
		if (this.contentSyncing || this.Selected is not { } r)
			return;

		_ = this.RunAsync(r, "Saving…", async () =>
		{
			this.AddLog(await this.software.SetContentAutoAsync(r.AccountId, r.NeuronJid, value));
			await this.ReadAsync(r, quiet: true);
		});
	}

	/// <summary>Set by the shell: the moves to other screens.</summary>
	public IShell? Shell { get; set; }

	public IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items)
	{
		if (items.Count != 1)
			return [];

		ActionList list = [];
		switch (items[0])
		{
			case UpdateRowViewModel row when this.Shell is IShell shell:
				list.AddRange(shell.NeuronActions(row.NeuronJid, row.AccountId, WorkspaceKind.Updates).Where(a => a.Text != "Check for updates"));
				break;

			case UpdateRowViewModel row:
				list.Copy("Copy address", () => row.NeuronJid, "the address", "Ctrl+C");
				break;

			case string installed:
				list.Copy("Copy name", () => installed, "the name", "Ctrl+C");
				list.Copy("Copy all installed packages", () => string.Join(Environment.NewLine, this.Installed), Ui.Count(this.Installed.Count, "package"));
				break;

			case UpdateLogLine line:
				list.Copy("Copy line", () => line.Text, "the line", "Ctrl+C");
				list.Copy("Copy the whole log", () => string.Join(Environment.NewLine, this.Log.Select(l => l.Time + "\t" + l.Text)), "the log");
				break;
		}

		return list;
	}

	/// <summary>True on a Neuron's page: the Neuron is already chosen, so the list of Neurons is not shown.</summary>
	[ObservableProperty] private bool isPinned;

	/// <summary>Shows one Neuron's software. Used by the Neuron's page.</summary>
	public void Focus(Guid accountId, string neuronJid)
	{
		this.IsPinned = true;
		this.Sync();
		UpdateRowViewModel? row = this.Rows.FirstOrDefault(r => r.AccountId == accountId && string.Equals(r.NeuronJid, neuronJid, StringComparison.OrdinalIgnoreCase));
		this.Selected = row;
		if (row is { IsLive: true, Software: null })
			_ = this.ReadAsync(row, quiet: true);
	}

	/// <summary>Called when the workspace opens: reads every live Neuron not read yet.</summary>
	public void Activate()
	{
		this.Sync();
		foreach (UpdateRowViewModel r in this.Rows.Where(r => r.IsLive && r.Software is null).ToList())
			_ = this.ReadAsync(r, quiet: true);
		this.Selected ??= this.Rows.FirstOrDefault(r => r.IsLive) ?? this.Rows.FirstOrDefault();
	}

	/// <summary>Updates the list of Neurons. UI thread.</summary>
	public void Sync()
	{
		if (this.syncing)
			return;

		this.syncing = true;
		try
		{
			Dictionary<Guid, AccountProfile> profiles = this.accounts.Sessions.ToDictionary(s => s.Profile.Id, s => s.Profile);
			List<UpdateRowViewModel> keep = [];
			foreach (WatchedNeuron n in this.monitors.Neurons.OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase))
			{
				UpdateRowViewModel r = this.Rows.FirstOrDefault(x => x.AccountId == n.AccountId && string.Equals(x.NeuronJid, n.BareJid, StringComparison.OrdinalIgnoreCase))
					?? new UpdateRowViewModel(n.AccountId, n.BareJid);
				r.Label = n.Label;
				r.IsLive = n.State == WatchState.Live;
				AccountProfile? p = profiles.TryGetValue(n.AccountId, out AccountProfile? x) ? x : null;
				r.AccountLabel = p?.Label ?? string.Empty;
				r.AccountColorIndex = p?.ColorIndex ?? 0;
				Describe(r);
				keep.Add(r);
			}

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

			this.HasRows = this.Rows.Count > 0;
		}
		finally
		{
			this.syncing = false;
		}
	}

	/// <summary>
	/// Asks several Neurons at once whether newer software is waiting. Each is asked on its own,
	/// so one that does not answer holds nobody up. Nothing is installed.
	/// </summary>
	public async Task<IReadOnlyList<UpdateCheck>> CheckManyAsync(IReadOnlyList<(Guid Account, string Jid)> neurons)
	{
		this.Sync();

		async Task<UpdateCheck> One(Guid account, string jid)
		{
			UpdateRowViewModel? r = this.Rows.FirstOrDefault(x => x.AccountId == account && string.Equals(x.NeuronJid, jid, StringComparison.OrdinalIgnoreCase));
			if (r is null || !r.IsLive)
				return new UpdateCheck(account, jid, r?.Label ?? jid, false, false, "Not connected") { WasAsked = false };

			try
			{
				await this.software.CheckAsync(account, jid);
				NeuronSoftware s = await this.software.ReadAsync(account, jid);
				r.Software = s;
				Describe(r);
				if (r == this.Selected)
					this.ShowDetail();

				return s.Error is not null
					? new UpdateCheck(account, jid, r.Label, false, false, "No answer")
					: s.PendingUpdate is not null
						? new UpdateCheck(account, jid, r.Label, true, true, r.StateLabel)
						: new UpdateCheck(account, jid, r.Label, true, false, "Up to date");
			}
			catch (Exception)
			{
				return new UpdateCheck(account, jid, r.Label, false, false, "No answer");
			}
		}

		return await Task.WhenAll(neurons.Select(n => One(n.Account, n.Jid)));
	}

	private static void Describe(UpdateRowViewModel r)
	{
		NeuronSoftware? s = r.Software;
		if (!r.IsLive)
		{
			(r.Meta, r.StateLabel, r.Tone, r.HasState) = ("Not connected as an administrator", "Offline", "idle", true);
			return;
		}

		if (s is null)
		{
			(r.Meta, r.StateLabel, r.Tone, r.HasState) = ("Reading…", string.Empty, "idle", false);
			return;
		}

		if (s.Error is not null)
		{
			(r.Meta, r.StateLabel, r.Tone, r.HasState) = (s.Error, "No answer", "bad", true);
			return;
		}

		string auto = s.AutoDelayMinutes is int m ? $"updates itself after {m} min" : "manual updates";
		r.Meta = (s.BuildTime is null ? string.Empty : "Built " + s.BuildTime + " · ") + auto;
		if (s.PendingUpdate is not null)
			(r.StateLabel, r.Tone, r.HasState) = (s.AutoDelayMinutes is null ? "Update ready" : "Update pending", "busy", true);
		else
			(r.StateLabel, r.Tone, r.HasState) = (string.Empty, "ok", false);
	}

	private async Task ReadAsync(UpdateRowViewModel r, bool quiet = false)
	{
		if (!quiet)
		{
			this.IsBusy = true;
			this.BusyText = "Asking the Neuron…";
		}

		try
		{
			NeuronSoftware s = await this.software.ReadAsync(r.AccountId, r.NeuronJid);
			Dispatcher.UIThread.Post(() =>
			{
				r.Software = s;
				Describe(r);
				if (r == this.Selected)
					this.ShowDetail();
			});
		}
		finally
		{
			if (!quiet)
				this.IsBusy = false;
		}
	}

	private void ShowDetail()
	{
		UpdateRowViewModel? r = this.Selected;
		NeuronSoftware? s = r?.Software;

		this.Packages.Clear();
		this.Installed.Clear();
		foreach (SoftwarePackage p in s?.Packages ?? [])
			this.Packages.Add(p);
		foreach (string i in s?.Installed ?? [])
			this.Installed.Add(i);
		this.HasPackages = this.Packages.Count > 0;
		this.HasInstalled = this.Installed.Count > 0;

		this.BuildText = s is null ? (r?.IsLive == false ? "Not connected" : "Reading…") : s.Error ?? (s.BuildTime is null ? "Not recorded in this build" : s.BuildTime);
		this.AutoText = s?.AutoDelayMinutes is int m
			? $"Installs new packages {m} minutes after they arrive. Until then you can cancel."
			: "Off. New packages wait until someone upgrades.";
		this.AutoMinutes = (s?.AutoDelayMinutes ?? 0).ToString(CultureInfo.CurrentCulture);

		this.contentSyncing = true;
		this.ContentAuto = s?.ContentAutoInstall == true;
		this.contentSyncing = false;

		this.HasPending = s?.PendingUpdate is not null;
		this.PendingText = s?.PendingUpdate is { } n
			? $"{n.Title}, {RelativeTime.Format(n.Received, DateTime.Now)}" + (n.Subject is null ? string.Empty : $" ({n.Subject})")
			: string.Empty;
	}

	private void AddLog(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return;
		this.Log.Insert(0, new UpdateLogLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture), text));
		this.HasLog = true;
	}

	private async Task RunAsync(UpdateRowViewModel r, string busy, Func<Task> action)
	{
		this.IsBusy = true;
		this.BusyText = busy;
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
			Describe(r);
		}
	}

	// ---- Actions ---------------------------------------------------------------------------

	[RelayCommand]
	private Task Refresh() => this.Selected is { } r ? this.RunAsync(r, "Asking the Neuron…", () => this.ReadAsync(r, quiet: true)) : Task.CompletedTask;

	[RelayCommand]
	private Task Check() => this.Selected is { } r
		? this.RunAsync(r, "Checking for new packages…", async () =>
		{
			IReadOnlyList<string> lines = await this.software.CheckAsync(r.AccountId, r.NeuronJid);
			foreach (string l in lines)
				this.AddLog(l);
			if (lines.Count == 0)
				this.AddLog("The Neuron did not answer.");
			await this.ReadAsync(r, quiet: true);
		})
		: Task.CompletedTask;

	[RelayCommand]
	private Task SaveAuto()
	{
		if (this.Selected is not { } r)
			return Task.CompletedTask;

		if (!int.TryParse(this.AutoMinutes, NumberStyles.Integer, CultureInfo.CurrentCulture, out int minutes) || minutes < 0)
		{
			this.Error = "Give the delay in whole minutes; 0 turns automatic updates off.";
			return Task.CompletedTask;
		}

		return this.RunAsync(r, "Saving…", async () =>
		{
			this.AddLog(await this.software.SetAutoDelayAsync(r.AccountId, r.NeuronJid, minutes));
			await this.ReadAsync(r, quiet: true);
		});
	}

	[RelayCommand]
	private Task CancelPending() => this.Selected is { } r
		? this.RunAsync(r, "Cancelling…", async () =>
		{
			this.AddLog(await this.software.CancelPendingAsync(r.AccountId, r.NeuronJid));
			await this.ReadAsync(r, quiet: true);
		})
		: Task.CompletedTask;

	[RelayCommand]
	private void AskUpgrade()
	{
		this.UpgradeConfirmText = string.Empty;
		this.IsConfirmingUpgrade = true;
	}

	[RelayCommand]
	private void CancelUpgrade() => this.IsConfirmingUpgrade = false;

	private bool CanUpgradeNow() => this.IsConfirmingUpgrade && this.Selected is { } r &&
		string.Equals(this.UpgradeConfirmText.Trim(), r.Label, StringComparison.OrdinalIgnoreCase);

	[RelayCommand(CanExecute = nameof(CanUpgradeNow))]
	private Task UpgradeNow()
	{
		if (this.Selected is not { } r)
			return Task.CompletedTask;

		this.IsConfirmingUpgrade = false;
		string? package = string.IsNullOrWhiteSpace(this.UpgradePackage) ? null : this.UpgradePackage;
		bool backup = this.UpgradeBackup;
		this.AddLog(backup ? "Upgrading, with a backup first. The Neuron restarts when it is done." : "Upgrading without a backup. The Neuron restarts when it is done.");

		return this.RunAsync(r, "Upgrading…", async () =>
		{
			foreach (string l in await this.software.UpgradeAsync(r.AccountId, r.NeuronJid, package, backup))
				this.AddLog(l);
		});
	}
}
