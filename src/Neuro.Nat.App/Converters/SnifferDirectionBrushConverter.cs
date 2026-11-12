using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Neuro.Nat.Engine.Sniffing;

namespace Neuro.Nat.App.Converters;

/// <summary>Colours sniffer lines by direction using the app's state tokens.</summary>
public sealed class SnifferDirectionBrushConverter : IValueConverter
{
	public static readonly SnifferDirectionBrushConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		string key = value switch
		{
			SnifferDirection.Transmitted => "Accent",
			SnifferDirection.Received => "Ink.Primary",
			SnifferDirection.Warning => "State.Busy",
			SnifferDirection.Error or SnifferDirection.Exception => "State.Bad",
			_ => "Ink.Secondary",
		};

		Application? app = Application.Current;
		if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out object? brush) && brush is IBrush b)
			return b;

		return Brushes.Gray;
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}

/// <summary>Short marker for a sniffer line's direction.</summary>
public sealed class SnifferDirectionGlyphConverter : IValueConverter
{
	public static readonly SnifferDirectionGlyphConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
	{
		SnifferDirection.Transmitted => "TX",
		SnifferDirection.Received => "RX",
		SnifferDirection.Information => "INF",
		SnifferDirection.Warning => "WRN",
		SnifferDirection.Error => "ERR",
		SnifferDirection.Exception => "EXC",
		_ => string.Empty,
	};

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
