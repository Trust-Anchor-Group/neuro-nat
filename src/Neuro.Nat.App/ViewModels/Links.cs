using CommunityToolkit.Mvvm.ComponentModel;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Links;

namespace Neuro.Nat.App.ViewModels;

/// <summary>
/// Anything you can point at gets a link: a Neuron, a problem, an event, a node in Sources, a
/// conversation or a room. This makes the link for an item wherever it is shown, and adds
/// "Copy link" to every list of actions, once, so it is on every right-click menu and on the bar
/// that shows when several things are selected.
/// </summary>
public static class Links
{
	/// <summary>The link for something shown on screen, or null when it has none.</summary>
	public static NatLink? For(object? item)
	{
		switch (item)
		{
			case FleetRowViewModel row:
				return NatLink.Neuron(row.BareJid);

			case NeuronChipViewModel chip:
				return NatLink.Neuron(chip.BareJid);

			case NeuronPageViewModel page:
				return NatLink.Neuron(page.BareJid, TabOf(page.Tab));

			case IssueItemViewModel issue:
				return NatLink.Problem(issue.Issue.Latest.Source.NeuronJid, issue.Fingerprint);

			case NeuronEvent e:
				return NatLink.Event(e);

			case TreeItemViewModel tree when !tree.IsPlaceholder && tree.Owner.SelectedNeuron is NeuronChoice neuron && PathOf(tree) is { Count: > 0 } path:
				return NatLink.Node(neuron.BareJid, path);

			case PersonViewModel p when !p.IsPrivate:
				return p.IsRoom ? NatLink.Room(p.BareJid) : NatLink.Chat(p.BareJid);

			default:
				return null;
		}
	}

	/// <summary>
	/// The actions for some items, with "Copy link" added when every one of them has a link.
	/// Every right-click menu, selection bar and shortcut gets its actions through here.
	/// </summary>
	public static IReadOnlyList<ItemAction> WithLink(IReadOnlyList<ItemAction> actions, IReadOnlyList<object> items)
	{
		if (items.Count == 0 || actions.Any(a => a.Text is "Copy link" or "Copy links"))
			return actions;

		List<NatLink> links = [];
		foreach (object item in items)
		{
			if (For(item) is not NatLink link)
				return actions;
			links.Add(link);
		}

		string text = links.Count == 1 ? "Copy link" : "Copy links";
		string what = links.Count == 1 ? "the link" : Ui.Count(links.Count, "link");
		ItemAction copy = new(text, ActionGroup.Copy, () => Ui.Copy(string.Join(Environment.NewLine, links.Select(l => l.ToString())), what))
		{
			Icon = "Link",
			InBar = true,
			BarText = text,
		};

		return [.. actions, copy];
	}

	/// <summary>The tab of a Neuron's page as a link names it. Null for the Overview.</summary>
	public static string? TabOf(WorkspaceKind screen) => screen switch
	{
		WorkspaceKind.NeuronEvents => "events",
		WorkspaceKind.Sources => "sources",
		WorkspaceKind.NeuronConsole => "console",
		WorkspaceKind.Updates => "software",
		WorkspaceKind.Identities => "identity",
		WorkspaceKind.PubSub => "storage",
		_ => null,
	};

	/// <summary>The tab of a Neuron's page a link names. Its Overview when it names none.</summary>
	public static WorkspaceKind ScreenOf(string? tab) => tab switch
	{
		"events" => WorkspaceKind.NeuronEvents,
		"sources" => WorkspaceKind.Sources,
		"console" => WorkspaceKind.NeuronConsole,
		"software" => WorkspaceKind.Updates,
		"identity" => WorkspaceKind.Identities,
		"storage" => WorkspaceKind.PubSub,
		_ => WorkspaceKind.Neuron,
	};

	/// <summary>The ids from the data source down to an item in the Sources tree.</summary>
	public static IReadOnlyList<string> PathOf(TreeItemViewModel item)
	{
		List<string> path = [];
		for (TreeItemViewModel? i = item; i is not null; i = i.Parent)
		{
			string? id = SourcesViewModel.IdOf(i);
			if (id is null)
				return [];
			path.Insert(0, id);
		}

		// Found by a search, a node does not know where it hangs: its source is the most that is known.
		if (item is NodeItemViewModel { Parent: null } node && !string.IsNullOrEmpty(node.Node.SourceId))
			path.Insert(0, node.Node.SourceId);

		return path;
	}
}

/// <summary>What a link points at, as it is now: its name, one line of state, and the tone of its dot.</summary>
public sealed record LinkInfo(string Title, string State, string Tone);

/// <summary>A link in a chat message, shown as a small card under it. A click opens what it points at.</summary>
public sealed partial class LinkCardViewModel(NatLink link) : ObservableObject
{
	public NatLink Link { get; } = link;

	/// <summary>The link itself, for the tooltip.</summary>
	public string Text => this.Link.ToString();

	[ObservableProperty] private string title = string.Empty;
	[ObservableProperty] private string state = string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOk), nameof(IsBusy), nameof(IsBad))]
	private string tone = "idle";

	public bool IsOk => this.Tone == "ok";
	public bool IsBusy => this.Tone == "busy";
	public bool IsBad => this.Tone == "bad";

	public void Apply(LinkInfo info)
	{
		this.Title = info.Title;
		this.State = info.State;
		this.Tone = info.Tone;
	}
}
