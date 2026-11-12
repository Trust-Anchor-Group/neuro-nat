using System.Globalization;
using System.Text.RegularExpressions;
using Neuro.Nat.Engine.Concentrator;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Software;
using Waher.Networking.XMPP.Concentrator;
using Waher.Networking.XMPP.DataForms;
using Waher.Networking.XMPP.DataForms.FieldTypes;
using Waher.Things.DisplayableParameters;

namespace Neuro.Nat.Engine.Compare;

/// <summary>Reads what a comparison compares from one Neuron. Tests hand in snapshots instead.</summary>
public interface INeuronReader
{
	Task<NeuronSnapshot> ReadAsync(Guid accountId, string neuronJid, CancellationToken cancel = default);
}

/// <summary>
/// Reads a Neuron for a comparison, with the operator's own rights and nothing the Neuron does
/// not already offer an administrator:
/// <list type="bullet">
/// <item>Software: what the Software tab reads through the chat admin (build time, installed
/// packages and their versions, automatic updates), and the version its client reports.</item>
/// <item>Settings: the Neuron's <c>GatewayConfig</c> data source, which mirrors Gateway.config:
/// the <c>Ports</c> node and its ports, the <c>WebServer</c> node and its children (proxy domains
/// and default pages per host count as domains), <c>LoginAuditor</c> (the sign-in guard) and
/// <c>EventSinks</c> (where events are sent). Each node's values are its edit form, opened and
/// cancelled, as Copy in Sources does; its displayable parameters when it has no form.</item>
/// <item>State now: CPU, memory and disk from the last health reading, and the number of
/// signed-in clients from the <c>C2S</c> node of the <c>Connections</c> data source.</item>
/// </list>
/// </summary>
public sealed partial class LiveNeuronReader(MonitorService monitors, SoftwareService software, FleetService fleet) : INeuronReader
{
	/// <summary>The data source that mirrors Gateway.config.</summary>
	public const string ConfigSourceId = "GatewayConfig";

	/// <summary>The data source with the connections, and the node with the clients.</summary>
	public const string ConnectionsSourceId = "Connections";
	public const string ClientsNodeId = "C2S";

	/// <summary>The nodes of Gateway.config that are compared, and the heading each goes under.</summary>
	public static readonly IReadOnlyDictionary<string, string> Sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["Ports"] = "Ports",
		["WebServer"] = "Web server",
		["LoginAuditor"] = "Sign-in guard",
		["EventSinks"] = "Where events are sent",
	};

	public const string DomainsSection = "Domains";

	private const int MaxNodes = 300;

	public async Task<NeuronSnapshot> ReadAsync(Guid accountId, string neuronJid, CancellationToken cancel = default)
	{
		WatchedNeuron? w = monitors.For(accountId)?.Neurons.FirstOrDefault(n => string.Equals(n.BareJid, neuronJid, StringComparison.OrdinalIgnoreCase));
		DateTime now = DateTime.UtcNow;
		if (w?.State == WatchState.Forbidden)
			return new NeuronSnapshot { NeuronJid = neuronJid, Name = w.Name, ReadAt = now, NoAccess = true };

		Dictionary<CompareGroup, string> notRead = [];
		List<SnapshotValue> values = [];
		if (w?.State != WatchState.Live)
		{
			string why = w is null ? "Not among your Neurons" : "Not connected";
			foreach (CompareGroup g in Enum.GetValues<CompareGroup>())
				notRead[g] = why;
			return new NeuronSnapshot { NeuronJid = neuronJid, Name = w?.Name, ReadAt = now, NotRead = notRead };
		}

		NeuronConcentrator? c = monitors.For(accountId)?.Concentrator(neuronJid);
		Task<NeuronSoftware> sw = software.ReadAsync(accountId, neuronJid);
		Task<(List<SnapshotValue> Values, string? Error)> settings = c is null ? Task.FromResult<(List<SnapshotValue>, string?)>(([], "Not connected")) : ReadSettingsAsync(c, cancel);
		Task<string?> clients = c is null ? Task.FromResult<string?>(null) : ReadClientsAsync(c);

		NeuronSoftware s = await sw;
		if (s.Error is string e)
			notRead[CompareGroup.Software] = e;
		else
			values.AddRange(SoftwareValues(s, fleet.Get(accountId, neuronJid)?.Software));

		(List<SnapshotValue> configValues, string? configError) = await settings;
		if (configError is not null)
			notRead[CompareGroup.Settings] = configError;
		values.AddRange(configValues);

		values.AddRange(StateValues(fleet.Get(accountId, neuronJid), await clients));
		return new NeuronSnapshot { NeuronJid = neuronJid, Name = w.Name, ReadAt = now, Values = values, NotRead = notRead };
	}

	// ----- Software -----

	public const string SoftwareSection = "Software";
	public const string PackagesSection = "Installed packages";

	/// <summary>What the Software tab shows, as values to compare.</summary>
	public static IEnumerable<SnapshotValue> SoftwareValues(NeuronSoftware s, string? version)
	{
		const CompareGroup g = CompareGroup.Software;
		if (!string.IsNullOrWhiteSpace(version))
			yield return new SnapshotValue(g, SoftwareSection, "software/version", "Version", version.Trim());
		if (s.BuildTime is string built)
			yield return new SnapshotValue(g, SoftwareSection, "software/built", "Built", built);
		yield return new SnapshotValue(g, SoftwareSection, "software/auto", "Automatic updates",
			s.AutoDelayMinutes is int m ? $"After {m.ToString(CultureInfo.InvariantCulture)} min" : "Off");
		if (s.ContentAutoInstall is bool content)
			yield return new SnapshotValue(g, SoftwareSection, "software/content", "Content packages", content ? "Install when received" : "Like other packages");

		// The installed version of each package: the newest installed file of that name.
		Dictionary<string, (string Version, string Installed)> installed = new(StringComparer.OrdinalIgnoreCase);
		foreach (SoftwarePackage p in s.Packages.Where(p => p.IsInstalled))
		{
			(string name, string ver) = SplitPackage(p.FileName);
			// Without a version in its name, a package is told apart by when it was published.
			if (ver.Length == 0 && !string.IsNullOrWhiteSpace(p.Published))
				ver = "Published " + p.Published.Trim();
			if (!installed.TryGetValue(name, out (string Version, string Installed) had) || string.CompareOrdinal(p.Installed, had.Installed) > 0)
				installed[name] = (ver, p.Installed ?? string.Empty);
		}

		foreach (string file in s.Installed)
		{
			(string name, string ver) = SplitPackage(file);
			if (!installed.ContainsKey(name))
				installed[name] = (ver.Length > 0 ? ver : "Installed", string.Empty);
		}

		foreach (KeyValuePair<string, (string Version, string Installed)> p in installed.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
			yield return new SnapshotValue(g, PackagesSection, "software/package/" + p.Key.ToLowerInvariant(), p.Key, p.Value.Version.Length > 0 ? p.Value.Version : "Installed");
	}

	/// <summary>"TAG.Neuron.3.11.2.package" is TAG.Neuron, version 3.11.2. Without a version, the name alone.</summary>
	internal static (string Name, string Version) SplitPackage(string fileName)
	{
		string f = fileName.Trim();
		if (f.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
			f = f[..^".package".Length];

		Match m = Versioned().Match(f);
		return m.Success ? (m.Groups["n"].Value, m.Groups["v"].Value) : (f, string.Empty);
	}

	[GeneratedRegex(@"^(?<n>.+?)\.(?<v>\d+(\.\d+)+)$")]
	private static partial Regex Versioned();

	// ----- Settings -----

	private static async Task<(List<SnapshotValue>, string?)> ReadSettingsAsync(NeuronConcentrator c, CancellationToken cancel)
	{
		List<SnapshotValue> values = [];
		try
		{
			int budget = MaxNodes;
			foreach (NodeInformation root in await c.GetRootNodesAsync(ConfigSourceId))
			{
				cancel.ThrowIfCancellationRequested();
				if (!Sections.TryGetValue(root.NodeId, out string? section))
					continue;

				await ReadNodeAsync(c, root, root.NodeId, section, root.NodeId, true, values, () => --budget >= 0, cancel);
			}

			return (values, null);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return (values, values.Count == 0 ? ex.Message : null);
		}
	}

	private static async Task ReadNodeAsync(NeuronConcentrator c, NodeInformation node, string rootId, string section, string path, bool isRoot,
		List<SnapshotValue> values, Func<bool> take, CancellationToken cancel)
	{
		if (!take())
			return;

		string id = string.IsNullOrEmpty(node.LocalId) ? node.NodeId : node.LocalId;
		if (!isRoot && string.Equals(rootId, "WebServer", StringComparison.OrdinalIgnoreCase) &&
			(node.NodeType.Contains("ProxyDomain", StringComparison.OrdinalIgnoreCase) || node.NodeType.Contains("DefaultPage", StringComparison.OrdinalIgnoreCase)))
		{
			section = DomainsSection;
		}

		IReadOnlyList<(string Var, string Label, string Value, bool Private)> fields = await FieldsAsync(c, node);
		values.AddRange(SettingValues(rootId, section, path, isRoot ? null : id, fields));

		if (!node.HasChildren)
			return;

		foreach (NodeInformation child in await c.GetChildNodesAsync(node))
		{
			cancel.ThrowIfCancellationRequested();
			await ReadNodeAsync(c, child, rootId, section, path + "/" + child.NodeId, false, values, take, cancel);
		}
	}

	/// <summary>
	/// The values of one node. A port is one value, "Port 5222" with its protocol; every other
	/// field is named by the node it is on and its own label. Secrets keep only their hash.
	/// </summary>
	public static IEnumerable<SnapshotValue> SettingValues(string rootId, string section, string path, string? nodeLabel,
		IReadOnlyList<(string Var, string Label, string Value, bool Private)> fields)
	{
		const CompareGroup g = CompareGroup.Settings;
		if (string.Equals(rootId, "Ports", StringComparison.OrdinalIgnoreCase) && nodeLabel is not null)
		{
			string protocol = fields.FirstOrDefault(f => f.Var.Equals("Protocol", StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
			yield return new SnapshotValue(g, section, "settings/" + path, "Port " + nodeLabel, protocol);
			yield break;
		}

		foreach ((string var, string label, string value, bool isPrivate) in fields)
		{
			string name = Clean(label.Length > 0 ? label : var);
			string shown = nodeLabel is null ? name : nodeLabel + ", " + name;
			yield return SnapshotValue.Of(g, section, "settings/" + path + "/" + var, shown, value, isPrivate || Secrets.IsSecret(var, label));
		}
	}

	private static string Clean(string label) => label.Trim().TrimEnd(':', '.').Trim();

	private static async Task<IReadOnlyList<(string, string, string, bool)>> FieldsAsync(NeuronConcentrator c, NodeInformation node)
	{
		List<(string, string, string, bool)> list = [];
		try
		{
			FormSession edit = await c.EditAsync(node);
			foreach (Field f in edit.Form.Fields)
			{
				if (f is FixedField or HiddenField || string.IsNullOrEmpty(f.Var))
					continue;
				list.Add((f.Var, f.Label ?? string.Empty, string.Join(", ", f.ValueStrings ?? []), f is TextPrivateField));
			}

			if (edit.Form.CanCancel)
				await edit.Form.Cancel();
		}
		catch (Exception)
		{
			// No form for this node: what it shows of itself.
		}

		if (list.Count == 0 && node.ParameterList is Parameter[] parameters)
		{
			foreach (Parameter p in parameters)
				list.Add((p.Id, p.Name ?? string.Empty, Convert.ToString(p.StringValue, CultureInfo.InvariantCulture) ?? string.Empty, false));
		}

		return list;
	}

	// ----- State now -----

	public const string StateSection = "Now";

	/// <summary>How the Neuron is doing now: never counted as a difference.</summary>
	public static IEnumerable<SnapshotValue> StateValues(NeuronHealth? h, string? clients)
	{
		const CompareGroup g = CompareGroup.StateNow;
		static string Mb(double mb) => mb >= 1024 ? $"{(mb / 1024).ToString("0.0", CultureInfo.CurrentCulture)} GB" : $"{mb.ToString("0", CultureInfo.CurrentCulture)} MB";

		yield return new SnapshotValue(g, StateSection, "state/cpu", "Processor", h?.Cpu.Latest is double cpu ? cpu.ToString("0", CultureInfo.CurrentCulture) + " %" : "-");
		yield return new SnapshotValue(g, StateSection, "state/memory", "Memory used", h?.AllocatedMemory.Latest is double a ? Mb(a) : "-");
		yield return new SnapshotValue(g, StateSection, "state/free-memory", "Memory free", h?.FreeMemory.Latest is double f ? Mb(f) : "-");
		yield return new SnapshotValue(g, StateSection, "state/disk", "Disk free", h?.FreeDisk.Latest is double d ? d.ToString("0.0", CultureInfo.CurrentCulture) + " GB" : "-");
		yield return new SnapshotValue(g, StateSection, "state/clients", "Signed-in clients", clients ?? "-");
	}

	private static async Task<string?> ReadClientsAsync(NeuronConcentrator c)
	{
		try
		{
			NodeInformation? node = (await c.GetRootNodesAsync(ConnectionsSourceId)).FirstOrDefault(n => n.NodeId == ClientsNodeId);
			return Convert.ToString(node?.ParameterList?.FirstOrDefault(p => p.Id == "NrConnections")?.StringValue, CultureInfo.InvariantCulture);
		}
		catch (Exception)
		{
			return null;
		}
	}
}
