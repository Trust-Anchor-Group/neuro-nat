using Avalonia.Controls;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

/// <summary>The Inbox: one list of everything that waits for you, with the open row beside it.</summary>
public partial class InboxView : UserControl
{
	public InboxView()
	{
		this.InitializeComponent();

		// Ctrl and Shift pick several rows; the view model answers for all of them at once.
		this.List.SelectionChanged += (_, _) =>
		{
			if (this.DataContext is InboxViewModel vm)
				vm.SelectedRows = [.. (this.List.SelectedItems ?? Array.Empty<object>()).OfType<InboxRowViewModel>()];
		};
	}
}
