using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Neuro.AA.App.Converters;

/// <summary>Binds radio buttons to an integer choice: checked when the value equals the index.</summary>
public static class IndexConverters
{
	public static readonly IValueConverter Is0 = new Index(0);
	public static readonly IValueConverter Is1 = new Index(1);
	public static readonly IValueConverter Is2 = new Index(2);

	private sealed class Index(int i) : IValueConverter
	{
		public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is int v && v == i;

		public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
			=> value is true ? i : BindingOperations.DoNothing;
	}
}
