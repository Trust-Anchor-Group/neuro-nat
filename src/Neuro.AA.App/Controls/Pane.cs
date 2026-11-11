using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Neuro.AA.App.ViewModels;

namespace Neuro.AA.App.Controls;

/// <summary>
/// Makes the line between two panes something you can drag. Put <c>c:Pane.Key="inbox"</c> on the
/// pane that has a width of its own (the list, the tree, the side panel): its border line
/// towards the rest of the screen becomes the handle. Dragging sets the pane's width,
/// double-clicking puts it back, and the width is remembered under the key.
/// </summary>
public static class Pane
{
	/// <summary>How close to the line the pointer has to be, on either side of it.</summary>
	private const double Reach = 4;

	/// <summary>What is left for the rest of the screen, at least.</summary>
	private const double Rest = 280;

	public static readonly AttachedProperty<string?> KeyProperty = AvaloniaProperty.RegisterAttached<Border, string?>("Key", typeof(Pane));

	/// <summary>The narrowest the pane can be dragged.</summary>
	public static readonly AttachedProperty<double> MinProperty = AvaloniaProperty.RegisterAttached<Border, double>("Min", typeof(Pane), 200);

	private static readonly AttachedProperty<Grip?> GripProperty = AvaloniaProperty.RegisterAttached<Border, Grip?>("Grip", typeof(Pane));

	private static readonly Cursor sideways = new(StandardCursorType.SizeWestEast);

	static Pane()
	{
		KeyProperty.Changed.AddClassHandler<Border>((pane, e) =>
		{
			pane.GetValue(GripProperty)?.Dispose();
			pane.SetValue(GripProperty, e.NewValue is string key && key.Length > 0 ? new Grip(pane, key) : null);
		});
	}

	public static string? GetKey(Border pane) => pane.GetValue(KeyProperty);
	public static void SetKey(Border pane, string? value) => pane.SetValue(KeyProperty, value);
	public static double GetMin(Border pane) => pane.GetValue(MinProperty);
	public static void SetMin(Border pane, double value) => pane.SetValue(MinProperty, value);

	/// <summary>Sets a pane's width as a drag would, and remembers it. Null puts it back. For tests and for "reset".</summary>
	public static void Resize(Border pane, double? width) => pane.GetValue(GripProperty)?.Set(width, remember: true);

	/// <summary>True when the point, in the pane's own coordinates, is on the line that can be dragged.</summary>
	public static bool IsOnLine(Border pane, Point point) => pane.GetValue(GripProperty)?.IsOnLine(point) == true;

	private sealed class Grip : IDisposable
	{
		private readonly Border pane;
		private readonly string key;
		private TopLevel? top;
		private Control? parent;
		private bool dragging;
		private bool hovering;
		private double? wanted;

		public Grip(Border pane, string key)
		{
			this.pane = pane;
			this.key = key;
			pane.AttachedToVisualTree += this.Attached;
			pane.DetachedFromVisualTree += this.Detached;
			if (pane.IsAttachedToVisualTree())
				this.Attach();
		}

		public void Dispose()
		{
			this.pane.AttachedToVisualTree -= this.Attached;
			this.pane.DetachedFromVisualTree -= this.Detached;
			this.Detach();
		}

		private void Attached(object? sender, VisualTreeAttachmentEventArgs e) => this.Attach();
		private void Detached(object? sender, VisualTreeAttachmentEventArgs e) => this.Detach();

		// The line is on the side where the pane draws its border towards the rest of the screen.
		private bool OnTheLeft => this.pane.BorderThickness.Left > 0 && this.pane.BorderThickness.Right == 0;

		private void Attach()
		{
			this.Detach();
			this.top = TopLevel.GetTopLevel(this.pane);
			if (this.top is null)
				return;

			// The pane and what is next to it are often see-through, so the pointer is watched from the window.
			this.top.AddHandler(InputElement.PointerMovedEvent, this.Moved, RoutingStrategies.Tunnel, true);
			this.top.AddHandler(InputElement.PointerPressedEvent, this.Pressed, RoutingStrategies.Tunnel, true);
			this.top.AddHandler(InputElement.PointerReleasedEvent, this.Released, RoutingStrategies.Tunnel, true);
			this.top.AddHandler(InputElement.PointerCaptureLostEvent, this.Lost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

			this.parent = this.pane.GetVisualParent() as Control;
			if (this.parent is not null)
				this.parent.SizeChanged += this.Parent_SizeChanged;

			if (Ui.Layout is SettingsViewModel settings)
				settings.LayoutReset += this.Reset;

			this.wanted = Ui.Layout?.Width(this.key);
			this.Apply();
		}

		private void Detach()
		{
			if (this.top is not null)
			{
				this.top.RemoveHandler(InputElement.PointerMovedEvent, this.Moved);
				this.top.RemoveHandler(InputElement.PointerPressedEvent, this.Pressed);
				this.top.RemoveHandler(InputElement.PointerReleasedEvent, this.Released);
				this.top.RemoveHandler(InputElement.PointerCaptureLostEvent, this.Lost);
				if (this.hovering)
					this.top.Cursor = null;
			}

			if (this.parent is not null)
				this.parent.SizeChanged -= this.Parent_SizeChanged;
			if (Ui.Layout is SettingsViewModel settings)
				settings.LayoutReset -= this.Reset;

			this.top = null;
			this.parent = null;
			this.dragging = false;
			this.hovering = false;
		}

		private void Reset(object? sender, EventArgs e) => this.Set(null, remember: false);

		private void Parent_SizeChanged(object? sender, SizeChangedEventArgs e) => this.Apply();

		/// <summary>The widest the pane may be right now: the rest of the screen keeps some room.</summary>
		private double Most => this.parent is { Bounds.Width: > 0 } p ? Math.Max(GetMin(this.pane), p.Bounds.Width - Rest) : double.PositiveInfinity;

		/// <summary>Shows the width that was asked for, as far as the window has room for it.</summary>
		private void Apply()
		{
			if (this.wanted is double w)
				this.pane.Width = Math.Clamp(w, GetMin(this.pane), this.Most);
			else
				this.pane.ClearValue(Avalonia.Layout.Layoutable.WidthProperty);
		}

		public void Set(double? width, bool remember)
		{
			this.wanted = width is double w ? Math.Clamp(w, GetMin(this.pane), this.Most) : null;
			this.Apply();
			if (remember)
				Ui.Layout?.SetWidth(this.key, this.wanted);
		}

		public bool IsOnLine(Point p)
		{
			if (!this.pane.IsEffectivelyVisible || p.Y < 0 || p.Y > this.pane.Bounds.Height)
				return false;

			double line = this.OnTheLeft ? 0 : this.pane.Bounds.Width;
			return Math.Abs(p.X - line) <= Reach;
		}

		private void Moved(object? sender, PointerEventArgs e)
		{
			if (this.top is null)
				return;

			Point p = e.GetPosition(this.pane);
			if (this.dragging)
			{
				this.wanted = Math.Clamp(this.OnTheLeft ? this.pane.Bounds.Width - p.X : p.X, GetMin(this.pane), this.Most);
				this.Apply();
				e.Handled = true;
				return;
			}

			bool on = this.IsOnLine(p);
			if (on != this.hovering)
			{
				this.hovering = on;
				this.top.Cursor = on ? sideways : null;
			}
		}

		private void Pressed(object? sender, PointerPressedEventArgs e)
		{
			if (!e.GetCurrentPoint(this.pane).Properties.IsLeftButtonPressed || !this.IsOnLine(e.GetPosition(this.pane)))
				return;

			e.Handled = true;

			// Twice on the line: back to the pane's own width.
			if (e.ClickCount >= 2)
			{
				this.dragging = false;
				this.Set(null, remember: true);
				return;
			}

			this.dragging = true;
			e.Pointer.Capture(this.pane);
		}

		private void Released(object? sender, PointerReleasedEventArgs e)
		{
			if (!this.dragging)
				return;

			this.dragging = false;
			e.Pointer.Capture(null);
			e.Handled = true;
			Ui.Layout?.SetWidth(this.key, this.wanted);
		}

		private void Lost(object? sender, PointerCaptureLostEventArgs e)
		{
			if (!this.dragging)
				return;

			this.dragging = false;
			Ui.Layout?.SetWidth(this.key, this.wanted);
		}
	}
}
