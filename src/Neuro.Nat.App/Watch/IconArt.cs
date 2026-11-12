using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Neuro.Nat.App.Watch;

/// <summary>What the dot in the middle of the icon says.</summary>
public enum IconState
{
	/// <summary>All quiet: white.</summary>
	Quiet,

	/// <summary>Something waits for you: amber.</summary>
	NeedsYou,

	/// <summary>A Neuron is down: red.</summary>
	Down,

	/// <summary>No account is connected: grey.</summary>
	NotConnected,
}

/// <summary>
/// The NAT icon, in one place: the company logo's ring around a dot, on a dark tile. The same
/// drawing makes the program's icon file (Assets\NAT.ico in the Desktop project) and the live icons
/// of the tray and the windows, whose dot shows the state of the Neurons.
/// Geometry is in a 1000 x 1000 box.
/// </summary>
public static class IconArt
{
	/// <summary>The sizes in the program's icon file.</summary>
	public static readonly int[] FileSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

	/// <summary>The sizes in the live icons: what Windows asks for in the tray, title bars and the taskbar.</summary>
	public static readonly int[] LiveSizes = [16, 20, 24, 32, 40, 48, 64];

	/// <summary>Up to this size the ring's pieces blur, so four plain arcs are drawn instead.</summary>
	public const int SmallUpTo = 24;

	public static readonly Color Tile = Color.FromRgb(0x16, 0x1A, 0x24);
	public static readonly Color Blue = Color.FromRgb(0x4A, 0x97, 0xF2);
	public static readonly Color Green = Color.FromRgb(0x2B, 0xBF, 0x85);
	public static readonly Color Purple = Color.FromRgb(0x8B, 0x40, 0xD0);
	public static readonly Color Yellow = Color.FromRgb(0xFB, 0xB9, 0x00);

	public static readonly Color Quiet = Colors.White;
	public static readonly Color Amber = Color.FromRgb(0xFF, 0xB0, 0x20);
	public static readonly Color Red = Color.FromRgb(0xF0, 0x46, 0x46);
	public static readonly Color Grey = Color.FromRgb(0x78, 0x7E, 0x8A);

	private const double Centre = 500;
	private const double RingScale = 0.7;

	/// <summary>
	/// The state the dot shows. No account connected: grey, since nothing else can be known then.
	/// Otherwise red when a Neuron is down, amber when something waits for you, white when all is quiet.
	/// </summary>
	public static IconState StateFor(bool anyConnected, int neuronsDown, int waiting)
		=> !anyConnected ? IconState.NotConnected
			: neuronsDown > 0 ? IconState.Down
			: waiting > 0 ? IconState.NeedsYou
			: IconState.Quiet;

	public static Color DotColor(IconState state) => state switch
	{
		IconState.NeedsYou => Amber,
		IconState.Down => Red,
		IconState.NotConnected => Grey,
		_ => Quiet,
	};

	// The logo's ring: closed smooth curves (uniform Catmull-Rom) through these points, one piece per line.
	private static readonly (Color Colour, string Points)[] pieces =
	[
		(Blue, "605,699 583,713 566,734 557,758 558,784 564,810 569,836 575,861 581,887 586,913 591,938 600,963 620,978 646,977 671,968 695,958 719,946 731,924 727,899 714,876 701,853 688,830 675,807 662,785 649,762 635,739 622,716"),
		(Blue, "917,485 891,486 865,487 839,488 813,491 788,498 766,512 746,529 729,549 715,571 702,593 690,616 675,638 658,658 646,680 657,703 672,723 689,744 704,764 721,785 737,805 753,826 769,846 785,866 809,877 833,869 852,851 870,833 887,813 903,792 918,770 931,748 943,725 955,702 964,677 973,653 981,628 986,602 991,577 994,551 997,525 990,500 969,486 943,485"),
		(Green, "918,280 894,289 870,302 847,314 824,327 801,339 777,351 754,364 731,376 708,388 705,410 722,430 744,445 770,450 796,447 822,442 848,438 874,433 900,429 926,424 951,419 974,406 982,382 976,356 968,331 958,307 943,286"),
		(Green, "532,1 509,13 498,36 498,63 498,89 498,115 498,141 498,167 501,193 510,217 524,239 542,259 562,275 583,290 606,302 628,316 648,333 667,351 691,357 713,343 734,328 755,312 776,297 797,282 818,267 839,251 860,236 879,218 885,193 873,170 856,151 837,133 817,116 797,99 776,85 754,70 731,58 707,46 683,36 659,27 634,19 609,13 583,7 557,4"),
		(Purple, "468,725 445,736 433,759 427,785 420,810 414,836 408,861 401,887 395,912 388,938 386,964 400,986 425,994 451,998 477,999 503,998 523,982 529,956 527,930 525,904 524,878 522,851 520,825 518,799 516,773 512,747 493,729"),
		(Purple, "197,578 171,582 146,588 121,594 95,600 70,607 45,614 26,631 24,657 32,681 42,705 53,729 66,752 80,774 94,795 110,815 127,835 145,854 164,872 184,889 205,905 226,919 248,933 271,945 296,950 318,937 330,913 340,889 350,865 359,841 369,817 378,792 380,767 378,741 370,716 359,692 345,670 329,650 312,631 292,614 271,599 248,587 223,580"),
		(Yellow, "39,404 15,413 2,436 0,462 0,488 0,514 10,537 34,547 60,546 86,542 112,540 138,538 164,534 191,532 217,528 241,518 259,500 271,476 270,453 244,446 219,441 193,435 167,430 142,424 116,419 90,413 64,408"),
		(Yellow, "107,209 84,221 69,242 57,264 45,287 41,312 53,334 76,346 100,354 124,364 148,372 173,381 197,390 221,399 245,408 269,416 292,406 304,384 311,360 294,341 274,325 253,309 233,293 213,277 192,262 172,246 152,230 131,215"),
		(Yellow, "254,66 231,77 209,91 189,107 171,125 168,150 182,172 199,191 218,209 235,228 253,246 271,265 289,283 307,302 325,320 348,328 370,314 389,297 386,273 375,250 364,227 353,203 342,180 330,157 319,134 308,111 296,88 279,69"),
		(Yellow, "398,11 373,17 356,37 356,62 364,87 372,112 380,136 387,161 396,185 404,210 412,235 420,260 431,282 453,271 462,247 459,221 456,195 452,170 449,144 446,118 442,92 439,67 435,41 422,19"),
	];

	// The small variant: four arcs, degrees clockwise from 3 o'clock (y points down).
	private static readonly (Color Colour, double From, double To)[] arcs =
	[
		(Blue, 6, 78),
		(Purple, 96, 168),
		(Yellow, 186, 258),
		(Green, 276, 348),
	];

	private static Geometry[]? pieceGeometry;

	/// <summary>Draws the icon into a <paramref name="size"/> x <paramref name="size"/> square at the origin.</summary>
	/// <param name="small">Draw the four arcs instead of the ring's pieces (for 16 to 24 pixels).</param>
	/// <param name="tray">The tray's larger dot (only with <paramref name="small"/>).</param>
	/// <param name="ringOnly">Leave out the tile and the dot: the ring alone, for checking the pieces.</param>
	public static void Draw(DrawingContext dc, double size, Color dot, bool small, bool tray = false, bool ringOnly = false)
	{
		using (dc.PushTransform(Matrix.CreateScale(size / 1000, size / 1000)))
		{
			if (!ringOnly)
				dc.DrawRectangle(new SolidColorBrush(Tile), null, new Rect(30, 30, 940, 940), 220, 220);

			if (small)
			{
				foreach ((Color colour, double from, double to) in arcs)
				{
					StreamGeometry g = new();
					using (StreamGeometryContext c = g.Open())
					{
						c.BeginFigure(OnCircle(275, from), false);
						c.ArcTo(OnCircle(275, to), new Size(275, 275), 0, false, SweepDirection.Clockwise);
						c.EndFigure(false);
					}
					dc.DrawGeometry(null, new Pen(new SolidColorBrush(colour), 150, lineCap: PenLineCap.Flat), g);
				}
			}
			else
			{
				Geometry[] geometry = pieceGeometry ??= [.. pieces.Select(p => Piece(p.Points))];
				for (int i = 0; i < pieces.Length; i++)
					dc.DrawGeometry(new SolidColorBrush(pieces[i].Colour), null, geometry[i]);
			}

			if (!ringOnly)
			{
				double r = small ? (tray ? 120 : 100) : 85;
				dc.DrawEllipse(new SolidColorBrush(dot), null, new Point(Centre, Centre), r, r);
			}
		}
	}

	/// <summary>Draws the icon at one size into a PNG.</summary>
	public static byte[] Png(int size, Color dot, bool tray = false, bool ringOnly = false, bool? small = null)
	{
		using RenderTargetBitmap bitmap = new(new PixelSize(size, size), new Vector(96, 96));
		using (DrawingContext dc = bitmap.CreateDrawingContext())
			Draw(dc, size, dot, small ?? size <= SmallUpTo, tray, ringOnly);

		using MemoryStream ms = new();
		bitmap.Save(ms);
		return ms.ToArray();
	}

	/// <summary>An .ico file with the icon at each of <paramref name="sizes"/>, PNG-compressed (Windows Vista and later read these at every size).</summary>
	public static byte[] Ico(IReadOnlyList<int> sizes, Color dot, bool tray = false)
	{
		byte[][] images = [.. sizes.Select(s => Png(s, dot, tray))];

		using MemoryStream ico = new();
		using (BinaryWriter w = new(ico, System.Text.Encoding.UTF8, leaveOpen: true))
		{
			w.Write((ushort)0);
			w.Write((ushort)1);
			w.Write((ushort)sizes.Count);
			int offset = 6 + (16 * sizes.Count);
			for (int i = 0; i < sizes.Count; i++)
			{
				byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
				w.Write(dim);
				w.Write(dim);
				w.Write((byte)0);
				w.Write((byte)0);
				w.Write((ushort)1);
				w.Write((ushort)32);
				w.Write((uint)images[i].Length);
				w.Write((uint)offset);
				offset += images[i].Length;
			}

			foreach (byte[] image in images)
				w.Write(image);
		}

		return ico.ToArray();
	}

	private static Point OnCircle(double radius, double degrees)
	{
		double a = degrees * Math.PI / 180;
		return new Point(Centre + (radius * Math.Cos(a)), Centre + (radius * Math.Sin(a)));
	}

	/// <summary>A closed uniform Catmull-Rom spline through the points, as cubic Beziers, scaled to the ring's size around the centre.</summary>
	private static StreamGeometry Piece(string points)
	{
		Point[] p = [.. points.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(xy =>
		{
			string[] v = xy.Split(',');
			double x = double.Parse(v[0], CultureInfo.InvariantCulture);
			double y = double.Parse(v[1], CultureInfo.InvariantCulture);
			return new Point(Centre + ((x - Centre) * RingScale), Centre + ((y - Centre) * RingScale));
		})];

		int n = p.Length;
		StreamGeometry g = new();
		using (StreamGeometryContext c = g.Open())
		{
			c.BeginFigure(p[0], true);
			for (int i = 0; i < n; i++)
			{
				Point p0 = p[(i - 1 + n) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n];
				c.CubicBezierTo(p1 + ((p2 - p0) / 6), p2 - ((p3 - p1) / 6), p2);
			}
			c.EndFigure(true);
		}

		return g;
	}
}
