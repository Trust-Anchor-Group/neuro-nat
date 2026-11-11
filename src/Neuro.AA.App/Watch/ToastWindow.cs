using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Neuro.AA.Engine.Alerts;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.Watch;

/// <summary>
/// A small alert in the corner of the screen: what happened, where, and when. A click opens it in
/// Neuro.AA; it closes by itself after a while, but stays while the pointer is on it.
/// </summary>
public sealed class ToastWindow : Window
{
	private readonly DispatcherTimer life;

	public ToastWindow(Alert alert, int more, Action open)
	{
		this.Alert = alert;
		this.WindowDecorations = WindowDecorations.None;
		this.Topmost = true;
		this.ShowInTaskbar = false;
		this.ShowActivated = false;
		this.CanResize = false;
		this.SizeToContent = SizeToContent.Height;
		this.Width = 360;
		this.Background = Brushes.Transparent;
		this.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
		this.Title = "Neuro.AA alert";
		this.Icon = AppIcon.Get(attention: true);

		string tone = alert.Kind switch
		{
			AlertKind.NeuronBack => "State.Ok",
			AlertKind.ContactRequest => "Accent",
			_ when alert.Severity >= EventSeverity.Error => "State.Bad",
			_ when alert.Severity >= EventSeverity.Warning || alert.Kind == AlertKind.Notification => "State.Busy",
			_ => "Accent",
		};

		string kind = alert.Kind switch
		{
			AlertKind.Notification => "Notification",
			AlertKind.Error => "Error",
			AlertKind.NeuronDown => "Neuron down",
			AlertKind.ContactRequest => "Contact request",
			_ => "Neuron back",
		};

		TextBlock close = new()
		{
			Text = "✕",
			FontSize = 12,
			Foreground = this.Brush("Ink.Tertiary"),
			Cursor = new Cursor(StandardCursorType.Hand),
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(8, 0, 0, 0),
		};
		close.PointerPressed += (_, e) =>
		{
			e.Handled = true;
			this.Close();
		};

		StackPanel text = new() { Spacing = 3 };
		text.Children.Add(new TextBlock
		{
			Text = kind.ToUpperInvariant() + (more > 0 ? $"  ·  {more} more" : string.Empty),
			FontSize = 10.5,
			FontWeight = FontWeight.SemiBold,
			Foreground = this.Brush(tone),
		});
		text.Children.Add(new TextBlock
		{
			Text = alert.Title,
			FontWeight = FontWeight.SemiBold,
			TextWrapping = TextWrapping.Wrap,
			MaxLines = 2,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Foreground = this.Brush("Ink.Primary"),
		});
		if (!string.IsNullOrEmpty(alert.Detail))
		{
			text.Children.Add(new TextBlock
			{
				Text = alert.Detail,
				FontSize = 12,
				TextTrimming = TextTrimming.CharacterEllipsis,
				Foreground = this.Brush("Ink.Secondary"),
			});
		}

		DockPanel body = new() { Margin = new Thickness(14, 10, 12, 12) };
		DockPanel.SetDock(close, Dock.Right);
		body.Children.Add(close);
		body.Children.Add(text);

		Grid card = new() { ColumnDefinitions = new ColumnDefinitions("4,*") };
		Border bar = new() { Background = this.Brush(tone), CornerRadius = new CornerRadius(8, 0, 0, 8) };
		Grid.SetColumn(body, 1);
		card.Children.Add(bar);
		card.Children.Add(body);

		this.Content = new Border
		{
			Background = this.Brush("Surface.Panel"),
			BorderBrush = this.Brush("Line.Strong"),
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(8),
			Margin = new Thickness(6),
			BoxShadow = BoxShadows.Parse("0 4 16 0 #40000000"),
			Cursor = new Cursor(StandardCursorType.Hand),
			Child = card,
		};

		this.PointerPressed += (_, e) =>
		{
			if (!e.Handled)
			{
				open();
				this.Close();
			}
		};

		// Errors and Neurons going down stay longer than information.
		TimeSpan shown = alert.Severity >= EventSeverity.Error ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(10);
		this.life = new DispatcherTimer(shown, DispatcherPriority.Background, (_, _) => this.Close());
		this.PointerEntered += (_, _) => this.life.Stop();
		this.PointerExited += (_, _) => this.life.Start();
		this.Opened += (_, _) => this.life.Start();
		this.Closed += (_, _) => this.life.Stop();
	}

	public Alert Alert { get; }

	private IBrush Brush(string key)
		=> Application.Current is Application app && app.TryGetResource(key, app.ActualThemeVariant, out object? v) && v is IBrush b ? b : Brushes.Gray;
}
