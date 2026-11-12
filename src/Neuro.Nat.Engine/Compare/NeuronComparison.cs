using System.Globalization;
using System.Text;

namespace Neuro.Nat.Engine.Compare;

/// <summary>How the two sides of one row compare.</summary>
public enum RowKind
{
	Same,
	Different,

	/// <summary>Only the left Neuron has it.</summary>
	OnlyLeft,

	/// <summary>Only the right Neuron has it.</summary>
	OnlyRight,

	/// <summary>One side could not be read, so it cannot be compared. Not counted.</summary>
	NotRead,

	/// <summary>State now: shown beside each other, never counted.</summary>
	Background,
}

/// <summary>One row of a comparison: what it is, and what each side has. A secret shows only "Same" or "Different".</summary>
public sealed record CompareRow(CompareGroup Group, string Section, string Key, string Label, string Left, string Right, RowKind Kind, bool IsSecret = false)
{
	public bool IsDifference => this.Kind is RowKind.Different or RowKind.OnlyLeft or RowKind.OnlyRight;
}

/// <summary>Two Neurons, or a Neuron and a kept copy of it, side by side.</summary>
public sealed record Comparison(NeuronSnapshot Left, NeuronSnapshot Right, string LeftName, string RightName, IReadOnlyList<CompareRow> Rows, IReadOnlyList<string> Notes)
{
	/// <summary>Rows that differ. State now never counts.</summary>
	public int Differences => this.Rows.Count(r => r.IsDifference);

	/// <summary>The headings (such as "Ports" or "Installed packages") with at least one difference.</summary>
	public int GroupsWithDifferences => this.Rows.Where(r => r.IsDifference).Select(r => (r.Group, r.Section)).Distinct().Count();

	/// <summary>"14 differences in 3 groups", or "No differences".</summary>
	public string CountText => this.Differences == 0
		? "No differences"
		: $"{Count(this.Differences, "difference")} in {Count(this.GroupsWithDifferences, "group")}";

	/// <summary>The comparison as plain text, for the clipboard or a file.</summary>
	public string ToText(bool onlyDifferences)
	{
		StringBuilder sb = new();
		sb.Append(this.LeftName).Append(" and ").AppendLine(this.RightName);
		sb.Append(this.LeftName).Append(" read ").AppendLine(When(this.Left.ReadAt));
		sb.Append(this.RightName).Append(this.Right.CopyId is null ? " read " : " kept ").AppendLine(When(this.Right.ReadAt));
		sb.AppendLine(this.CountText);
		foreach (string note in this.Notes)
			sb.AppendLine(note);

		foreach (IGrouping<CompareGroup, CompareRow> group in this.Rows.GroupBy(r => r.Group))
		{
			List<CompareRow> rows = [.. group.Where(r => !onlyDifferences || r.IsDifference || r.Kind == RowKind.Background)];
			if (rows.Count == 0)
				continue;

			sb.AppendLine().AppendLine(GroupName(group.Key));
			string? section = null;
			foreach (CompareRow r in rows)
			{
				if (r.Section != section)
				{
					section = r.Section;
					sb.Append("  ").AppendLine(section);
				}

				sb.Append("    ").Append(r.Label).Append(": ");
				sb.AppendLine(r.Kind switch
				{
					RowKind.OnlyLeft => $"{r.Left} (only on {this.LeftName})",
					RowKind.OnlyRight => $"{r.Right} (only on {this.RightName})",
					RowKind.Same => r.Left,
					_ => $"{Blank(r.Left)} | {Blank(r.Right)}",
				});
			}
		}

		return sb.ToString();
	}

	public static string GroupName(CompareGroup g) => g switch
	{
		CompareGroup.Software => "Software",
		CompareGroup.Settings => "Settings",
		_ => "State now",
	};

	private static string Blank(string s) => s.Length == 0 ? "-" : s;

	private static string When(DateTime utc) => utc == default ? "-" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

	private static string Count(int n, string one) => n == 1 ? "1 " + one : n.ToString(CultureInfo.CurrentCulture) + " " + one + "s";
}

/// <summary>Lays two snapshots side by side.</summary>
public static class NeuronComparer
{
	public const string NotReadText = "Not read";
	public const string SameText = "Same";
	public const string DifferentText = "Different";
	public const string SetText = "Set";

	/// <summary>
	/// Compares two snapshots value by value, by key. Secrets are compared by their hashes and
	/// never shown. State now is laid out but never counted. A group one side could not read is
	/// shown from the other side and not counted either.
	/// </summary>
	public static Comparison Compare(NeuronSnapshot left, NeuronSnapshot right, string leftName, string rightName)
	{
		List<string> notes = [];
		Note(left, leftName, notes);
		Note(right, rightName, notes);

		Dictionary<string, SnapshotValue> l = Index(left.Values);
		Dictionary<string, SnapshotValue> r = Index(right.Values);

		// The order things were read in: the left side's, then what only the right side has.
		List<string> keys = [.. l.Keys];
		keys.AddRange(r.Keys.Where(k => !l.ContainsKey(k)));

		Dictionary<(CompareGroup, string), int> sectionOrder = [];
		List<(CompareRow Row, int Index)> rows = [];
		foreach (string key in keys)
		{
			l.TryGetValue(key, out SnapshotValue? a);
			r.TryGetValue(key, out SnapshotValue? b);
			SnapshotValue v = a ?? b!;
			sectionOrder.TryAdd((v.Group, v.Section), sectionOrder.Count);

			bool readL = left.WasRead(v.Group);
			bool readR = right.WasRead(v.Group);
			bool secret = (a?.IsSecret ?? false) || (b?.IsSecret ?? false);

			CompareRow row;
			if (v.Group == CompareGroup.StateNow)
				row = new CompareRow(v.Group, v.Section, key, v.Label, Shown(a, readL), Shown(b, readR), RowKind.Background, secret);
			else if (!readL || !readR)
				row = new CompareRow(v.Group, v.Section, key, v.Label, readL ? Shown(a, true) : NotReadText, readR ? Shown(b, true) : NotReadText, RowKind.NotRead, secret);
			else if (a is null)
				row = new CompareRow(v.Group, v.Section, key, v.Label, string.Empty, Shown(b, true), RowKind.OnlyRight, secret);
			else if (b is null)
				row = new CompareRow(v.Group, v.Section, key, v.Label, Shown(a, true), string.Empty, RowKind.OnlyLeft, secret);
			else if (secret)
			{
				bool same = a.SecretHash == b.SecretHash && a.IsSecret == b.IsSecret;
				string text = same ? SameText : DifferentText;
				row = new CompareRow(v.Group, v.Section, key, v.Label, text, text, same ? RowKind.Same : RowKind.Different, true);
			}
			else
			{
				bool same = string.Equals(a.Value, b.Value, StringComparison.Ordinal);
				row = new CompareRow(v.Group, v.Section, key, v.Label, a.Value ?? string.Empty, b.Value ?? string.Empty, same ? RowKind.Same : RowKind.Different);
			}

			rows.Add((row, rows.Count));
		}

		List<CompareRow> ordered = [.. rows
			.OrderBy(x => x.Row.Group)
			.ThenBy(x => sectionOrder[(x.Row.Group, x.Row.Section)])
			.ThenBy(x => x.Index)
			.Select(x => x.Row)];

		return new Comparison(left, right, leftName, rightName, ordered, notes);
	}

	private static void Note(NeuronSnapshot s, string name, List<string> notes)
	{
		if (s.NoAccess)
		{
			notes.Add($"No access on {name}. Only the other side shows.");
			return;
		}

		foreach (KeyValuePair<CompareGroup, string> p in s.NotRead.OrderBy(p => p.Key))
		{
			if (p.Key != CompareGroup.StateNow)
				notes.Add($"{Comparison.GroupName(p.Key)} not read on {name}: {p.Value.TrimEnd('.')}");
		}
	}

	/// <summary>A value as shown: secrets as "Set", never their value.</summary>
	private static string Shown(SnapshotValue? v, bool read)
		=> !read ? NotReadText : v is null ? string.Empty : v.IsSecret ? SetText : v.Value ?? string.Empty;

	private static Dictionary<string, SnapshotValue> Index(IEnumerable<SnapshotValue> values)
	{
		Dictionary<string, SnapshotValue> d = new(StringComparer.Ordinal);
		foreach (SnapshotValue v in values)
			d.TryAdd(v.Key, v);
		return d;
	}
}
