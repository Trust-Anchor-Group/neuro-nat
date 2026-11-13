using Avalonia.Controls.Templates;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.Features;

// The one contract between NAT and a feature that lives in a project of its own (such as app
// telemetry). NAT knows a feature only through these types: whether it is on a Neuron, and what
// it adds to a Neuron's page, the rail, the Inbox, the search box and the Big screen. Everything a
// feature adds is optional; a feature that adds nothing leaves NAT exactly as it is without it.

/// <summary>Whether a feature is on a Neuron, as far as the account that asked can tell.</summary>
public enum FeaturePresence
{
	/// <summary>Not asked yet, or the answer did not come (no connection, not signed in).</summary>
	Unknown,

	/// <summary>Not on this Neuron, or the Neuron has no web address to ask.</summary>
	Absent,

	/// <summary>On the Neuron, but the account lacks the privilege to use it.</summary>
	NotAllowed,

	/// <summary>On the Neuron, and the account may use it.</summary>
	Present,
}

/// <summary>A Neuron as one account reaches it.</summary>
public readonly record struct FeatureNeuron(Guid AccountId, string NeuronJid);

/// <summary>
/// Where something a feature adds leads: a tab on a Neuron's page when <see cref="NeuronJid"/>
/// is given, else a place in the rail. <see cref="ViewKey"/> is the key of that tab or place.
/// </summary>
public sealed record FeatureTarget(string ViewKey, Guid? AccountId = null, string? NeuronJid = null)
{
	/// <summary>A part inside the tab or place, such as one of its pages. The content is shown it through <see cref="IFeatureContent"/>.</summary>
	public string? Part { get; init; }
}

/// <summary>
/// Content a feature makes for a tab or a place that has parts of its own (pages, a chosen view).
/// When something leads to a part, NAT opens the tab or place and then asks the content to show it.
/// </summary>
public interface IFeatureContent
{
	/// <summary>Shows a part. Unknown parts are left alone.</summary>
	void ShowPart(string part);
}

/// <summary>
/// Links to what a feature shows (neuro-nat:// links), made by the shell. Until the shell fills
/// <see cref="Make"/>, there are no links and nothing offers to copy one.
/// </summary>
public static class FeatureLinks
{
	/// <summary>The link that opens a target, or null when there is none.</summary>
	public static Func<FeatureTarget, string?> Make { get; set; } = _ => null;
}

/// <summary>A tab on a Neuron's page. <see cref="Content"/> makes what the tab shows (a view model the feature has a template for).</summary>
public sealed record FeatureTab(string Key, string Title, Func<object> Content);

/// <summary>A place in the rail, under the five places. <see cref="IconKey"/> names an icon resource ("Icon." + key).</summary>
public sealed record FeaturePlace(string Key, string Title, string IconKey, Func<object> Content);

/// <summary>A page on the Big screen.</summary>
public sealed record FeatureScreenPage(string Key, string Title, Func<object> Content);

/// <summary>A line in the search box (Ctrl+K).</summary>
public sealed record FeatureSearchHit(string Title, string Hint, FeatureTarget Opens)
{
	/// <summary>What kind of thing it is, at the end of the line, as the search box says it ("Screen", "Neuron", "Do").</summary>
	public string Kind { get; init; } = "Screen";

	/// <summary>More to find it by that is not shown.</summary>
	public string? Words { get; init; }
}

/// <summary>Something that waits for the operator, in the Inbox beside messages and requests.</summary>
public sealed record FeatureInboxItem(string Key, Guid AccountId, string NeuronJid, string KindLabel, string Title, DateTime When)
{
	public string? Detail { get; init; }

	/// <summary>The colour of the row's icon: ok, busy, bad or info.</summary>
	public string Tone { get; init; } = "info";

	/// <summary>The answer on the row, such as "Done". None when null.</summary>
	public string? PrimaryText { get; init; }

	/// <summary>What the answer does. The row leaves when the feature stops listing it.</summary>
	public Func<Task>? Primary { get; init; }

	/// <summary>Where opening the row leads, if anywhere.</summary>
	public FeatureTarget? Opens { get; init; }
}

/// <summary>What NAT hands a feature when it starts: its accounts and Neurons, and when to ask again.</summary>
public interface IFeatureHost
{
	AccountManager Accounts { get; }

	MonitorService Monitors { get; }

	/// <summary>An account has signed in (its connection is up). Any thread.</summary>
	event EventHandler<Guid>? SignedIn;

	/// <summary>A Neuron's software is not what it was the last time NAT read it. Any thread.</summary>
	event EventHandler<FeatureNeuron>? SoftwareChanged;

	/// <summary>
	/// The end-to-end encryption on an account's connection (the one chat uses), for a feature
	/// that sends something a Neuron takes only encrypted. Null when there is none.
	/// </summary>
	Waher.Networking.XMPP.IEndToEndEncryption? EncryptionOf(Guid accountId) => null;

	/// <summary>
	/// Makes sure the encryption keys of an online resource are known, asking it for them when its
	/// presence did not carry them. Null when they are known, else why they are not, for the log.
	/// </summary>
	Task<string?> FetchKeysOfAsync(Guid accountId, string fullJid) => Task.FromResult<string?>("no encryption");
}

/// <summary>
/// A feature in a project of its own. The desktop host registers it at start (see
/// <see cref="FeatureSet"/>); NAT asks it what it adds and shows that. Members may be called on
/// any thread and must not block.
/// </summary>
public interface INatFeature : IAsyncDisposable
{
	/// <summary>A short stable key, such as "telemetry".</summary>
	string Key { get; }

	/// <summary>Called once, before anything else is asked.</summary>
	void Start(IFeatureHost host);

	/// <summary>Raised when what the feature adds may have changed: for one Neuron, or for all when null. Any thread.</summary>
	event EventHandler<FeatureNeuron?>? Changed;

	/// <summary>Whether the feature is on a Neuron.</summary>
	FeaturePresence PresenceOn(Guid accountId, string neuronJid);

	/// <summary>Quiet lines on a Neuron's Software tab.</summary>
	IReadOnlyList<string> SoftwareLines(Guid accountId, string neuronJid) => [];

	/// <summary>Tabs on a Neuron's page, after its own.</summary>
	IReadOnlyList<FeatureTab> NeuronTabs(Guid accountId, string neuronJid) => [];

	/// <summary>Places in the rail.</summary>
	IReadOnlyList<FeaturePlace> Places() => [];

	/// <summary>What waits in the Inbox.</summary>
	IReadOnlyList<FeatureInboxItem> InboxItems() => [];

	/// <summary>What the search box can find. It narrows the list itself, as for everything else.</summary>
	IReadOnlyList<FeatureSearchHit> SearchHits() => [];

	/// <summary>Pages on the Big screen.</summary>
	IReadOnlyList<FeatureScreenPage> BigScreenPages() => [];

	/// <summary>How what the feature shows is drawn: templates for the view models its tabs, places and pages make.</summary>
	IReadOnlyList<IDataTemplate> Templates() => [];
}
