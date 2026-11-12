namespace Neuro.Nat.Desktop;

/// <summary>
/// Where NAT keeps its data: %AppData%\Neuro.Nat, or the folder named by <see cref="OverrideVariable"/>.
/// </summary>
internal static class DataFolder
{
	/// <summary>Names a data folder to use instead of the usual one, for example a copy to try the app against.</summary>
	public const string OverrideVariable = "NAT_DATA_FOLDER";

	/// <summary>The data folder's name under %AppData%.</summary>
	public const string FolderName = "Neuro.Nat";

	/// <summary>The log file's name in the data folder's logs folder.</summary>
	public const string LogFileName = "nat.log";

	/// <summary>The folder named by <see cref="OverrideVariable"/>, or null when it is not set.</summary>
	public static string? Override
		=> Environment.GetEnvironmentVariable(OverrideVariable) is string s && !string.IsNullOrWhiteSpace(s) ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(s.Trim())) : null;
}

/// <summary>What <see cref="DataFolderMove.Run"/> did.</summary>
internal enum DataFolderMoveOutcome
{
	/// <summary>There was no old folder: nothing to do.</summary>
	NothingToMove,

	/// <summary>The old folder now has the new name.</summary>
	Moved,

	/// <summary>Both folders exist: the new one is used and the old one is left alone.</summary>
	BothExist,

	/// <summary>The old folder could not be moved, most likely because the old app still runs. Nothing was changed.</summary>
	Failed,
}

/// <param name="Outcome">What was done.</param>
/// <param name="Note">A line for the log, when there is something to say.</param>
/// <param name="Error">Why the move failed, when it did.</param>
internal sealed record DataFolderMoveResult(DataFolderMoveOutcome Outcome, string? Note = null, Exception? Error = null);

/// <summary>
/// Moves the data folder the app used before it was called NAT (%AppData%\Neuro.AA) to its new
/// name, once, before the log file is opened. Everything in it comes along: accounts, secrets,
/// the database, settings and update-source.txt. Takes both paths, so tests never touch the real
/// folders.
/// </summary>
internal static class DataFolderMove
{
	/// <summary>The data folder's name under %AppData% before the rename. Only the move reads it.</summary>
	public const string OldFolderName = "Neuro.AA";

	/// <summary>The log file's name before the rename. Only the move reads it.</summary>
	public const string OldLogFileName = "neuro.aa.log";

	public static DataFolderMoveResult Run(string oldFolder, string newFolder)
	{
		if (!Directory.Exists(oldFolder))
			return new DataFolderMoveResult(DataFolderMoveOutcome.NothingToMove);

		if (Directory.Exists(newFolder))
			return new DataFolderMoveResult(DataFolderMoveOutcome.BothExist, $"Both {oldFolder} and {newFolder} exist; using {newFolder} and leaving {oldFolder} alone.");

		try
		{
			// A rename on the same disk: all of it moves, or nothing does.
			Directory.Move(oldFolder, newFolder);
		}
		catch (Exception ex)
		{
			return new DataFolderMoveResult(DataFolderMoveOutcome.Failed, $"Could not move {oldFolder} to {newFolder}.", ex);
		}

		string note = $"Moved the data folder from {oldFolder} to {newFolder}.";
		string oldLog = Path.Combine(newFolder, "logs", OldLogFileName);
		string newLog = Path.Combine(newFolder, "logs", DataFolder.LogFileName);
		if (File.Exists(oldLog) && !File.Exists(newLog))
		{
			try
			{
				File.Move(oldLog, newLog);
			}
			catch (Exception ex)
			{
				note += $" The old log file keeps its name: {ex.Message}";
			}
		}

		return new DataFolderMoveResult(DataFolderMoveOutcome.Moved, note);
	}
}
