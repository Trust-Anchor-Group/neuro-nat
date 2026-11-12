using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Neuro.Nat.App.Converters;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Controls;

/// <summary>
/// Gives a control its right-click menu, its keyboard shortcuts and, for a list, the bar that
/// appears when several rows are selected. All three come from the same list of actions, which
/// the control's <see cref="IActionSource"/> hands out for whatever is selected.
/// <code>
/// &lt;ListBox c:ActionMenu.Source="{Binding}" c:ActionMenu.Bar="{Binding #Bar}" SelectionMode="Multiple" /&gt;
/// </code>
/// On anything that is not a list, the actions are for the control's own data.
/// </summary>
public static class ActionMenu
{
	public static readonly AttachedProperty<IActionSource?> SourceProperty =
		AvaloniaProperty.RegisterAttached<Control, IActionSource?>("Source", typeof(ActionMenu));

	public static readonly AttachedProperty<ActionBar?> BarProperty =
		AvaloniaProperty.RegisterAttached<Control, ActionBar?>("Bar", typeof(ActionMenu));

	/// <summary>The item the actions are for, when it is not the control's own data.</summary>
	public static readonly AttachedProperty<object?> ItemProperty =
		AvaloniaProperty.RegisterAttached<Control, object?>("Item", typeof(ActionMenu));

	/// <summary>
	/// On a list where selecting a row opens it (a conversation, an Inbox row): a right-click on
	/// a row that is not selected acts on that row without selecting it, so nothing opens or gets
	/// marked as read just because its menu was asked for.
	/// </summary>
	public static readonly AttachedProperty<bool> KeepSelectionProperty =
		AvaloniaProperty.RegisterAttached<Control, bool>("KeepSelection", typeof(ActionMenu));

	public static bool GetKeepSelection(Control c) => c.GetValue(KeepSelectionProperty);
	public static void SetKeepSelection(Control c, bool value) => c.SetValue(KeepSelectionProperty, value);

	/// <summary>On a button: a click opens the menu under it, so the actions can be found without right-clicking.</summary>
	public static readonly AttachedProperty<bool> OnClickProperty =
		AvaloniaProperty.RegisterAttached<Control, bool>("OnClick", typeof(ActionMenu));

	private static readonly AttachedProperty<bool> HookedProperty =
		AvaloniaProperty.RegisterAttached<Control, bool>("Hooked", typeof(ActionMenu));

	static ActionMenu()
	{
		SourceProperty.Changed.AddClassHandler<Control>((c, _) => Hook(c));
		BarProperty.Changed.AddClassHandler<Control>((c, _) => UpdateBar(c));
		OnClickProperty.Changed.AddClassHandler<Button>((b, e) =>
		{
			if (e.NewValue is true)
				b.Click += OnButtonClick;
			else
				b.Click -= OnButtonClick;
		});
	}

	public static bool GetOnClick(Control c) => c.GetValue(OnClickProperty);
	public static void SetOnClick(Control c, bool value) => c.SetValue(OnClickProperty, value);

	private static void OnButtonClick(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button b || GetSource(b) is not IActionSource source)
			return;

		IReadOnlyList<ItemAction> actions = ActionsOf(source, Items(b));
		if (actions.Count == 0)
			return;

		ContextMenu menu = Build(actions);
		menu.Placement = PlacementMode.BottomEdgeAlignedRight;
		menu.PlacementTarget = b;
		Last = menu;
		menu.Open(b);
	}

	/// <summary>
	/// What can be done to some items: what their source offers, and "Copy link" for anything
	/// that has a link. The menu, the bar and the keys all ask here, so they offer the same.
	/// </summary>
	public static IReadOnlyList<ItemAction> ActionsOf(IActionSource source, IReadOnlyList<object> items)
		=> Links.WithLink(source.ActionsFor(items), items);

	public static IActionSource? GetSource(Control c) => c.GetValue(SourceProperty);
	public static void SetSource(Control c, IActionSource? value) => c.SetValue(SourceProperty, value);
	public static ActionBar? GetBar(Control c) => c.GetValue(BarProperty);
	public static void SetBar(Control c, ActionBar? value) => c.SetValue(BarProperty, value);
	public static object? GetItem(Control c) => c.GetValue(ItemProperty);
	public static void SetItem(Control c, object? value) => c.SetValue(ItemProperty, value);

	private static void Hook(Control c)
	{
		if (c.GetValue(HookedProperty))
			return;

		c.SetValue(HookedProperty, true);
		c.ContextRequested += OnContextRequested;
		c.AddHandler(InputElement.KeyDownEvent, OnKeyDown);

		if (c is SelectingItemsControl list)
			list.SelectionChanged += (_, _) => UpdateBar(list);
	}

	/// <summary>The menu opened last, by a right-click or a "more" button. Tests look at it.</summary>
	public static ContextMenu? Last { get; private set; }

	/// <summary>The items an action would apply to right now.</summary>
	public static IReadOnlyList<object> Items(Control c)
	{
		if (c is ListBox list)
			return list.SelectedItems is IList sel ? [.. sel.Cast<object>().Where(o => o is not null)] : [];

		if (c is TreeView tree)
			return tree.SelectedItem is object t ? [t] : [];

		object? item = GetItem(c) ?? c.DataContext;
		return item is null ? [] : [item];
	}

	private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
	{
		if (sender is not Control c || GetSource(c) is not IActionSource source)
			return;

		// Right-clicking a row that is not part of the selection acts on that row alone, as in a
		// file manager. Right-clicking inside the selection keeps it.
		IReadOnlyList<object>? only = null;
		if (c is ListBox list && e.Source is Visual v)
		{
			ListBoxItem? row = v.FindAncestorOfType<ListBoxItem>(true);
			if (row?.DataContext is object item && !(list.SelectedItems?.Contains(item) ?? false))
			{
				if (GetKeepSelection(list))
					only = [item];
				else
					list.SelectedItem = item;
			}
			else if (row is null)
				return;
		}
		else if (c is TreeView tree && e.Source is Visual tv)
		{
			TreeViewItem? row = tv.FindAncestorOfType<TreeViewItem>(true);
			if (row?.DataContext is object item && !ReferenceEquals(tree.SelectedItem, item))
				tree.SelectedItem = item;
			else if (row is null)
				return;
		}

		IReadOnlyList<ItemAction> actions = ActionsOf(source, only ?? Items(c));
		if (actions.Count == 0)
			return;

		ContextMenu menu = Build(actions);
		Last = menu;
		menu.Open(c);
		e.Handled = true;
	}

	private static void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Handled || sender is not Control c || GetSource(c) is not IActionSource source)
			return;

		// Keys typed into a text box belong to the text box.
		if (e.Source is TextBox)
			return;

		if (e.Key == Key.Escape && c is ListBox many && (many.SelectedItems?.Count ?? 0) > 1)
		{
			many.UnselectAll();
			e.Handled = true;
			return;
		}

		bool plain = e.KeyModifiers == KeyModifiers.None;
		if (plain && e.Key is not (Key.Enter or Key.Delete or Key.F2 or Key.F5))
			return;

		IReadOnlyList<object> items = Items(c);
		if (items.Count == 0)
			return;

		foreach (ItemAction a in Flatten(ActionsOf(source, items)))
		{
			if (a.Key is null || !a.IsEnabled)
				continue;

			KeyGesture gesture = KeyGesture.Parse(a.Key);
			if (gesture.Matches(e))
			{
				_ = a.RunAsync();
				e.Handled = true;
				return;
			}
		}
	}

	private static IEnumerable<ItemAction> Flatten(IEnumerable<ItemAction> actions)
	{
		foreach (ItemAction a in actions)
		{
			if (a.Children is not null)
			{
				foreach (ItemAction child in Flatten(a.Children))
					yield return child;
			}
			else
				yield return a;
		}
	}

	private static void UpdateBar(Control c)
	{
		if (GetBar(c) is not ActionBar bar)
			return;

		if (c is ListBox list && GetSource(c) is IActionSource source && (list.SelectedItems?.Count ?? 0) > 1)
			bar.Show(list.SelectedItems!.Count, ActionsOf(source, Items(c)), list.UnselectAll);
		else
			bar.Hide();
	}

	/// <summary>The menu for a list of actions: groups in their fixed order, a line between groups.</summary>
	public static ContextMenu Build(IReadOnlyList<ItemAction> actions)
	{
		ContextMenu menu = new();
		foreach (object item in MenuItems(actions))
			menu.Items.Add(item);

		return menu;
	}

	/// <summary>Menu items for a list of actions, for a menu or a button's drop-down.</summary>
	public static IEnumerable<object> MenuItems(IReadOnlyList<ItemAction> actions)
	{
		bool first = true;
		foreach (IGrouping<ActionGroup, ItemAction> group in actions.GroupBy(a => a.Group).OrderBy(g => g.Key))
		{
			if (!first)
				yield return new Separator();

			first = false;
			foreach (ItemAction a in group)
				yield return Item(a);
		}
	}

	private static MenuItem Item(ItemAction a)
	{
		MenuItem item = new() { Header = a.Text, IsEnabled = a.IsEnabled };

		if (a.Icon is not null && IconConverter.Get(a.Icon) is Geometry g)
			item.Icon = new LineIcon { Data = g, Width = 15, Height = 15 };

		if (a.Key is string key)
		{
			item.InputGesture = KeyGesture.Parse(key);

			// The menu would spell the gesture its own way ("Return"); show the key as people say it.
			item.TemplateApplied += (_, e) =>
			{
				if (e.NameScope.Find<TextBlock>("PART_InputGestureText") is TextBlock text)
					text.Text = key;
			};
		}

		if (a.IsDanger)
			item.Classes.Add("danger");

		if (a.Children is not null)
		{
			foreach (object child in MenuItems(a.Children))
				item.Items.Add(child);
		}
		else
			item.Click += (_, _) => _ = a.RunAsync();

		return item;
	}
}

/// <summary>
/// The bar that appears while several rows of a list are selected: how many, the main things
/// that can be done to all of them, the rest under "More", and a way out.
/// </summary>
public sealed class ActionBar : ContentControl
{
	public ActionBar()
	{
		this.IsVisible = false;
	}

	/// <summary>What the rows are called, for "3 events selected". Singular.</summary>
	public string Noun { get; set; } = "row";

	/// <summary>Plural of <see cref="Noun"/> when adding an s is not enough.</summary>
	public string? Nouns { get; set; }

	public void Hide()
	{
		this.IsVisible = false;
		this.Content = null;
	}

	public void Show(int count, IReadOnlyList<ItemAction> actions, Action clear)
	{
		DockPanel row = new() { LastChildFill = true };

		Button close = new() { Content = new LineIcon { Data = IconConverter.Get("Close"), Width = 13, Height = 13 }, Padding = new Thickness(6) };
		close.Classes.Add("ghost");
		close.Classes.Add("small");
		ToolTip.SetTip(close, "Clear the selection (Escape)");
		close.Click += (_, _) => clear();
		DockPanel.SetDock(close, Dock.Right);
		row.Children.Add(close);

		StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 6, 0) };
		DockPanel.SetDock(buttons, Dock.Right);

		foreach (ItemAction a in actions.Where(a => a.InBar && !a.HasChildren))
		{
			Button b = new() { Content = a.BarText ?? a.Text, IsEnabled = a.IsEnabled };
			b.Classes.Add("small");
			if (a.IsDanger)
				b.Classes.Add("danger");
			if (a.Key is not null)
				ToolTip.SetTip(b, $"{a.Text} ({a.Key})");
			else
				ToolTip.SetTip(b, a.Text);
			b.Click += (_, _) => _ = a.RunAsync();
			buttons.Children.Add(b);
		}

		if (actions.Any(a => !a.InBar))
		{
			Button more = new() { Content = "More" };
			more.Classes.Add("small");
			more.Classes.Add("ghost");
			MenuFlyout flyout = new() { Placement = PlacementMode.TopEdgeAlignedRight };
			foreach (object item in ActionMenu.MenuItems(actions))
				flyout.Items.Add(item);
			more.Flyout = flyout;
			buttons.Children.Add(more);
		}

		row.Children.Add(buttons);

		TextBlock text = new()
		{
			Text = Ui.Count(count, this.Noun, this.Nouns) + " selected",
			FontWeight = FontWeight.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0, 0, 14, 0),
		};
		text.Classes.Add("barText");
		row.Children.Add(text);

		Border frame = new() { Padding = new Thickness(12, 6, 6, 6), Child = row };
		frame.Classes.Add("actionBar");
		this.Content = frame;
		this.IsVisible = true;
	}
}
