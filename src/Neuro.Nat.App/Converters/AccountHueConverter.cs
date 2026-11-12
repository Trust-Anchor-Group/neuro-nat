using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Neuro.Nat.App.Converters;

/// <summary>
/// Maps an account's colour index to its hue brush (Account.Hue0 to Account.Hue7) for the
/// current theme. Pass "subtle" as the parameter for a 16% tint used behind avatars.
/// </summary>
public sealed class AccountHueConverter : IValueConverter
{
	public static readonly AccountHueConverter Instance = new();
	private const int HueCount = 8;

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		int index = value is int i ? ((i % HueCount) + HueCount) % HueCount : 0;
		Application? app = Application.Current;

		if (app is null || !app.TryGetResource("Account.Hue" + index, app.ActualThemeVariant, out object? res) || res is not ISolidColorBrush brush)
			return Brushes.Gray;

		if (parameter is "subtle")
			return new SolidColorBrush(brush.Color, 0.16);

		return brush;
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}

/// <summary>First letter of an account label, for the avatar.</summary>
public sealed class InitialConverter : IValueConverter
{
	public static readonly InitialConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> value is string s && s.Length > 0 ? char.ToUpperInvariant(s[0]).ToString() : "?";

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
