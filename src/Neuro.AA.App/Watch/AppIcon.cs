using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Neuro.AA.App.Watch;

/// <summary>
/// The app's icon, drawn rather than shipped: an accent tile with "N", plus a red badge with the
/// number of things that wait for you, or greyed out while pop-ups and sounds are held back.
/// Used by the window, the taskbar and the tray.
/// </summary>
public static class AppIcon
{
	private static readonly Dictionary<(int, bool), WindowIcon> cache = [];

	/// <param name="count">How many things wait for you. Ten or more shows as "9+".</param>
	/// <param name="quiet">Do not disturb is on.</param>
	public static WindowIcon Get(int count = 0, bool quiet = false)
	{
		count = Math.Clamp(count, 0, 10);
		if (cache.TryGetValue((count, quiet), out WindowIcon? icon))
			return icon;

		const int size = 64;
		RenderTargetBitmap bitmap = new(new PixelSize(size, size), new Vector(96, 96));
		using (DrawingContext dc = bitmap.CreateDrawingContext())
		{
			Color tile = quiet ? Color.FromRgb(0x8A, 0x92, 0x9E) : Color.FromRgb(0x33, 0x58, 0xD4);
			dc.DrawRectangle(new SolidColorBrush(tile), null, new Rect(2, 2, size - 4, size - 4), 14, 14);

			FormattedText n = new("N", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
				new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 40, Brushes.White);
			dc.DrawText(n, new Point((size - n.Width) / 2, (size - n.Height) / 2));

			if (count > 0)
			{
				// The badge is large on purpose: the tray shows this at 16 pixels.
				Point centre = new(size - 20, 20);
				dc.DrawEllipse(Brushes.White, null, centre, 20, 20);
				dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xC8, 0x31, 0x2B)), null, centre, 17, 17);

				string label = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
				FormattedText t = new(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
					new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), label.Length > 1 ? 21 : 27, Brushes.White);
				dc.DrawText(t, new Point(centre.X - (t.Width / 2), centre.Y - (t.Height / 2)));
			}
		}

		using MemoryStream ms = new();
		bitmap.Save(ms);
		ms.Position = 0;
		icon = new WindowIcon(ms);
		cache[(count, quiet)] = icon;
		return icon;
	}
}
