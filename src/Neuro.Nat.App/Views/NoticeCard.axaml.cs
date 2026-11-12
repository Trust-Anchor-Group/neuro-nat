using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

/// <summary>
/// The look of a pop-up, inside the window and on the desktop. A click anywhere but on its
/// buttons and its reply box opens what it is about.
/// </summary>
public partial class NoticeCard : UserControl
{
	public NoticeCard()
	{
		this.InitializeComponent();

		this.PointerEntered += (_, _) => this.Notice?.Hold();
		this.PointerExited += (_, _) => this.Notice?.Release();
		this.Card.PointerPressed += this.Card_PointerPressed;

		this.ReplyBox.GotFocus += (_, _) => this.Notice?.Hold();
		this.ReplyBox.LostFocus += (_, _) => this.Notice?.Release();
		this.ReplyBox.AddHandler(KeyDownEvent, this.ReplyBox_KeyDown, RoutingStrategies.Tunnel);
	}

	private NoticeViewModel? Notice => this.DataContext as NoticeViewModel;

	private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			return;

		// Buttons and the reply box keep their clicks.
		for (Visual? v = e.Source as Visual; v is not null && !ReferenceEquals(v, this.Card); v = v.GetVisualParent())
		{
			if (v is Button or TextBox)
				return;
		}

		e.Handled = true;
		this.Notice?.OpenItCommand.Execute(null);
	}

	private void ReplyBox_KeyDown(object? sender, KeyEventArgs e)
	{
		if (this.Notice is not NoticeViewModel n)
			return;

		if (e.Key == Key.Enter)
		{
			e.Handled = true;
			if (n.SendReplyCommand.CanExecute(null))
				n.SendReplyCommand.Execute(null);
		}
		else if (e.Key == Key.Escape)
		{
			e.Handled = true;
			n.Close();
		}
	}
}
