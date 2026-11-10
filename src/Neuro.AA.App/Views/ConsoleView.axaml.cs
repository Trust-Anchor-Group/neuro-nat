using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Neuro.AA.App.ViewModels;

namespace Neuro.AA.App.Views;

public partial class ConsoleView : UserControl
{
	private ConsoleViewModel? vm;

	public ConsoleView()
	{
		this.InitializeComponent();
		this.Input.AddHandler(KeyDownEvent, this.Input_KeyDown, RoutingStrategies.Tunnel);
		this.ConfirmBox.AddHandler(KeyDownEvent, this.Confirm_KeyDown, RoutingStrategies.Tunnel);
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);

		if (this.vm is not null)
		{
			this.vm.Runs.CollectionChanged -= this.Runs_CollectionChanged;
			this.vm.PropertyChanged -= this.Vm_PropertyChanged;
		}

		this.vm = this.DataContext as ConsoleViewModel;

		if (this.vm is not null)
		{
			this.vm.Runs.CollectionChanged += this.Runs_CollectionChanged;
			this.vm.PropertyChanged += this.Vm_PropertyChanged;
		}
	}

	private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ConsoleViewModel.IsConfirming) && this.vm?.IsConfirming == true)
			Dispatcher.UIThread.Post(() => this.ConfirmBox.Focus());
		else if (e.PropertyName == nameof(ConsoleViewModel.CommandText) && this.vm is not null)
			Dispatcher.UIThread.Post(() => this.Input.CaretIndex = this.vm.CommandText.Length);
	}

	private void Runs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		=> Dispatcher.UIThread.Post(() => this.Transcript.ScrollToEnd(), DispatcherPriority.Background);

	private void Input_KeyDown(object? sender, KeyEventArgs e)
	{
		if (this.vm is null)
			return;

		bool singleLine = !(this.vm.CommandText ?? string.Empty).Contains('\n');

		switch (e.Key)
		{
			case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
				e.Handled = true;
				if (this.vm.RunCommand.CanExecute(null))
					this.vm.RunCommand.Execute(null);
				break;

			case Key.Tab when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
				e.Handled = this.vm.Complete();
				break;

			case Key.Up when singleLine:
				e.Handled = true;
				this.vm.History(-1);
				break;

			case Key.Down when singleLine:
				e.Handled = true;
				this.vm.History(1);
				break;

			case Key.Escape:
				this.vm.CancelConfirmCommand.Execute(null);
				break;
		}
	}

	private void Confirm_KeyDown(object? sender, KeyEventArgs e)
	{
		if (this.vm is null)
			return;

		if (e.Key == Key.Enter)
		{
			e.Handled = true;
			this.vm.RunCommand.Execute(null);
		}
		else if (e.Key == Key.Escape)
		{
			e.Handled = true;
			this.vm.CancelConfirmCommand.Execute(null);
			this.Input.Focus();
		}
	}
}
