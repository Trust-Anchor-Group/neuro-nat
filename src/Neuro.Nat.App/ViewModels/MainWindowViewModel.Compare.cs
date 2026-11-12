using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Links;

namespace Neuro.Nat.App.ViewModels;

/// <summary>
/// Comparing two Neurons, or a Neuron with a kept copy of how it was. It starts from two Neurons
/// selected in the list, from "Compare with" on a Neuron, and from "compare" and two names in
/// the search box.
/// </summary>
public sealed partial class MainWindowViewModel
{
	/// <summary>The comparison screen.</summary>
	public CompareViewModel Compare { get; private set; } = null!;

	public bool IsCompare => this.Workspace == WorkspaceKind.Compare;

	/// <summary>"Neurons" and an arrow before the title: the way up to all Neurons, on a Neuron's page and on a comparison.</summary>
	public bool HasCrumb => this.IsNeuronPage || this.IsCompare;

	private void StartCompare(Engine.Software.SoftwareService software)
	{
		this.Compare = new CompareViewModel(new Engine.Compare.LiveNeuronReader(this.fleet.Monitors, software, this.fleet))
		{
			Neurons = () => this.Fleet.Rows,
		};

		// Windows beside the main one keep their copies in the same place.
		if (this.primary is not null)
			this.Compare.Store = this.primary.Compare.Store;
	}

	private CompareSide? SideOf(string jid, Guid? account = null)
		=> this.RowOf(jid, account) is FleetRowViewModel row ? new CompareSide(row.AccountId, row.BareJid) : null;

	/// <summary>Compares two Neurons and shows them side by side.</summary>
	public void CompareNeurons((Guid Account, string Jid) a, (Guid Account, string Jid) b)
	{
		this.Workspace = WorkspaceKind.Compare;
		_ = this.Compare.CompareAsync(new CompareSide(a.Account, a.Jid), new CompareSide(b.Account, b.Jid));
	}

	/// <summary>"Compare with": the comparison screen, asking which Neuron to put beside this one.</summary>
	public void CompareWith(string jid, Guid? account = null)
	{
		if (this.SideOf(jid, account) is not CompareSide a)
			return;

		this.Compare.ChooseOther(a);
		this.Workspace = WorkspaceKind.Compare;
	}

	/// <summary>"Compare with earlier": the comparison screen, asking which kept copy to put beside the Neuron.</summary>
	public void CompareWithEarlier(string jid, Guid? account = null)
	{
		if (this.SideOf(jid, account) is not CompareSide a)
			return;

		this.Workspace = WorkspaceKind.Compare;
		_ = this.Compare.ChooseEarlierAsync(a);
	}

	/// <summary>"Keep a copy now": reads the Neuron and keeps what it read.</summary>
	public Task KeepCopy(string jid, Guid? account = null)
		=> this.SideOf(jid, account) is CompareSide a ? this.Compare.KeepCopyAsync(a) : Task.CompletedTask;

	[RelayCommand]
	private void ShowCompare()
	{
		if (!this.Compare.HasResult && !this.Compare.IsChoosing)
			this.Compare.ChooseFirst();
		this.Workspace = WorkspaceKind.Compare;
	}

	/// <summary>On a Neuron's page: the comparison screen, asking which Neuron to compare it with.</summary>
	[RelayCommand]
	private void ComparePage()
	{
		if (this.Page is NeuronPageViewModel p)
			this.CompareWith(p.BareJid, p.AccountId);
	}

	/// <summary>The comparison actions on a Neuron's menu.</summary>
	private void AddCompareActions(ActionList list, string neuronJid, Guid account)
	{
		list.Do("Compare with…", () => this.CompareWith(neuronJid, account), icon: "Compare");
		list.Do("Compare with earlier…", () => this.CompareWithEarlier(neuronJid, account), icon: "Compare");
		list.Do("Keep a copy now", () => this.KeepCopy(neuronJid, account), icon: "Save");
	}

	/// <summary>
	/// "compare lab prod" in the search box: one line for each pair of Neurons the two words
	/// name. Only when the query starts with "compare".
	/// </summary>
	private IEnumerable<SearchItem> CompareSearchItems(Func<string?, Geometry?> icon)
	{
		string query = this.Search?.Query?.Trim() ?? string.Empty;
		string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (words.Length == 0 || !"compare".StartsWith(words[0], StringComparison.OrdinalIgnoreCase) || words[0].Length < 3)
			yield break;

		yield return new SearchItem("Compare two Neurons", "Pick them", icon("Compare"), this.ShowCompare) { Kind = "Do", Words = "compare" };

		if (words.Length < 3)
			yield break;

		bool Names(NeuronChipViewModel c, string w) => c.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || c.BareJid.Contains(w, StringComparison.OrdinalIgnoreCase);

		// A name that starts with the word comes before one that only has it somewhere.
		static int Fit(NeuronChipViewModel c, string w) => c.Label.StartsWith(w, StringComparison.OrdinalIgnoreCase) ? 0 : c.Label.Contains(w, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

		List<NeuronChipViewModel> first = [.. this.NeuronChips.Where(c => Names(c, words[1])).OrderBy(c => Fit(c, words[1]))];
		List<NeuronChipViewModel> second = [.. this.NeuronChips.Where(c => Names(c, words[2])).OrderBy(c => Fit(c, words[2]))];
		HashSet<(string, string)> seen = [];
		foreach (NeuronChipViewModel a in first)
		{
			foreach (NeuronChipViewModel b in second)
			{
				if (string.Equals(a.BareJid, b.BareJid, StringComparison.OrdinalIgnoreCase) || !seen.Add((a.BareJid, b.BareJid)))
					continue;

				string ja = a.BareJid, jb = b.BareJid;
				yield return new SearchItem($"Compare {a.Label} and {b.Label}", "Side by side", icon("Compare"), () =>
				{
					if (this.SideOf(ja) is CompareSide sa && this.SideOf(jb) is CompareSide sb)
						this.CompareNeurons((sa.AccountId, sa.NeuronJid), (sb.AccountId, sb.NeuronJid));
				})
				{
					Kind = "Do",
					Words = $"compare {ja} {jb} {a.Label} {b.Label}",
				};
			}
		}
	}

	/// <summary>Opens a comparison link: two Neurons, or a Neuron and a kept copy of it.</summary>
	private async Task<LinkOutcome> GoCompareAsync(NatLink link, FleetRowViewModel row)
	{
		if (link.CopyId is string id)
		{
			if (await this.Compare.Store.GetAsync(id) is null)
			{
				this.CompareWithEarlier(row.BareJid, row.AccountId);
				Ui.Say(CopyGone);
				return LinkOutcome.Gone;
			}

			this.Workspace = WorkspaceKind.Compare;
			await this.Compare.CompareAsync(new CompareSide(row.AccountId, row.BareJid), new CompareSide(row.AccountId, row.BareJid, id));
			return LinkOutcome.Opened;
		}

		if (link.OtherDomain is not string other || this.NeuronOf(other) is not FleetRowViewModel second)
		{
			Ui.Say(NotThereNote(link.OtherDomain ?? link.Domain));
			return LinkOutcome.NotThere;
		}

		this.Workspace = WorkspaceKind.Compare;
		await this.Compare.CompareAsync(new CompareSide(row.AccountId, row.BareJid), new CompareSide(second.AccountId, second.BareJid));
		return LinkOutcome.Opened;
	}

	/// <summary>The note when a kept copy a link points at is not kept any more.</summary>
	public const string CopyGone = "That copy is not kept any more";

	private LinkInfo DescribeCompare(NatLink link, string name)
	{
		if (link.CopyId is not null)
			return new LinkInfo(name + " and a kept copy", "Comparison", "idle");

		string other = link.OtherDomain is string d && this.Fleet.Rows.FirstOrDefault(r => NatLink.DomainOf(r.BareJid) == d) is FleetRowViewModel r2
			? Converters.NeuronNameConverter.Name(r2.BareJid)
			: link.OtherDomain ?? string.Empty;
		return new LinkInfo(name + " and " + other, "Comparison", "idle");
	}
}
