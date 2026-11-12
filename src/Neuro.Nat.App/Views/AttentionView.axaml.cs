using Avalonia.Controls;
using Avalonia.Input.Platform;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

public partial class AttentionView : UserControl
{
	public AttentionView() => this.InitializeComponent();

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);

		if (this.DataContext is AttentionViewModel vm)
		{
			vm.CopyToClipboard = async text =>
			{
				IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
				if (clipboard is not null)
					await clipboard.SetTextAsync(text);
			};

			vm.OpenUrl = async url =>
			{
				if (TopLevel.GetTopLevel(this) is TopLevel top && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
					await top.Launcher.LaunchUriAsync(uri);
			};
		}
	}
}
