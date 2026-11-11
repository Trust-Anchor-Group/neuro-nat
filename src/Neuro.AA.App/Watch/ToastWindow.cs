using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Neuro.AA.App.ViewModels;
using Neuro.AA.App.Views;

namespace Neuro.AA.App.Watch;

/// <summary>
/// A pop-up in the corner of the screen, for when the window is not in front: the same card the
/// window shows in its own corner, with its reply box and answer buttons. It closes with its
/// pop-up.
/// </summary>
public sealed class ToastWindow : Window
{
	public ToastWindow(NoticeViewModel notice)
	{
		this.Notice = notice;
		this.WindowDecorations = WindowDecorations.None;
		this.Topmost = true;
		this.ShowInTaskbar = false;
		this.ShowActivated = false;
		this.CanResize = false;
		this.SizeToContent = SizeToContent.Height;
		this.Width = 372;
		this.Background = Brushes.Transparent;
		this.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
		this.Title = "Neuro.AA alert";
		this.Icon = AppIcon.Get(1);

		// The margin leaves room for the card's shadow.
		this.Content = new NoticeCard { DataContext = notice, Margin = new Thickness(6, 4, 6, 10) };

		notice.Closed += this.Notice_Closed;
		this.Opened += (_, _) => notice.Start();
		this.Closed += (_, _) =>
		{
			notice.Closed -= this.Notice_Closed;
			notice.Close();
		};
	}

	public NoticeViewModel Notice { get; }

	private void Notice_Closed(object? sender, EventArgs e) => this.Close();
}
