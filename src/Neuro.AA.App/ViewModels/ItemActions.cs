using System.Collections;

namespace Neuro.AA.App.ViewModels;

/// <summary>
/// The groups every right-click menu is made of, always in this order. A separator goes
/// between groups, so the same kind of thing is in the same place on every menu.
/// </summary>
public enum ActionGroup
{
	Open,
	Copy,
	Filter,
	GoTo,
	Do,
	Window,
}

/// <summary>
/// One thing that can be done to an item, or to several selected items. The right-click menu,
/// the bar that appears for a selection and the keyboard shortcuts are all built from the same
/// list of these, so they cannot disagree.
/// </summary>
public sealed class ItemAction
{
	private readonly Func<Task>? run;

	public ItemAction(string text, ActionGroup group, Action run)
		: this(text, group, () =>
		{
			run();
			return Task.CompletedTask;
		})
	{
	}

	public ItemAction(string text, ActionGroup group, Func<Task>? run)
	{
		this.Text = text;
		this.Group = group;
		this.run = run;
	}

	/// <summary>A submenu.</summary>
	public ItemAction(string text, ActionGroup group, IReadOnlyList<ItemAction> children)
	{
		this.Text = text;
		this.Group = group;
		this.Children = children;
	}

	public string Text { get; }
	public ActionGroup Group { get; }
	public IReadOnlyList<ItemAction>? Children { get; }

	/// <summary>Name of an icon in the theme, without the "Icon." prefix.</summary>
	public string? Icon { get; init; }

	/// <summary>Keyboard shortcut, such as "Ctrl+C". Works while the list has the keyboard.</summary>
	public string? Key { get; init; }

	/// <summary>Shown in red: removes or changes something that cannot be taken back.</summary>
	public bool IsDanger { get; init; }

	public bool IsEnabled { get; init; } = true;

	/// <summary>Also shown as a button on the bar that appears when several items are selected.</summary>
	public bool InBar { get; init; }

	/// <summary>Shorter text for the bar; the menu text is used when not set.</summary>
	public string? BarText { get; init; }

	public bool HasChildren => this.Children is { Count: > 0 };

	public async Task RunAsync()
	{
		if (this.run is null || !this.IsEnabled)
			return;

		try
		{
			await this.run();
		}
		catch (Exception ex)
		{
			Ui.Say(ex.Message);
		}
	}
}

/// <summary>Builds the list of actions for an item, group by group.</summary>
public sealed class ActionList : IReadOnlyList<ItemAction>
{
	private readonly List<ItemAction> items = [];

	public int Count => this.items.Count;
	public ItemAction this[int index] => this.items[index];
	public IEnumerator<ItemAction> GetEnumerator() => this.items.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

	public ActionList Add(ItemAction action)
	{
		this.items.Add(action);
		return this;
	}

	public ActionList AddRange(IEnumerable<ItemAction> actions)
	{
		this.items.AddRange(actions);
		return this;
	}

	public ActionList Open(string text, Action run, string? key = "Enter", string? icon = null)
		=> this.Add(new ItemAction(text, ActionGroup.Open, run) { Key = key, Icon = icon });

	/// <summary>Copies text and says so. <paramref name="what"/> finishes the sentence "Copied …".</summary>
	public ActionList Copy(string text, Func<string?> content, string what, string? key = null, bool inBar = false)
		=> this.Add(new ItemAction(text, ActionGroup.Copy, () => Ui.Copy(content(), what)) { Key = key, InBar = inBar, BarText = inBar ? "Copy" : null, Icon = inBar ? "Copy" : null });

	/// <summary>Save to file, next to Copy. The file type the person picks decides the format.</summary>
	public ActionList Save(string text, string fileName, params SaveFormat[] formats)
		=> this.Add(new ItemAction(text, ActionGroup.Copy, () => Ui.Save(fileName, formats)) { Key = "Ctrl+S", InBar = true, BarText = "Save", Icon = "Save" });

	public ActionList Filter(string text, Action run)
		=> this.Add(new ItemAction(text, ActionGroup.Filter, run));

	public ActionList GoTo(string text, Action run, string? icon = null)
		=> this.Add(new ItemAction(text, ActionGroup.GoTo, run) { Icon = icon });

	public ActionList Do(string text, Action run, bool inBar = false, bool danger = false, string? key = null, string? icon = null, bool enabled = true)
		=> this.Add(new ItemAction(text, ActionGroup.Do, run) { InBar = inBar, IsDanger = danger, Key = key, Icon = icon, IsEnabled = enabled });

	public ActionList Do(string text, Func<Task> run, bool inBar = false, bool danger = false, string? key = null, string? icon = null, bool enabled = true)
		=> this.Add(new ItemAction(text, ActionGroup.Do, run) { InBar = inBar, IsDanger = danger, Key = key, Icon = icon, IsEnabled = enabled });

	public ActionList Window(string text, Action run)
		=> this.Add(new ItemAction(text, ActionGroup.Window, run) { Icon = "Window" });

	public ActionList Sub(string text, ActionGroup group, IReadOnlyList<ItemAction> children, string? icon = null)
		=> children.Count == 0 ? this : this.Add(new ItemAction(text, group, children) { Icon = icon });
}

/// <summary>
/// Something that knows what can be done to the items it shows. Views hand it the selected
/// items (one, or several of the same list) and get the actions back.
/// </summary>
public interface IActionSource
{
	IReadOnlyList<ItemAction> ActionsFor(IReadOnlyList<object> items);
}

/// <summary>
/// The moves between screens that any screen may offer under "Go to". Implemented by the
/// shell, so a Neuron has the same destinations wherever it is shown.
/// </summary>
public interface IShell
{
	/// <summary>"Go to" entries for a Neuron: Events, Sources, Console, Map, Chat and Software.</summary>
	IReadOnlyList<ItemAction> NeuronPlaces(string neuronJid, Guid? accountId = null, WorkspaceKind? except = null);

	/// <summary>Everything that can be done to a Neuron, wherever it appears.</summary>
	IReadOnlyList<ItemAction> NeuronActions(string neuronJid, Guid? accountId = null, WorkspaceKind? except = null);

	void ShowEvents(string neuronJid, string? search = null);
	void ShowIssue(string fingerprint);
	void ShowConsole(IReadOnlyList<(Guid Account, string Jid)> neurons, string? command = null);
	void ShowChat(Guid accountId, string bareJid);
	void ShowAccount(Guid accountId);
	void Share(string title, string markdown);
}
