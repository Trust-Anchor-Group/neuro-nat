using System.Globalization;
using Avalonia.Data.Converters;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.Converters;

/// <summary>
/// Shows a Neuron's address as its name on screen (<see cref="NeuronNames"/>): the roster name,
/// else the domain, or the full address when two Neurons would look the same. The names are
/// kept current by the Neurons list.
/// </summary>
public sealed class NeuronNameConverter : IValueConverter
{
	public static readonly NeuronNameConverter Instance = new();

	private static IReadOnlyDictionary<string, string> names = new Dictionary<string, string>();

	/// <summary>Takes the names of the Neurons known now, by bare JID.</summary>
	public static void Update(IReadOnlyDictionary<string, string> current) => Volatile.Write(ref names, current);

	/// <summary>The name of a Neuron by its address.</summary>
	public static string Name(string? jid)
	{
		if (string.IsNullOrEmpty(jid))
			return string.Empty;

		string bare = jid.Split('/')[0];
		return Volatile.Read(ref names).TryGetValue(bare, out string? name) ? name : NeuronNames.Domain(bare);
	}

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string jid ? Name(jid) : value;

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
