using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

public partial class RunbookView : UserControl
{
	private RunbooksViewModel? vm;

	public RunbookView()
	{
		this.InitializeComponent();
		this.AskBox.AddHandler(KeyDownEvent, this.Ask_KeyDown, RoutingStrategies.Tunnel);
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);

		if (this.vm is not null)
			this.vm.PropertyChanged -= this.Vm_PropertyChanged;

		this.vm = this.DataContext as RunbooksViewModel;

		if (this.vm is not null)
			this.vm.PropertyChanged += this.Vm_PropertyChanged;
	}

	private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (this.vm is null)
			return;

		switch (e.PropertyName)
		{
			// A marked step asks: the keyboard goes where the answer is typed.
			case nameof(RunbooksViewModel.IsAsking) when this.vm.IsAsking:
				Dispatcher.UIThread.Post(() => this.AskBox.Focus());
				break;

			case nameof(RunbooksViewModel.IsEditing) when this.vm.IsEditing:
				Dispatcher.UIThread.Post(() => this.EditTitleBox.Focus());
				break;

			case nameof(RunbooksViewModel.Run):
				Dispatcher.UIThread.Post(() => this.RunScroll.ScrollToHome(), DispatcherPriority.Background);
				break;
		}
	}

	private void Ask_KeyDown(object? sender, KeyEventArgs e)
	{
		if (this.vm is null)
			return;

		if (e.Key == Key.Enter)
		{
			e.Handled = true;
			this.vm.ConfirmCommand.Execute(null);
		}
		else if (e.Key == Key.Escape)
		{
			e.Handled = true;
			this.vm.DeclineCommand.Execute(null);
		}
	}
}
