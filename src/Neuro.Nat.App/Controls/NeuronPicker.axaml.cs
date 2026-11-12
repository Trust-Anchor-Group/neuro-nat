using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Controls;

/// <summary>
/// The Neuron filter in the top bar: a button that says which Neurons the screens show, and a
/// drop-down with a search box, All and None, and a tick box per Neuron.
/// <para>
/// Keyboard: Enter, Space or Down on the button opens it. Typing narrows the list; Down goes
/// into the list, Up and Down move, Space or Enter ticks, typing goes back to the search box.
/// Escape closes it and gives the keyboard back to the button.
/// </para>
/// </summary>
public partial class NeuronPicker : UserControl
{
	private NeuronPickerViewModel? vm;

	public NeuronPicker()
	{
		this.InitializeComponent();

		this.PickerButton.Click += (_, _) => this.SetOpen(!(this.vm?.IsOpen ?? false));
		this.PickerButton.KeyDown += this.Button_KeyDown;
		this.DropPanel.AddHandler(KeyDownEvent, this.Drop_KeyDown, RoutingStrategies.Tunnel);
		this.PickerList.AddHandler(TextInputEvent, this.List_TextInput, RoutingStrategies.Tunnel);
		this.PickerList.Tapped += this.List_Tapped;
		this.Drop.Opened += (_, _) => Dispatcher.UIThread.Post(this.FocusSearch, DispatcherPriority.Input);
		this.Drop.Closed += (_, _) =>
		{
			// Closed by a click elsewhere: what was clicked keeps the keyboard. Closed from inside: the button gets it back.
			if (this.IsKeyboardInside())
				this.PickerButton.Focus(NavigationMethod.Directional);
		};
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (this.vm is not null)
			this.vm.PropertyChanged -= this.Vm_PropertyChanged;

		this.vm = this.DataContext as NeuronPickerViewModel;
		if (this.vm is not null)
			this.vm.PropertyChanged += this.Vm_PropertyChanged;
	}

	private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		// The highlighted row is always in sight.
		if (e.PropertyName == nameof(NeuronPickerViewModel.Highlighted) && this.vm?.Highlighted is NeuronChipViewModel n && this.vm.IsOpen)
			this.PickerList.ScrollIntoView(n);
	}

	private void SetOpen(bool open)
	{
		if (this.vm is null)
			return;

		bool wasInside = this.IsKeyboardInside();
		this.vm.IsOpen = open;
		if (!open && wasInside)
			this.PickerButton.Focus(NavigationMethod.Directional);
	}

	private bool IsKeyboardInside()
		=> TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual v && (v == this.PickerSearch || this.DropPanel.IsVisualAncestorOf(v));

	private void FocusSearch()
	{
		if (this.vm?.IsOpen != true)
			return;

		this.PickerSearch.Focus(NavigationMethod.Directional);
		this.PickerSearch.CaretIndex = this.PickerSearch.Text?.Length ?? 0;
	}

	private void FocusHighlighted()
	{
		if (this.vm?.Highlighted is not NeuronChipViewModel n)
			return;

		this.PickerList.ScrollIntoView(n);
		if (this.PickerList.ContainerFromItem(n) is Control row)
			row.Focus(NavigationMethod.Directional);
	}

	private void Button_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Down)
		{
			this.SetOpen(true);
			e.Handled = true;
		}
	}

	private void Drop_KeyDown(object? sender, KeyEventArgs e)
	{
		if (this.vm is null || e.KeyModifiers != KeyModifiers.None)
			return;

		bool inSearch = e.Source is Visual v && (v == this.PickerSearch || this.PickerSearch.IsVisualAncestorOf(v));
		bool inList = !inSearch && e.Source is Visual l && (l == this.PickerList || this.PickerList.IsVisualAncestorOf(l));
		switch (e.Key)
		{
			case Key.Escape:
				this.SetOpen(false);
				e.Handled = true;
				break;

			case Key.Down when inSearch:
				this.vm.Highlighted ??= this.vm.Shown.FirstOrDefault();
				this.FocusHighlighted();
				e.Handled = true;
				break;

			case Key.Down when inList:
				this.vm.Move(1);
				this.FocusHighlighted();
				e.Handled = true;
				break;

			case Key.Up when inList:
				if (this.vm.Highlighted is null || this.vm.Shown.IndexOf(this.vm.Highlighted) <= 0)
					this.FocusSearch();
				else
				{
					this.vm.Move(-1);
					this.FocusHighlighted();
				}

				e.Handled = true;
				break;

			case Key.Space when inList:
			case Key.Enter when inList || inSearch:
				this.vm.ToggleCommand.Execute(null);
				e.Handled = true;
				break;
		}
	}

	/// <summary>Typing while the list has the keyboard goes on in the search box.</summary>
	private void List_TextInput(object? sender, TextInputEventArgs e)
	{
		if (this.vm is null || string.IsNullOrEmpty(e.Text) || e.Text == " ")
			return;

		this.vm.Query += e.Text;
		this.FocusSearch();
		e.Handled = true;
	}

	private void List_Tapped(object? sender, TappedEventArgs e)
	{
		if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true)?.DataContext is NeuronChipViewModel n)
			this.vm?.ToggleCommand.Execute(n);
	}
}
