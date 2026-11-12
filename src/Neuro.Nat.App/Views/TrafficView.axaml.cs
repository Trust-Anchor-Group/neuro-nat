using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

/// <summary>
/// The traffic view. Lines are selected the way text is in an editor: click, Shift-click,
/// Ctrl-click, Ctrl+A, or drag across them. The view follows the newest line until you scroll
/// up, and then leaves the lines where they are.
/// </summary>
public partial class TrafficView : UserControl
{
	private SnifferViewModel? vm;
	private ScrollViewer? scroll;
	private int dragAnchor = -1;
	private int dragLast = -1;
	private bool endQueued;
	private int settling;

	public TrafficView()
	{
		this.InitializeComponent();

		this.List.TemplateApplied += (_, _) => this.HookScroll();
		this.List.SelectionChanged += (_, _) =>
		{
			if (this.vm is not null)
				this.vm.Selected = this.List.SelectedItems is null ? [] : [.. this.List.SelectedItems.OfType<TrafficLineViewModel>().OrderBy(l => l.Line.Sequence)];
		};
		this.List.DoubleTapped += this.List_DoubleTapped;
		this.List.AddHandler(PointerPressedEvent, this.List_PointerPressed, RoutingStrategies.Tunnel);
		this.List.AddHandler(PointerMovedEvent, this.List_PointerMoved, RoutingStrategies.Bubble, true);
		this.List.AddHandler(PointerReleasedEvent, (_, _) => this.dragAnchor = -1, RoutingStrategies.Bubble, true);
		this.List.AddHandler(KeyDownEvent, this.List_KeyDown, RoutingStrategies.Tunnel);
		this.FindBox.AddHandler(KeyDownEvent, this.FindBox_KeyDown, RoutingStrategies.Tunnel);
		this.SizeChanged += (_, e) => this.Fit(e.NewSize.Width);
	}

	/// <summary>Fits the toolbar to the width the view has, whichever screen or window it is in.</summary>
	private void Fit(double width)
	{
		bool tight = width < 780;
		bool stacked = width < 640;

		// Narrower still, the buttons go down beside the find box, so that none of them is cut off.
		bool narrowest = width < 480;

		this.Toolbar.Classes.Set("tight", tight);
		Grid.SetRow(this.FindRow, stacked ? 1 : 0);
		Grid.SetColumn(this.FindRow, stacked ? 0 : 1);
		Grid.SetColumnSpan(this.FindRow, stacked ? (narrowest ? 2 : 3) : 1);
		Grid.SetRow(this.Actions, narrowest ? 1 : 0);
		this.FindRow.MaxWidth = stacked ? double.PositiveInfinity : 420;
		this.FindRow.HorizontalAlignment = stacked ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
	}

	/// <summary>Hides the "own window" button, for the view that already is in its own window.</summary>
	public bool IsInOwnWindow
	{
		get => !this.WindowButton.IsVisible;
		set => this.WindowButton.IsVisible = !value;
	}

	/// <summary>Puts the keyboard in the find box (Ctrl+F).</summary>
	public void FocusFind()
	{
		this.FindBox.Focus();
		this.FindBox.SelectAll();
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);

		if (this.vm is not null)
		{
			this.vm.ScrollToLatest -= this.Vm_ScrollToLatest;
			this.vm.ScrollTo -= this.Vm_ScrollTo;
			this.vm.Lines.CollectionChanged -= this.Lines_CollectionChanged;
		}

		this.vm = this.DataContext as SnifferViewModel;

		if (this.vm is not null)
		{
			this.vm.ScrollToLatest += this.Vm_ScrollToLatest;
			this.vm.ScrollTo += this.Vm_ScrollTo;
			this.vm.Lines.CollectionChanged += this.Lines_CollectionChanged;
			if (this.vm.IsFollowing)
				this.QueueToEnd();
		}
	}

	private void HookScroll()
	{
		if (this.scroll is not null)
			return;

		this.scroll = this.List.FindDescendantOfType<ScrollViewer>();
		if (this.scroll is null)
			return;

		this.scroll.ScrollChanged += (_, e) =>
		{
			if (this.vm is null || this.scroll is null)
				return;

			if (this.scroll.Offset.Y >= this.scroll.Extent.Height - this.scroll.Viewport.Height - 6)
			{
				this.vm.IsFollowing = true;
				this.vm.SeenLatest();
			}
			else if (e.ExtentDelta.Y == 0 && e.OffsetDelta.Y != 0)
				this.vm.IsFollowing = false;    // You scrolled away from the newest line.
			else if (this.vm.IsFollowing && this.settling++ < 4)
				this.QueueToEnd();              // The list grew or shrank under us.
		};

		// The wheel is the plainest way to say "stop following", also while lines pour in.
		this.List.AddHandler(PointerWheelChangedEvent, (_, e) =>
		{
			if (e.Delta.Y > 0 && this.vm is not null)
				this.vm.IsFollowing = false;
		}, RoutingStrategies.Tunnel);
	}

	private void QueueToEnd()
	{
		if (this.endQueued)
			return;

		this.endQueued = true;
		Dispatcher.UIThread.Post(() =>
		{
			this.endQueued = false;
			if (this.vm?.IsFollowing == true)
				this.ToEnd();
		}, DispatcherPriority.Background);
	}

	private void ToEnd()
	{
		this.HookScroll();
		if (this.vm is { Lines.Count: > 0 } v)
			this.List.ScrollIntoView(v.Lines.Count - 1);
	}

	private void Lines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (this.vm?.IsFollowing == true && e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
		{
			this.settling = 0;
			this.QueueToEnd();
		}
	}

	private void Vm_ScrollToLatest(object? sender, EventArgs e)
	{
		this.settling = 0;
		this.ToEnd();
	}

	private void Vm_ScrollTo(object? sender, TrafficLineViewModel line)
	{
		this.List.ScrollIntoView(line);
		this.List.SelectedItem = line;
	}

	private int RowAt(Visual? v)
		=> v?.FindAncestorOfType<ListBoxItem>(true) is ListBoxItem row ? this.List.IndexFromContainer(row) : -1;

	private void List_DoubleTapped(object? sender, TappedEventArgs e)
	{
		if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true)?.DataContext is TrafficLineViewModel line && line.CanOpen && e.Source is not SelectableTextBlock)
			line.IsOpen = !line.IsOpen;
	}

	private void List_PointerPressed(object? sender, PointerPressedEventArgs e)
	{
		// Dragging over rows selects them. Inside an opened line the drag selects text instead.
		bool plain = e.KeyModifiers == KeyModifiers.None && e.GetCurrentPoint(this.List).Properties.IsLeftButtonPressed;
		Visual? source = e.Source as Visual;
		bool inText = source is SelectableTextBlock || source?.FindAncestorOfType<SelectableTextBlock>() is not null;
		bool inButton = source?.FindAncestorOfType<Button>(true) is not null;
		this.dragAnchor = plain && !inText && !inButton ? this.RowAt(source) : -1;
		this.dragLast = this.dragAnchor;
	}

	private void List_PointerMoved(object? sender, PointerEventArgs e)
	{
		if (this.dragAnchor < 0 || !e.GetCurrentPoint(this.List).Properties.IsLeftButtonPressed)
			return;

		int row = this.RowAt(this.List.InputHitTest(e.GetPosition(this.List)) as Visual);
		if (row < 0 || row == this.dragLast)
			return;

		this.dragLast = row;
		this.List.Selection.BeginBatchUpdate();
		try
		{
			this.List.Selection.Clear();
			this.List.Selection.SelectRange(Math.Min(this.dragAnchor, row), Math.Max(this.dragAnchor, row));
		}
		finally
		{
			this.List.Selection.EndBatchUpdate();
		}
	}

	private void List_KeyDown(object? sender, KeyEventArgs e)
	{
		if (this.vm is null)
			return;

		if (e.Key == Key.F3)
		{
			this.vm.FindNextCommand.Execute(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "back" : null);
			e.Handled = true;
		}
		else if (e.Key == Key.End && e.KeyModifiers == KeyModifiers.Control)
		{
			this.vm.JumpToLatestCommand.Execute(null);
			e.Handled = true;
		}
	}

	private void FindBox_KeyDown(object? sender, KeyEventArgs e)
	{
		if (this.vm is null)
			return;

		if (e.Key is Key.Enter or Key.F3)
		{
			this.vm.FindNextCommand.Execute(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "back" : null);
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			this.vm.FindText = string.Empty;
			this.List.Focus();
			e.Handled = true;
		}
	}
}
