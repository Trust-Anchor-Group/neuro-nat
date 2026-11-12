using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Waher.Persistence.Files;

namespace Neuro.Nat.Engine.Storage;

/// <summary>What happened when NAT tried to open its database.</summary>
/// <param name="Database">The open database, or null when it could not be opened.</param>
/// <param name="Problem">Why not, in a line for the log. Never anything from the database itself.</param>
public sealed record DatabaseOpening(FilesProvider? Database, string? Problem = null, Exception? Error = null)
{
	public bool IsOpen => this.Database is not null;
}

/// <summary>
/// Opens the encrypted database that holds everything NAT keeps, and sets it aside when the user
/// chooses to start fresh. A wrong key is found before the database files are touched: a failed
/// open leaves them locked until the process ends.
/// </summary>
public static class LocalDatabase
{
	/// <summary>The words the user sees when the database cannot be opened.</summary>
	public const string CannotOpen = "NAT cannot open what it has saved on this computer.";

	/// <summary>What the user can do about it, in plain words.</summary>
	public const string CannotOpenDetail =
		"Try again after restarting the computer, or start fresh: what was saved is moved aside, and nothing is deleted.";

	/// <summary>A file in the database folder that tells whether a key is the right one. It holds a keyed hash and nothing else.</summary>
	public const string KeyCheckFile = "key.check";

	/// <summary>Opens the database in <paramref name="folder"/> with <paramref name="key"/>, creating it when there is none.</summary>
	public static async Task<DatabaseOpening> OpenAsync(string folder, byte[] key)
	{
		string checkFile = Path.Combine(folder, KeyCheckFile);
		string check = KeyCheck(key);
		bool hasFiles = Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();

		if (File.Exists(checkFile))
		{
			string saved;
			try
			{
				saved = (await File.ReadAllTextAsync(checkFile)).Trim();
			}
			catch (Exception ex)
			{
				return new DatabaseOpening(null, "The key check of the database could not be read.", ex);
			}

			if (!string.Equals(saved, check, StringComparison.Ordinal))
				return new DatabaseOpening(null, "The database key does not match the database.");
		}

		FilesProvider db;
		try
		{
			db = await EngineRuntime.OpenDatabaseAsync(folder, key);
		}
		catch (Exception ex)
		{
			return new DatabaseOpening(null, hasFiles ? "The database files could not be read." : "The database could not be created.", ex);
		}

		if (!File.Exists(checkFile))
		{
			try
			{
				await File.WriteAllTextAsync(checkFile, check);
			}
			catch (Exception ex)
			{
				await db.Stop();
				return new DatabaseOpening(null, "The key check of the database could not be written.", ex);
			}
		}

		return new DatabaseOpening(db);
	}

	/// <summary>
	/// Moves an unreadable database aside, into a dated folder beside it, so NAT can start fresh.
	/// Deletes nothing. Returns where it went, or null when there was nothing to move.
	/// </summary>
	public static string? SetAside(string folder, DateTime now)
	{
		if (!Directory.Exists(folder))
			return null;

		string stamp = now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
		string target = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + " unreadable " + stamp;
		for (int i = 2; Directory.Exists(target) || File.Exists(target); i++)
			target = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + " unreadable " + stamp + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";

		Directory.Move(folder, target);
		return target;
	}

	private static string KeyCheck(byte[] key)
		=> Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("NAT database key check v1")));
}
