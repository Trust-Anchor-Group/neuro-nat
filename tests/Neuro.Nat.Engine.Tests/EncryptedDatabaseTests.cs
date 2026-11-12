using System.Security.Cryptography;
using System.Text;
using Waher.Runtime.Settings.SettingObjects;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// The database that keeps signing keys: what goes in comes back with the same key, and nothing
/// readable is on disk.
/// </summary>
public class EncryptedDatabaseTests
{
	[Fact]
	public async Task KeepsObjectsEncryptedBetweenRuns()
	{
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.Db." + Guid.NewGuid().ToString("N"));
		byte[] key = RandomNumberGenerator.GetBytes(32);
		string marker = "private-key-" + Guid.NewGuid().ToString("N");

		try
		{
			var db = await EngineRuntime.OpenDatabaseAsync(dir, key);
			// Signing keys are kept as runtime settings, so store one the same way.
			await db.Insert(new StringSetting("Nat.Test.Key", marker));
			await db.Stop();

			byte[] needle = Encoding.UTF8.GetBytes(marker);
			foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
			{
				byte[] content = await File.ReadAllBytesAsync(file);
				Assert.True(content.AsSpan().IndexOf(needle) < 0, "Readable secret in " + Path.GetFileName(file));
			}

			db = await EngineRuntime.OpenDatabaseAsync(dir, key);
			IEnumerable<StringSetting> found = await db.Find<StringSetting>(0, int.MaxValue);
			Assert.Contains(found, r => r.Value == marker);
			await db.Stop();

			// Another key reads nothing useful.
			bool readWithWrongKey;
			try
			{
				db = await EngineRuntime.OpenDatabaseAsync(dir, RandomNumberGenerator.GetBytes(32));
				readWithWrongKey = (await db.Find<StringSetting>(0, int.MaxValue)).Any(r => r.Value == marker);
				await db.Stop();
			}
			catch (Exception)
			{
				readWithWrongKey = false;
			}

			Assert.False(readWithWrongKey);
		}
		finally
		{
			try
			{
				Directory.Delete(dir, true);
			}
			catch (IOException)
			{
			}
		}
	}

	[Fact]
	public async Task RefusesShortKeys()
	{
		EngineRuntime.Initialize();
		await Assert.ThrowsAsync<ArgumentException>(() => EngineRuntime.OpenDatabaseAsync(Path.GetTempPath(), new byte[16]));
	}
}
