using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Concentrator;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Runtime.Inventory;

namespace Neuro.Nat.Engine;

/// <summary>
/// One-time initialization of the Waher runtime. The XMPP library discovers SASL mechanisms,
/// extensions and content codecs through <see cref="Types"/>, so it must know our assemblies
/// before the first client is created.
/// </summary>
public static class EngineRuntime
{
	private static readonly object initLock = new();
	private static bool typesInitialized;
	private static bool initialized;
	private static FilesProvider? database;

	/// <summary>True when an encrypted database keeps signing keys and identity state between runs.</summary>
	public static bool KeepsKeys => database is not null;

	/// <summary>
	/// Initializes the runtime without a database: anything the Waher runtime would persist
	/// (encryption counters, signing keys) lives for this run only. Used by tests, and on
	/// platforms where NAT has no vault for the database key yet. Safe to call more than once.
	/// </summary>
	/// <param name="additionalAssemblies">Extra assemblies (for example the UI) that contain
	/// types the Waher runtime should be able to find.</param>
	public static void Initialize(params Assembly[] additionalAssemblies)
	{
		lock (initLock)
		{
			if (initialized)
				return;

			InitializeTypes(additionalAssemblies);

			if (!Database.HasProvider)
				Database.Register(new NullDatabaseProvider());

			initialized = true;
		}
	}

	/// <summary>
	/// Initializes the runtime with an encrypted object database in <paramref name="databaseFolder"/>.
	/// It keeps legal identity signing keys, approved identity state and encryption counters between
	/// runs. Every file is encrypted with keys derived from <paramref name="databaseKey"/>, which the
	/// host keeps in the operating system's vault.
	/// </summary>
	public static async Task InitializeAsync(string databaseFolder, byte[] databaseKey, params Assembly[] additionalAssemblies)
	{
		lock (initLock)
		{
			if (initialized)
				return;

			InitializeTypes(additionalAssemblies);
		}

		FilesProvider db = await OpenDatabaseAsync(databaseFolder, databaseKey);

		lock (initLock)
		{
			if (initialized || Database.HasProvider)
			{
				_ = db.DisposeAsync();
				initialized = true;
				return;
			}

			Database.Register(db);
			database = db;
			initialized = true;
		}
	}

	/// <summary>Writes pending database changes and closes it. Call once, when the app exits.</summary>
	public static async Task ShutdownAsync()
	{
		FilesProvider? db;
		lock (initLock)
		{
			db = database;
			database = null;
		}

		if (db is not null)
			await db.Stop();
	}

	/// <summary>
	/// Opens (or creates) the encrypted database. Each file gets its own key and IV, derived from
	/// the database key and the file's name, so the folder is unreadable without the key.
	/// </summary>
	// These two must stay "Neuro.AA ..." after the rename: every file of the existing database is
	// encrypted with keys derived from them. Pinned by a test.
	internal const string FileKeyLabel = "Neuro.AA database key:";
	internal const string FileIvLabel = "Neuro.AA database iv:";

	internal static Task<FilesProvider> OpenDatabaseAsync(string folder, byte[] databaseKey)
	{
		if (databaseKey.Length < 32)
			throw new ArgumentException("The database key must be at least 32 bytes.", nameof(databaseKey));

		byte[] key = (byte[])databaseKey.Clone();
		Directory.CreateDirectory(folder);

		return FilesProvider.CreateAsync(folder, "Default", 8192, 1000, 8192, Encoding.UTF8, 10000, fileName =>
		{
			string name = Path.GetFileName(fileName).ToLowerInvariant();
			byte[] k = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(FileKeyLabel + name));
			byte[] iv = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(FileIvLabel + name));
			return Task.FromResult(new KeyValuePair<byte[], byte[]>(k, iv));
		});
	}

	private static void InitializeTypes(Assembly[] additionalAssemblies)
	{
		if (typesInitialized)
			return;

		List<Assembly> assemblies =
		[
			typeof(EngineRuntime).Assembly,
			typeof(Types).Assembly,
			typeof(XmppClient).Assembly,
			typeof(ConcentratorClient).Assembly,
			typeof(Waher.Events.Log).Assembly,
			typeof(Database).Assembly,
			typeof(FilesProvider).Assembly,
			typeof(Waher.Persistence.Serialization.ObjectSerializer).Assembly,

			// End-to-end encryption: peers' keys are parsed into endpoint types found through Types.
			typeof(Waher.Networking.XMPP.P2P.EndpointSecurity).Assembly,
			typeof(Waher.Security.E2EE.E2eEndpoint).Assembly,
			typeof(Waher.Security.EllipticCurves.E2EE.Edwards25519Endpoint).Assembly,
			typeof(Waher.Networking.XMPP.MUC.MultiUserChatClient).Assembly,
			typeof(Waher.Networking.XMPP.Contracts.ContractsClient).Assembly,
		];

		try
		{
			assemblies.Add(Assembly.Load("Waher.Security.PQC"));
		}
		catch (Exception)
		{
			// Post-quantum keys are optional.
		}

		foreach (Assembly a in additionalAssemblies)
		{
			if (!assemblies.Contains(a))
				assemblies.Add(a);
		}

		// The database stores objects from many Waher assemblies (runtime settings hold the signing
		// keys, runtime counters number encrypted messages). Objects are only read back when Types
		// knows their assembly, so include every Waher assembly the ones above depend on.
		HashSet<string> seen = [.. assemblies.Select(a => a.GetName().Name ?? string.Empty)];
		Queue<Assembly> pending = new(assemblies);
		while (pending.TryDequeue(out Assembly? a))
		{
			foreach (AssemblyName r in a.GetReferencedAssemblies())
			{
				if (r.Name is not string name || !name.StartsWith("Waher.", StringComparison.Ordinal) || !seen.Add(name))
					continue;

				try
				{
					Assembly loaded = Assembly.Load(r);
					assemblies.Add(loaded);
					pending.Enqueue(loaded);
				}
				catch (Exception)
				{
					// A reference the app never uses at run time.
				}
			}
		}

		if (!Types.IsInitialized)
			Types.Initialize([.. assemblies]);

		typesInitialized = true;
	}
}
