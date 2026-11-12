using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.ViewModels;

/// <summary>Something on a Neuron that waits for an answer, shown on its page.</summary>
public sealed record PageWaiting(string Key, string IconKey, string Title, string When);

/// <summary>
/// One Neuron's page: who it is, how it is doing, and what the Overview tab shows. The other
/// tabs are the screens themselves, pointed at this Neuron by the shell.
/// </summary>
public sealed partial class NeuronPageViewModel(Guid accountId, string bareJid) : ObservableObject
{
	public Guid AccountId { get; } = accountId;
	public string BareJid { get; } = bareJid;

	[ObservableProperty] private string label = bareJid;
	[ObservableProperty] private string accountLabel = string.Empty;

	/// <summary>Whether the account is worth naming: only when there are several.</summary>
	[ObservableProperty] private bool showAccount;
	[ObservableProperty] private int accountColorIndex;
	[ObservableProperty] private bool isOk;
	[ObservableProperty] private bool isBusy;
	[ObservableProperty] private bool isBad;
	[ObservableProperty] private bool isLive;
	[ObservableProperty] private string stateText = string.Empty;
	[ObservableProperty] private string software = "–";
	[ObservableProperty] private string? note;

	[ObservableProperty] private string cpuText = "–";
	[ObservableProperty] private int[] cpuSeries = [];
	[ObservableProperty] private bool highCpu;
	[ObservableProperty] private string memoryUsedText = "–";
	[ObservableProperty] private string memoryFreeText = string.Empty;
	[ObservableProperty] private int[] memorySeries = [];
	[ObservableProperty] private bool lowMemory;
	[ObservableProperty] private string diskText = "–";
	[ObservableProperty] private int[] diskSeries = [];
	[ObservableProperty] private bool lowDisk;
	[ObservableProperty] private string healthAge = string.Empty;

	[ObservableProperty] private int errorsLastHour;
	[ObservableProperty] private int warningsLastHour;
	[ObservableProperty] private int openIssues;
	[ObservableProperty] private bool hasErrors;
	[ObservableProperty] private string lastEventText = "–";
	[ObservableProperty] private string problemsText = string.Empty;

	/// <summary>True when the account that reaches this Neuron lives on it, so its publish/subscribe service is in reach.</summary>
	[ObservableProperty] private bool hostsAccount;

	/// <summary>What waits for an answer on this Neuron.</summary>
	public ObservableCollection<PageWaiting> Waiting { get; } = [];
	[ObservableProperty] private bool hasWaiting;

	/// <summary>What the Neuron is connected to, from the last look at the map.</summary>
	public ObservableCollection<TopologyConnection> Links { get; } = [];
	[ObservableProperty] private bool hasLinks;
	[ObservableProperty] private string linksNote = string.Empty;

	private static string Mb(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";

	internal void Apply(FleetRowViewModel row)
	{
		this.Label = row.Label;
		this.AccountLabel = row.AccountLabel;
		this.ShowAccount = row.ShowAccount;
		this.AccountColorIndex = row.AccountColorIndex;
		this.IsLive = row.State == WatchState.Live;
		this.IsOk = this.IsLive;
		this.IsBusy = row.State is WatchState.Waiting or WatchState.Discovering or WatchState.Subscribing;
		this.IsBad = row.State is WatchState.Forbidden or WatchState.Offline or WatchState.Failed;
		this.StateText = row.StateText;
		this.Software = row.Software;
		this.Note = row.Note;
		this.ErrorsLastHour = row.ErrorsLastHour;
		this.WarningsLastHour = row.WarningsLastHour;
		this.OpenIssues = row.OpenIssues;
		this.HasErrors = row.HasErrors;
		this.LastEventText = row.LastEventText;
		this.ProblemsText = (row.ErrorsLastHour, row.WarningsLastHour) switch
		{
			(0, 0) => "Nothing went wrong in the last hour",
			(int e, 0) => $"{Ui.Count(e, "error")} in the last hour",
			(0, int w) => $"{Ui.Count(w, "warning")} in the last hour",
			(int e, int w) => $"{Ui.Count(e, "error")} and {Ui.Count(w, "warning")} in the last hour",
		};
	}

	internal void Apply(NeuronHealth? h, DateTime now)
	{
		if (h is null)
		{
			this.HealthAge = string.Empty;
			return;
		}

		if (h.Cpu.Latest is double cpu)
		{
			this.CpuText = cpu.ToString("0", CultureInfo.CurrentCulture) + " %";
			this.CpuSeries = [.. h.Cpu.Series.Select(v => (int)Math.Round(v))];
		}
		else
		{
			this.CpuText = "–";
			this.CpuSeries = [];
		}

		this.MemoryUsedText = h.AllocatedMemory.Latest is double a ? Mb(a) + " used" : h.FreeMemory.Latest is double f0 ? Mb(f0) + " free" : "–";
		this.MemoryFreeText = h.AllocatedMemory.Latest is not null && h.FreeMemory.Latest is double f ? Mb(f) + " free" : string.Empty;
		this.MemorySeries = [.. h.AllocatedMemory.Series.Select(v => (int)Math.Round(v))];
		this.DiskText = h.FreeDisk.Latest is double d ? $"{d:0.0} GB free" : "–";
		this.DiskSeries = [.. h.FreeDisk.Series.Select(v => (int)Math.Round(v * 10))];

		this.HighCpu = h.Cpu.Latest >= 80;
		this.LowMemory = h.FreeMemory.Latest is double fm && fm < 1024;
		this.LowDisk = h.FreeDisk.Latest is double fd && fd < 2;
		this.HealthAge = h.ReadAt == default ? string.Empty : "Health read " + (RelativeTime.Format(h.ReadAt, now) is string ago && ago != "now" ? ago : "just now");
	}
}
