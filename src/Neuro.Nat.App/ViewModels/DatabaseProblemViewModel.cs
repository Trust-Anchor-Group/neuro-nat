using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Storage;

namespace Neuro.Nat.App.ViewModels;

/// <summary>What the user chose when NAT could not open what it has saved.</summary>
public enum DatabaseChoice
{
	/// <summary>Nothing yet, or the window was closed: treated as Quit.</summary>
	None,
	TryAgain,
	StartFresh,
	Quit,
}

/// <summary>
/// Shown instead of the app when the database cannot be opened: NAT never starts empty without a
/// word. Try again, start fresh (what was saved is moved aside, nothing is deleted), or quit.
/// </summary>
public sealed partial class DatabaseProblemViewModel
{
	public string Title => LocalDatabase.CannotOpen;

	public string Detail => LocalDatabase.CannotOpenDetail;

	public DatabaseChoice Choice { get; private set; }

	/// <summary>Raised once a choice is made.</summary>
	public event EventHandler? Chosen;

	[RelayCommand]
	private void TryAgain() => this.Choose(DatabaseChoice.TryAgain);

	[RelayCommand]
	private void StartFresh() => this.Choose(DatabaseChoice.StartFresh);

	[RelayCommand]
	private void Quit() => this.Choose(DatabaseChoice.Quit);

	private void Choose(DatabaseChoice choice)
	{
		this.Choice = choice;
		this.Chosen?.Invoke(this, EventArgs.Empty);
	}
}
