using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Neuro.Nat.App.Converters;

/// <summary>The name of an icon ("Chat", "Bell") to its drawing, from the app's resources.</summary>
public sealed class IconConverter : IValueConverter
{
	public static readonly IconConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> Get(value as string);

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();

	public static Geometry? Get(string? name)
		=> name is not null && Application.Current is Application app && app.TryFindResource("Icon." + name, out object? g) ? g as Geometry : null;
}
