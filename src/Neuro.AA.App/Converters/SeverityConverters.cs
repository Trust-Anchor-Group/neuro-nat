using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.Converters;

/// <summary>Severity to its token brush (Severity.Informational, Warning, Error, Critical).</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
	public static readonly SeverityBrushConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		string key = value switch
		{
			EventSeverity.Warning => "Severity.Warning",
			EventSeverity.Error => "Severity.Error",
			EventSeverity.Critical => "Severity.Critical",
			_ => "Severity.Informational",
		};

		return BrushCache.Get(key, parameter is "subtle");
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}

/// <summary>Severity to a short label.</summary>
public sealed class SeverityTextConverter : IValueConverter
{
	public static readonly SeverityTextConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
	{
		EventSeverity.Warning => "Warning",
		EventSeverity.Error => "Error",
		EventSeverity.Critical => "Critical",
		_ => "Info",
	};

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}

/// <summary>Watch state to a short label and whether it is healthy.</summary>
public sealed class WatchStateTextConverter : IValueConverter
{
	public static readonly WatchStateTextConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
	{
		WatchState.Live => "Live",
		WatchState.Waiting => "Waiting for presence",
		WatchState.Discovering => "Checking",
		WatchState.Subscribing => "Starting stream",
		WatchState.Forbidden => "Not admin",
		WatchState.Offline => "Offline",
		WatchState.Failed => "Failed",
		_ => string.Empty,
	};

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}

/// <summary>Issue state to the action label shown in the issue header.</summary>
public static class EventConverters
{
	public static readonly IValueConverter IsLive = new FuncValueConverter<WatchState, bool>(s => s == WatchState.Live);
	public static readonly IValueConverter IsProblem = new FuncValueConverter<WatchState, bool>(s => s is WatchState.Forbidden or WatchState.Failed);
	public static readonly IValueConverter IsPending = new FuncValueConverter<WatchState, bool>(s => s is WatchState.Waiting or WatchState.Discovering or WatchState.Subscribing);
	public static readonly IValueConverter IsResolvedOrMuted = new FuncValueConverter<IssueState, bool>(s => s is IssueState.Resolved or IssueState.Muted);
	public static readonly IValueConverter IsNotOpen = new FuncValueConverter<IssueState, bool>(s => s != IssueState.Open);
	public static readonly IValueConverter IsOpenOrAck = new FuncValueConverter<IssueState, bool>(s => s is IssueState.Open or IssueState.Acknowledged);
	public static readonly IValueConverter StateText = new FuncValueConverter<IssueState, string>(s => s switch
	{
		IssueState.Acknowledged => "Acknowledged",
		IssueState.Muted => "Muted",
		IssueState.Resolved => "Resolved",
		_ => string.Empty,
	});
	public static readonly IValueConverter Time = new FuncValueConverter<DateTime, string>(t => t.Date == DateTime.Today ? t.ToString("HH:mm:ss") : t.ToString("yyyy-MM-dd HH:mm:ss"));
	public static readonly IValueConverter HasText = new FuncValueConverter<string?, bool>(s => !string.IsNullOrWhiteSpace(s));
	public static readonly IValueConverter HasItems = new FuncValueConverter<int, bool>(n => n > 0);
}

/// <summary>A state tone (ok, busy, bad, idle) to its State.* brush; "subtle" gives a tinted background.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
	public static readonly ToneBrushConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		string key = value switch
		{
			"ok" => "State.Ok",
			"busy" => "State.Busy",
			"bad" => "State.Bad",
			_ => "State.Idle",
		};

		return BrushCache.Get(key, parameter is "subtle");
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}

/// <summary>
/// Theme brushes by key, and their tinted variants, made once per theme instead of once per
/// binding: list rows bind several of them, and new brushes on every row made scrolling slow.
/// </summary>
internal static class BrushCache
{
	private static readonly Dictionary<(string Key, object? Theme, bool Subtle), IBrush> cache = [];

	public static IBrush Get(string key, bool subtle)
	{
		Application? app = Application.Current;
		object? theme = app?.ActualThemeVariant;
		if (cache.TryGetValue((key, theme, subtle), out IBrush? b))
			return b;

		IBrush brush = Brushes.Gray;
		if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out object? res) && res is ISolidColorBrush s)
			brush = subtle ? new ImmutableSolidColorBrush(s.Color, 0.14) : s;

		cache[(key, theme, subtle)] = brush;
		return brush;
	}
}
