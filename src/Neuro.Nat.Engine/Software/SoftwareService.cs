using System.Globalization;
using System.Text.RegularExpressions;
using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.Engine.Software;

/// <summary>A software package the Neuron has downloaded from its update service.</summary>
public sealed record SoftwarePackage(string FileName, string Size, string Published, string? Supersedes, string Created, string? Installed, string? From)
{
	public bool IsInstalled => !string.IsNullOrEmpty(this.Installed);
}

/// <summary>How a Neuron updates itself, and what it has.</summary>
public sealed record NeuronSoftware
{
	public required Guid AccountId { get; init; }
	public required string NeuronJid { get; init; }
	public string? BuildTime { get; init; }

	/// <summary>Minutes between receiving a package and installing it; null when automatic updates are off.</summary>
	public int? AutoDelayMinutes { get; init; }

	/// <summary>True when content-only packages install as soon as they arrive.</summary>
	public bool? ContentAutoInstall { get; init; }

	public IReadOnlyList<string> Installed { get; init; } = [];
	public IReadOnlyList<SoftwarePackage> Packages { get; init; } = [];

	/// <summary>The newest "new software downloaded" notification still open, if any.</summary>
	public NeuronNotification? PendingUpdate { get; init; }

	public string? Error { get; init; }
	public DateTime ReadAt { get; init; }
}

/// <summary>
/// Software and updates on the Neurons, through their chat admin (the Neuron has no other
/// interface for it): build time, installed and downloaded packages, the automatic update
/// delay, checking for new packages, cancelling a pending update and upgrading.
/// </summary>
public sealed partial class SoftwareService(MonitorService monitors, ChatService chat)
{
	private static readonly TimeSpan timeout = TimeSpan.FromSeconds(20);

	private ConsoleSession Console(Guid accountId, string neuronJid) => chat.Console(accountId, neuronJid);

	/// <summary>Answers in place of the Neurons, for tests and previews: what a Neuron's software is, and what a check says.</summary>
	internal Func<Guid, string, NeuronSoftware>? ReadStandIn { get; set; }

	internal Func<Guid, string, IReadOnlyList<string>>? CheckStandIn { get; set; }

	/// <summary>Reads everything about one Neuron's software.</summary>
	public async Task<NeuronSoftware> ReadAsync(Guid accountId, string neuronJid)
	{
		if (this.ReadStandIn is not null)
			return this.ReadStandIn(accountId, neuronJid);

		ConsoleSession c = this.Console(accountId, neuronJid);
		try
		{
			string? build = await c.AskAsync("buildtime", timeout);
			if (build is null)
				return new NeuronSoftware { AccountId = accountId, NeuronJid = neuronJid, Error = "The Neuron did not answer.", ReadAt = DateTime.Now };

			IReadOnlyList<string> auto = await c.AskUntilQuietAsync("auto", TimeSpan.FromSeconds(1.5), timeout);
			string? installed = await c.AskAsync("installed", timeout);
			string? packages = await c.AskAsync("packages", timeout);

			(int? delay, bool? content) = ParseAuto(auto);
			return new NeuronSoftware
			{
				AccountId = accountId,
				NeuronJid = neuronJid,
				BuildTime = NotificationClassifier.Plain(build).Trim(',', ' ') is { Length: > 0 } b ? b : null,
				AutoDelayMinutes = delay,
				ContentAutoInstall = content,
				Installed = installed is null ? [] : ParseInstalled(installed),
				Packages = packages is null ? [] : ParsePackages(packages),
				PendingUpdate = this.Pending(accountId, neuronJid),
				ReadAt = DateTime.Now,
			};
		}
		catch (Exception ex)
		{
			return new NeuronSoftware { AccountId = accountId, NeuronJid = neuronJid, Error = ex.Message, ReadAt = DateTime.Now };
		}
	}

	/// <summary>Asks the Neuron's update service for new packages. Returns the Neuron's progress messages.</summary>
	public async Task<IReadOnlyList<string>> CheckAsync(Guid accountId, string neuronJid)
		=> this.CheckStandIn is not null ? this.CheckStandIn(accountId, neuronJid) : Plain(await this.Console(accountId, neuronJid).AskUntilQuietAsync("check", TimeSpan.FromSeconds(4), TimeSpan.FromMinutes(3)));

	/// <summary>Sets the automatic update delay in minutes; 0 turns automatic updates off.</summary>
	public async Task<string> SetAutoDelayAsync(Guid accountId, string neuronJid, int minutes)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(minutes);
		return string.Join(" ", Plain(await this.Console(accountId, neuronJid).AskUntilQuietAsync(
			"auto " + minutes.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(1.5), timeout)));
	}

	/// <summary>Whether content-only packages install as soon as they arrive.</summary>
	public async Task<string> SetContentAutoAsync(Guid accountId, string neuronJid, bool on)
		=> string.Join(" ", Plain(await this.Console(accountId, neuronJid).AskUntilQuietAsync(
			"auto content " + (on ? "1" : "0"), TimeSpan.FromSeconds(1.5), timeout)));

	/// <summary>Cancels an automatic update that is waiting to install.</summary>
	public async Task<string> CancelPendingAsync(Guid accountId, string neuronJid)
		=> NotificationClassifier.Plain(await this.Console(accountId, neuronJid).AskAsync("cancel", timeout) ?? "The Neuron did not answer.");

	/// <summary>
	/// Upgrades the Neuron (all packages, or one), with a backup first unless told not to. The
	/// Neuron restarts when it is done, so the answer may be cut short.
	/// </summary>
	public async Task<IReadOnlyList<string>> UpgradeAsync(Guid accountId, string neuronJid, string? package, bool backup)
	{
		string command = "upgrade" + (backup ? string.Empty : " nobackup") + (string.IsNullOrWhiteSpace(package) ? string.Empty : " " + package.Trim());
		return Plain(await this.Console(accountId, neuronJid).AskUntilQuietAsync(command, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10)));
	}

	private NeuronNotification? Pending(Guid accountId, string neuronJid)
		=> monitors.Attention.Items
			.Where(n => n.AccountId == accountId && string.Equals(n.NeuronJid, neuronJid, StringComparison.OrdinalIgnoreCase) &&
				n.Kind == NotificationKind.SoftwareUpdate && n.State != AttentionState.Handled)
			.OrderByDescending(n => n.Received)
			.FirstOrDefault();

	private static List<string> Plain(IEnumerable<string> lines) => [.. lines.Select(NotificationClassifier.Plain).Where(s => s.Length > 0)];

	// ---- Reading the answers ---------------------------------------------------------------

	/// <summary>"auto": the delay line and the content line.</summary>
	internal static (int? Delay, bool? Content) ParseAuto(IEnumerable<string> answers)
	{
		int? delay = null;
		bool? content = null;
		foreach (string raw in answers)
		{
			string a = NotificationClassifier.Plain(raw);
			if (a.Contains("not activated", StringComparison.OrdinalIgnoreCase))
				delay = null;
			else if (Delay().Match(a) is { Success: true } m)
				delay = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

			if (a.Contains("content-only packages will be installed when received", StringComparison.OrdinalIgnoreCase))
				content = true;
			else if (a.Contains("content-only packages are processed as other packages", StringComparison.OrdinalIgnoreCase))
				content = false;
		}

		return (delay, content);
	}

	/// <summary>"installed": file names separated by semicolons, or "-" for none.</summary>
	internal static IReadOnlyList<string> ParseInstalled(string answer)
	{
		string a = NotificationClassifier.Plain(answer).Trim();
		return a.Length == 0 || a == "-" ? [] : [.. a.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
	}

	/// <summary>"packages": a Markdown table.</summary>
	internal static IReadOnlyList<SoftwarePackage> ParsePackages(string answer)
	{
		List<SoftwarePackage> list = [];
		foreach (IReadOnlyDictionary<string, string> r in MarkdownTable.Records(answer))
		{
			string file = r.GetValueOrDefault("File Name") ?? string.Empty;
			if (file.Length == 0)
				continue;

			static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
			list.Add(new SoftwarePackage(file, r.GetValueOrDefault("Size") ?? string.Empty, r.GetValueOrDefault("Published") ?? string.Empty,
				N(r.GetValueOrDefault("Supersedes")), r.GetValueOrDefault("Created") ?? string.Empty, N(r.GetValueOrDefault("Installed")),
				N(r.GetValueOrDefault("Remote Endpoint"))));
		}

		return list;
	}

	[GeneratedRegex(@"installed\s+(?<n>\d+)\s+minutes", RegexOptions.IgnoreCase)]
	private static partial Regex Delay();
}
