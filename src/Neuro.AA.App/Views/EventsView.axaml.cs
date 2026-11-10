using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Neuro.AA.App.ViewModels;

namespace Neuro.AA.App.Views;

public partial class EventsView : UserControl
{
	private ScrollViewer? streamScroll;

	public EventsView()
	{
		this.InitializeComponent();
		this.StreamList.TemplateApplied += (_, _) => this.HookStreamScroll();
	}

	private void HookStreamScroll()
	{
		if (this.streamScroll is not null)
			return;

		this.streamScroll = this.StreamList.FindDescendantOfType<ScrollViewer>();
		if (this.streamScroll is null)
			return;

		// New events are held back while the stream is scrolled down, and flow in again at the top.
		this.streamScroll.ScrollChanged += (_, _) =>
		{
			if (this.DataContext is EventsViewModel vm)
			{
				bool atTop = this.streamScroll.Offset.Y < 4;
				vm.IsStreamAtTop = atTop;
				if (atTop && vm.HasHeld && vm.ShowHeldCommand.CanExecute(null))
					vm.ShowHeldCommand.Execute(null);
			}
		};
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);

		if (this.DataContext is EventsViewModel vm)
		{
			vm.ScrollStreamToTop += (_, _) =>
			{
				this.HookStreamScroll();
				if (this.streamScroll is not null)
					this.streamScroll.Offset = default;
			};
			vm.CopyToClipboard = async text =>
			{
				IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
				if (clipboard is not null)
					await clipboard.SetTextAsync(text);
			};
		}
	}
}
