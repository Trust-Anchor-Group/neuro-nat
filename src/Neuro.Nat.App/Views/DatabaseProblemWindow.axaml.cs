using Avalonia.Controls;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

public partial class DatabaseProblemWindow : Window
{
	public DatabaseProblemWindow() => this.InitializeComponent();

	public DatabaseProblemWindow(DatabaseProblemViewModel vm)
		: this()
	{
		this.DataContext = vm;
		vm.Chosen += (_, _) => this.Close();
	}
}
