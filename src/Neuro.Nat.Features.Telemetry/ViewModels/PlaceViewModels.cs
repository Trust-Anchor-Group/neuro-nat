using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.Nat.App.Features;
using Neuro.Nat.Features.Telemetry.Data;

namespace Neuro.Nat.Features.Telemetry.ViewModels;

/// <summary>A collector in the picker at the top of the rail's dashboard.</summary>
public sealed record CollectorChoice(DashboardViewModel Dashboard, string? Account)
{
	public string Name => this.Dashboard.Name;
	public string Key => this.Dashboard.Collector.Key;
	public string Label
	{
		get
		{
			string address = this.Dashboard.Collector.Domain;
			string target = string.Equals(this.Name, address, StringComparison.OrdinalIgnoreCase) ? address : $"{this.Name} ({address})";
			return this.Account is null ? target : $"{target} via {this.Account}";
		}
	}
}

/// <summary>
/// App telemetry in the rail: one collector's dashboard at a time, and a picker at the top when
/// there are several. Each collector keeps its own dashboard; their numbers are never mixed.
/// </summary>
public sealed partial class TelemetryPlaceViewModel : ObservableObject, IFeatureContent
{
	private readonly Func<IReadOnlyList<CollectorChoice>> choices;

	public TelemetryPlaceViewModel(Func<IReadOnlyList<CollectorChoice>> choices)
	{
		this.choices = choices;
		this.Sync();
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasPicker))]
	private IReadOnlyList<CollectorChoice> collectors = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Current))]
	private CollectorChoice? selected;

	public DashboardViewModel? Current => this.Selected?.Dashboard;
	public bool HasPicker => this.Collectors.Count > 1;

	/// <summary>Brings the picker up to date with the collectors found now.</summary>
	public void Sync()
	{
		IReadOnlyList<CollectorChoice> now = this.choices();
		if (now.SequenceEqual(this.Collectors))
			return;

		string? key = this.Selected?.Key;
		this.Collectors = now;
		this.Selected = now.FirstOrDefault(c => c.Key == key) ?? now.FirstOrDefault();
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
			if (c is null)
				return;
			this.Selected = c;
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
