using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Neuro.AA.App.Watch;

/// <summary>
/// The app's icon, drawn rather than shipped: an accent tile with "N", plus a red dot when
/// something needs you, or greyed out while alerts are snoozed. Used by the window and the tray.
/// </summary>
public static class AppIcon
{
	private static readonly Dictionary<(bool, bool), WindowIcon> cache = [];

	public static WindowIcon Get(bool attention = false, bool snoozed = false)
	{
		if (cache.TryGetValue((attention, snoozed), out WindowIcon? icon))
			return icon;

		const int size = 64;
		RenderTargetBitmap bitmap = new(new PixelSize(size, size), new Vector(96, 96));
		using (DrawingContext dc = bitmap.CreateDrawingContext())
		{
			Color tile = snoozed ? Color.FromRgb(0x8A, 0x92, 0x9E) : Color.FromRgb(0x33, 0x58, 0xD4);
			dc.DrawRectangle(new SolidColorBrush(tile), null, new Rect(2, 2, size - 4, size - 4), 14, 14);

			FormattedText n = new("N", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
				new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 40, Brushes.White);
			dc.DrawText(n, new Point((size - n.Width) / 2, (size - n.Height) / 2));

			if (attention)
			{
				dc.DrawEllipse(Brushes.White, null, new Point(size - 14, 14), 13, 13);
				dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xC8, 0x31, 0x2B)), null, new Point(size - 14, 14), 10, 10);
			}
		}

		using MemoryStream ms = new();
		bitmap.Save(ms);
		ms.Position = 0;
		icon = new WindowIcon(ms);
		cache[(attention, snoozed)] = icon;
		return icon;
	}
}
