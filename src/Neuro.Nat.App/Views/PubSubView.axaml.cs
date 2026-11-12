using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

public partial class PubSubView : UserControl
{
	private PubSubViewModel? shown;

	public PubSubView()
	{
		this.InitializeComponent();
		this.EditorBox.AddHandler(KeyDownEvent, this.Editor_KeyDown, RoutingStrategies.Tunnel);
		this.ProblemButton.Click += (_, _) => this.GoToProblem();
		this.AskInput.KeyDown += (_, e) => this.OnEnter(e, vm => vm.ConfirmAskCommand.Execute(null), vm => vm.CancelAskCommand.Execute(null));
		this.NewNodeBox.KeyDown += (_, e) => this.OnEnter(e, vm => vm.CreateNodeCommand.Execute(null), vm => vm.CancelCreateCommand.Execute(null));
		this.NewPersonBox.KeyDown += (_, e) => this.OnEnter(e, vm => vm.AddPersonCommand.Execute(null), null);
		this.NodeFilterBox.KeyDown += (_, e) => this.OnEnter(e, null, vm => vm.NodeFilter = string.Empty);
		this.ItemFilterBox.KeyDown += (_, e) => this.OnEnter(e, null, vm => vm.ItemFilter = string.Empty);
	}

	private void OnEnter(KeyEventArgs e, Action<PubSubViewModel>? enter, Action<PubSubViewModel>? escape)
	{
		if (this.DataContext is not PubSubViewModel vm || e.KeyModifiers != KeyModifiers.None)
			return;

		if (e.Key == Key.Enter && enter is not null)
		{
			enter(vm);
			e.Handled = true;
		}
		else if (e.Key == Key.Escape && escape is not null)
		{
			escape(vm);
			e.Handled = true;
		}
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (this.shown is not null)
		{
			this.shown.EditorOpened -= this.Vm_EditorOpened;
			this.shown.CreateOpened -= this.Vm_CreateOpened;
			this.shown.PropertyChanged -= this.Vm_PropertyChanged;
		}

		this.shown = this.DataContext as PubSubViewModel;
		if (this.shown is not null)
		{
			this.shown.EditorOpened += this.Vm_EditorOpened;
			this.shown.CreateOpened += this.Vm_CreateOpened;
			this.shown.PropertyChanged += this.Vm_PropertyChanged;
		}
	}

	// The same nodes show on a Neuron's page and on an account's: only the one on screen takes the keyboard.
	private void FocusLater(Control c)
	{
		if (!this.IsEffectivelyVisible)
			return;

		Dispatcher.UIThread.Post(() => c.Focus(), DispatcherPriority.Background);
	}

	private void Vm_EditorOpened(object? sender, EventArgs e)
	{
		this.FocusLater(this.EditorBox);
		this.EditorBox.CaretIndex = this.EditorBox.Text?.Length ?? 0;
	}

	private void Vm_CreateOpened(object? sender, EventArgs e) => this.FocusLater(this.NewNodeBox);

	private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(PubSubViewModel.Ask) && this.shown?.Ask is not null)
			this.FocusLater(this.AskInput);
	}

	private void Editor_KeyDown(object? sender, KeyEventArgs e)
	{
		if (this.DataContext is not PubSubViewModel vm)
			return;

		if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
		{
			if (vm.PublishCommand.CanExecute(null))
				vm.PublishCommand.Execute(null);
			e.Handled = true;
		}
		else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
		{
			vm.CloseEditorCommand.Execute(null);
			e.Handled = true;
		}
	}

	/// <summary>Puts the caret where the XML goes wrong.</summary>
	private void GoToProblem()
	{
		if (this.DataContext is not PubSubViewModel vm || vm.ProblemLine <= 0)
			return;

		this.EditorBox.CaretIndex = Offset(this.EditorBox.Text ?? string.Empty, vm.ProblemLine, vm.ProblemPlace);
		this.EditorBox.Focus();
	}

	/// <summary>Where in a text a line and a place on it are. Both count from 1.</summary>
	public static int Offset(string text, int line, int place)
	{
		int at = 0;
		for (int i = 1; i < line; i++)
		{
			int next = text.IndexOf('\n', at);
			if (next < 0)
				return text.Length;
			at = next + 1;
		}

		return Math.Min(text.Length, at + Math.Max(0, place - 1));
	}
}
