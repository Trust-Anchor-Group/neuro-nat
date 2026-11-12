using Neuro.Nat.App.ViewModels;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Storage;

namespace Neuro.Nat.Desktop.Kiosk;

/// <summary>What NAT runs on when it starts from a config file: everything in memory, filled from the file.</summary>
internal static class KioskSetup
{
	/// <summary>
	/// Stores filled from the config: its accounts with their passwords, and the look and Big screen
	/// settings. After the first sign-in the password is replaced by what the Neuron agreed, in
	/// memory only; the file is never written.
	/// </summary>
	public static Program.HostStores Stores(KioskConfig config)
	{
		InMemoryAccountStore accounts = new();
		InMemoryCredentialVault vault = new();
		List<AccountProfile> profiles = [];
		foreach (KioskAccount a in config.Accounts)
		{
			AccountProfile p = new()
			{
				Host = a.Host,
				Port = a.Port,
				UserName = a.UserName,
				Domain = a.Domain ?? string.Empty,
				WebAddress = a.WebAddress is string w ? AccountWeb.Check(w) : null,
				TrustServerCertificate = a.TrustServerCertificate,
				ConnectOnStartup = true,
				ColorIndex = profiles.Count,
			};
			profiles.Add(p);
			vault.SetAsync(p.Id, AccountSecret.FromPassword(a.Password)).GetAwaiter().GetResult();
		}

		accounts.SaveAsync(profiles).GetAwaiter().GetResult();

		MemoryUiSettingsStore ui = new();
		ui.Save(new UiSettings
		{
			Look = config.Look,
			BigScreenSeconds = config.Seconds,
			BigScreenOff = config.Pages is IReadOnlyList<string> pages ? [.. Known.Where(k => !pages.Contains(k, StringComparer.OrdinalIgnoreCase))] : [],
		});

		return new Program.HostStores(accounts, vault, null, null, new Engine.Alerts.MemoryAlertSettingsStore(), ui, new InMemoryKnownNeuronStore(), KeepsData: false);
	}

	/// <summary>
	/// The kiosk's rule for presence subscriptions: a Neuron whose domain the config lists is accepted
	/// without asking what it is. When the config lists none, real Neurons are accepted as usual. Anything
	/// else is declined (the monitor declines what is not accepted).
	/// </summary>
	public static Func<string, bool?> Rule(KioskConfig config)
		=> bareJid => config.Neurons.Count == 0 ? null
			: config.Neurons.Contains(Engine.Events.NeuronNames.Domain(bareJid).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
				|| config.Neurons.Contains(bareJid.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase) ? true : false;

	/// <summary>The Big screen pages a config can turn off. Neurons always shows.</summary>
	private static readonly string[] Known = [BigScreenPages.Problems, Features.Telemetry.TelemetryFeature.ScreenKey];
}
