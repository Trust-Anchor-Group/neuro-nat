using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Neuro.AA.App.Controls;

/// <summary>
/// Motion: short, and only where it says what changed. A screen fades in, a note and a pop-up
/// rise into place, a row that just arrived fades in. It is off when the system asks for less
/// motion, when Settings says "Less motion", and in tests and previews.
/// </summary>
public static class Motion
{
	/// <summary>
	/// True when things may move. The app turns it on at start when the system allows and
	/// Settings does not say "Less motion"; nothing else does, so tests and previews stand still.
	/// </summary>
	public static bool Enabled { get; private set; }

	/// <summary>Raised when <see cref="Enabled"/> changed.</summary>
	public static event EventHandler? Changed;

	/// <summary>Turns motion on or off for every window.</summary>
	public static void Set(bool enabled)
	{
		if (Enabled == enabled)
			return;

		Enabled = enabled;
		ApplyStyles();
		Changed?.Invoke(null, EventArgs.Empty);
	}

	private static readonly Uri stylesAt = new("avares://Neuro.AA.App/Themes/Motion.axaml");
	private static Avalonia.Styling.IStyle? styles;

	/// <summary>
	/// Every animation is a style in Themes/Motion.axaml. The file is part of the app's styles
	/// only while motion is on, so with it off nothing can move, in any window or menu.
	/// </summary>
	private static void ApplyStyles()
	{
		if (Application.Current is not Application app)
			return;

		if (Enabled && styles is null)
		{
			styles = new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Neuro.AA.App/")) { Source = stylesAt };
			app.Styles.Add(styles);
		}
		else if (!Enabled && styles is not null)
		{
			app.Styles.Remove(styles);
			styles = null;
		}
	}

	/// <summary>
	/// What the system says: false when the person has turned animations off (on Windows:
	/// Settings, Accessibility, Visual effects, Animation effects). Systems that cannot be
	/// asked are taken to allow it.
	/// </summary>
	public static bool SystemAllows()
	{
		try
		{
			if (OperatingSystem.IsWindows() && SystemParametersInfo(GetClientAreaAnimation, 0, out int on, 0))
				return on != 0;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
		}

		return true;
	}

	private const uint GetClientAreaAnimation = 0x1042;

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint update);

	// ----- Rows that just arrived fade in -----

	/// <summary>
	/// On a list: a row that arrives while you watch fades in, so the eye catches that something
	/// came. Rows that were there when the list was filled do not.
	/// </summary>
	public static readonly AttachedProperty<bool> NewRowsProperty = AvaloniaProperty.RegisterAttached<ItemsControl, bool>("NewRows", typeof(Motion));

	public static bool GetNewRows(ItemsControl list) => list.GetValue(NewRowsProperty);
	public static void SetNewRows(ItemsControl list, bool value) => list.SetValue(NewRowsProperty, value);

	/// <summary>How long a new row carries the class "fresh": a little longer than its animation.</summary>
	private static readonly TimeSpan freshFor = TimeSpan.FromMilliseconds(700);

	static Motion()
	{
		NewRowsProperty.Changed.AddClassHandler<ItemsControl>((list, e) =>
		{
			if (e.NewValue is true)
				list.ItemsView.CollectionChanged += (_, change) => RowsChanged(list, change);
		});
	}

	private static void RowsChanged(ItemsControl list, NotifyCollectionChangedEventArgs e)
	{
		// One or a few rows at a time is something arriving. A whole list at once is the list being filled.
		if (!Enabled || e.Action != NotifyCollectionChangedAction.Add || e.NewItems is not { Count: > 0 and <= 3 } added || !list.IsEffectivelyVisible)
			return;

		int at = e.NewStartingIndex;
		int count = added.Count;
		Dispatcher.UIThread.Post(() =>
		{
			for (int i = at; i < at + count; i++)
			{
				if (list.ContainerFromIndex(i) is Control row && !row.Classes.Contains("fresh"))
				{
					row.Classes.Add("fresh");
					DispatcherTimer.RunOnce(() => row.Classes.Remove("fresh"), freshFor, DispatcherPriority.Background);
				}
			}
		}, DispatcherPriority.Loaded);
	}
}
