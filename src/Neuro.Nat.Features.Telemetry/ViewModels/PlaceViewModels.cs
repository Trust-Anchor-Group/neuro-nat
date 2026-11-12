using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.App.Features;
using Neuro.Nat.Features.Telemetry.Data;

namespace Neuro.Nat.Features.Telemetry.ViewModels;

/// <summary>A collector in the picker at the top of the rail's dashboard.</summary>
public sealed partial class CollectorChoice(DashboardViewModel dashboard) : ObservableObject
{
	public DashboardViewModel Dashboard { get; } = dashboard;
	public string Name => this.Dashboard.Name;
	public string Key => this.Dashboard.Collector.Key;

	[ObservableProperty] private bool isCurrent;
}

/// <summary>
/// App telemetry in the rail: one collector's dashboard at a time, and a picker at the top when
/// there are several. Each collector keeps its own dashboard; their numbers are never mixed.
/// </summary>
public sealed partial class TelemetryPlaceViewModel : ObservableObject, IFeatureContent
{
	private readonly Func<IReadOnlyList<DashboardViewModel>> dashboards;

	public TelemetryPlaceViewModel(Func<IReadOnlyList<DashboardViewModel>> dashboards)
	{
		this.dashboards = dashboards;
		this.Sync();
	}

	public ObservableCollection<CollectorChoice> Collectors { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasPicker))]
	private DashboardViewModel? current;

	public bool HasPicker => this.Collectors.Count > 1;

	/// <summary>Brings the picker up to date with the collectors found now.</summary>
	public void Sync()
	{
		List<DashboardViewModel> now = [.. this.dashboards()];
		if (!now.SequenceEqual(this.Collectors.Select(c => c.Dashboard)))
		{
			this.Collectors.Clear();
			foreach (DashboardViewModel d in now)
				this.Collectors.Add(new CollectorChoice(d));
		}

		if (this.Current is null || !now.Contains(this.Current))
			this.Current = now.FirstOrDefault();
		foreach (CollectorChoice c in this.Collectors)
			c.IsCurrent = c.Dashboard == this.Current;
		this.OnPropertyChanged(nameof(this.HasPicker));
	}

	[RelayCommand]
	private void Pick(string? key)
	{
		CollectorChoice? c = this.Collectors.FirstOrDefault(x => x.Key == key);
		if (c is null)
			return;

		this.Current = c.Dashboard;
		foreach (CollectorChoice x in this.Collectors)
			x.IsCurrent = x == c;
		c.Dashboard.Shown();
	}

	/// <summary>"lab.example.com/nfc" picks that collector and shows the page; "nfc" shows it on the one shown.</summary>
	public void ShowPart(string part)
	{
		this.Sync();
		int slash = part.IndexOf('/', StringComparison.Ordinal);
		if (slash > 0 && !part.StartsWith("metric:", StringComparison.Ordinal))
		{
			string domain = part[..slash];
			CollectorChoice? c = this.Collectors.FirstOrDefault(x => string.Equals(x.Dashboard.Collector.Domain, domain, StringComparison.OrdinalIgnoreCase));
			if (c is not null)
				this.Pick(c.Key);
			part = part[(slash + 1)..];
		}

		this.Current?.ShowPart(part);
	}
}

/// <summary>One collector's key numbers on the big screen.</summary>
public sealed record AppNumbersRow(string Name, string Freshness, IReadOnlyList<KeyNumber> Numbers, bool ShowName)
{
	/// <summary>One collector: three large tiles a row with their trend. Several: all six in one row each, without it.</summary>
	public int Columns => this.ShowName ? 6 : 3;

	public bool ShowTrend => !this.ShowName;
}

/// <summary>The big screen's App numbers page: the key numbers of each collector, each in its own row.</summary>
public sealed partial class AppNumbersViewModel : ObservableObject
{
	public ObservableCollection<AppNumbersRow> Rows { get; } = [];

	public void Set(IReadOnlyList<AppNumbersRow> rows)
	{
		if (rows.SequenceEqual(this.Rows))
			return;

		this.Rows.Clear();
		foreach (AppNumbersRow r in rows)
			this.Rows.Add(r);
	}
}
