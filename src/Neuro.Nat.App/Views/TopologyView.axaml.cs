using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Neuro.Nat.App.Controls;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

public partial class TopologyView : UserControl
{
	public TopologyView()
	{
		this.InitializeComponent();
		this.Map.NodeActivated += (_, id) => (this.DataContext as TopologyViewModel)?.Activated(id);
		this.Map.ContextRequested += this.Map_ContextRequested;
	}

	/// <summary>Right-click on a point of the map: select it and show what can be done with it.</summary>
	private void Map_ContextRequested(object? sender, ContextRequestedEventArgs e)
	{
		if (this.DataContext is not TopologyViewModel vm)
			return;

		string? id = e.TryGetPosition(this.Map, out Avalonia.Point p) ? this.Map.IdAt(p) : vm.SelectedId;
		if (id is not null)
			vm.SelectedId = id;

		IReadOnlyList<ItemAction> actions = vm.ActionsFor(id is null ? [] : [id]);
		if (actions.Count == 0)
			return;

		ActionMenu.Build(actions).Open(this.Map);
		e.Handled = true;
	}

	private void ZoomIn_Click(object? sender, RoutedEventArgs e) => this.Map.ZoomBy(1.25);

	private void ZoomOut_Click(object? sender, RoutedEventArgs e) => this.Map.ZoomBy(1 / 1.25);
}
