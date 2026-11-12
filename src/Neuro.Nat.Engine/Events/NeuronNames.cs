namespace Neuro.Nat.Engine.Events;

/// <summary>
/// The name a Neuron goes by on screen: the roster name when the user gave one, else its domain.
/// Two Neurons that would get the same name show their full addresses instead.
/// </summary>
public static class NeuronNames
{
	/// <summary>The domain of a JID: the part after "@", without a resource. A JID without "@" is a domain already.</summary>
	public static string Domain(string jid)
	{
		if (string.IsNullOrEmpty(jid))
			return string.Empty;

		int slash = jid.IndexOf('/');
		string bare = slash >= 0 ? jid[..slash] : jid;
		int at = bare.IndexOf('@');
		return at >= 0 ? bare[(at + 1)..] : bare;
	}

	/// <summary>The roster name if there is one, else the domain.</summary>
	public static string Of(string? rosterName, string bareJid)
		=> string.IsNullOrWhiteSpace(rosterName) ? Domain(bareJid) : rosterName.Trim();

	/// <summary>The name of one Neuron among the ones known: its full address when another known Neuron would get the same name.</summary>
	public static string For(IEnumerable<WatchedNeuron> known, string bareJid)
	{
		List<WatchedNeuron> all = [.. known];
		WatchedNeuron? self = all.FirstOrDefault(n => string.Equals(n.BareJid, bareJid, StringComparison.OrdinalIgnoreCase));
		string name = Of(self?.Name, bareJid);
		return all.Any(n => !string.Equals(n.BareJid, bareJid, StringComparison.OrdinalIgnoreCase) && string.Equals(Of(n.Name, n.BareJid), name, StringComparison.OrdinalIgnoreCase))
			? bareJid
			: name;
	}

	/// <summary>Names for Neurons shown together, by bare JID. Two different Neurons with the same name get their full addresses.</summary>
	public static IReadOnlyDictionary<string, string> Distinct(IEnumerable<(string? Name, string BareJid)> neurons)
	{
		Dictionary<string, string?> byJid = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string? name, string jid) in neurons)
		{
			if (!byJid.TryGetValue(jid, out string? had) || string.IsNullOrWhiteSpace(had))
				byJid[jid] = name;
		}

		Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
		foreach (IGrouping<string, KeyValuePair<string, string?>> same in byJid.GroupBy(p => Of(p.Value, p.Key), StringComparer.OrdinalIgnoreCase))
		{
			bool clash = same.Count() > 1;
			foreach (KeyValuePair<string, string?> p in same)
				result[p.Key] = clash ? p.Key : same.Key;
		}

		return result;
	}

	/// <summary>Names for the known Neurons, by bare JID.</summary>
	public static IReadOnlyDictionary<string, string> Distinct(IEnumerable<WatchedNeuron> neurons)
		=> Distinct(neurons.Select(n => (n.Name, n.BareJid)));
}
