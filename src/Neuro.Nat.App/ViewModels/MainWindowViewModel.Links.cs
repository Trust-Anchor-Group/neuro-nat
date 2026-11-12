using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Links;

namespace Neuro.Nat.App.ViewModels;

/// <summary>How following a link went.</summary>
public enum LinkOutcome
{
	/// <summary>What it points at is on screen.</summary>
	Opened,

	/// <summary>Not there yet, as at start before the accounts have connected: it opens as soon as it is.</summary>
	Waiting,

	/// <summary>None of your accounts reaches the Neuron it is on. Nothing opened.</summary>
	NotThere,

	/// <summary>The Neuron is there, but the thing itself is not kept any more: the nearest screen opened instead.</summary>
	Gone,

	/// <summary>Broken, or not a NAT link. Nothing opened.</summary>
	Broken,
}

/// <summary>
/// Following links: from the search box, from a card in a chat message, and from outside NAT
/// (a click in another program starts NAT or brings it forward with the link). A link only opens
/// a screen. It never runs a command and never changes anything, and the account is never in it:
/// it goes through whichever account reaches the Neuron.
/// </summary>
public sealed partial class MainWindowViewModel
{
	/// <summary>The note for something that is not a link NAT can open.</summary>
	public const string NotALink = "This is not a link NAT can open";

	/// <summary>The note when an event a link points at is not kept any more.</summary>
	public const string EventGone = "The event itself is gone. These are the events at that time.";

	/// <summary>The note when a problem a link points at is not kept any more.</summary>
	public const string ProblemGone = "That problem is no longer kept";

	/// <summary>The note when a node a link points at is not in the Neuron's sources.</summary>
	public const string NodeGone = "That node is not there any more";

	/// <summary>"This is on lab.example.com. None of your accounts is there."</summary>
	public static string NotThereNote(string domain) => $"This is on {domain}. None of your accounts is there.";

	private (NatLink Link, DateTime Until)? pendingLink;
	private DateTime? settledSince;

	private void StartLinks()
	{
		this.Search.FollowLink = text => _ = this.OpenLinkAsync(text);
		this.Chat.DescribeLink = this.DescribeLink;
		this.Chat.FollowLink = link => _ = this.FollowAsync(link, patient: false);
	}

	/// <summary>Opens what a link points at. From outside NAT, <paramref name="patient"/>: what is not there yet opens once it is.</summary>
	public void OpenLink(string? text, bool patient = false) => _ = this.OpenLinkAsync(text, patient);

	/// <summary>
	/// Opens what a link points at, or says in one short note why it cannot. Garbage is a note,
	/// never an exception. With <paramref name="patient"/>, a link to a Neuron that is not there
	/// yet waits while accounts are still connecting, and opens when it comes.
	/// </summary>
	public Task<LinkOutcome> OpenLinkAsync(string? text, bool patient = false)
	{
		if (!NatLink.TryParse(text, out NatLink link))
		{
			Ui.Say(NotALink);
			return Task.FromResult(LinkOutcome.Broken);
		}

		return this.FollowAsync(link, patient);
	}

	/// <summary>Opens what a link points at.</summary>
	public async Task<LinkOutcome> FollowAsync(NatLink link, bool patient)
	{
		this.pendingLink = null;
		if (!this.CanReach(link))
		{
			if (patient && this.MayStillCome())
			{
				this.pendingLink = (link, DateTime.UtcNow.AddSeconds(45));
				return LinkOutcome.Waiting;
			}

			Ui.Say(NotThereNote(link.Domain));
			return LinkOutcome.NotThere;
		}

		try
		{
			return await this.GoAsync(link);
		}
		catch (Exception ex)
		{
			// A link only opens a screen; whatever went wrong on the way is a note, not a crash.
			AppLog.Error("Could not open " + link + ".", ex);
			Ui.Say(NotALink);
			return LinkOutcome.Broken;
		}
	}

	/// <summary>Tries again to open a link that waited for its Neuron. Called as Neurons and accounts appear.</summary>
	private void TryPendingLink()
	{
		if (this.pendingLink is not (NatLink link, DateTime until))
			return;

		if (this.CanReach(link))
		{
			_ = this.FollowAsync(link, patient: false);
			return;
		}

		if (DateTime.UtcNow > until || !this.MayStillCome())
		{
			this.pendingLink = null;
			Ui.Say(NotThereNote(link.Domain));
		}
	}

	/// <summary>
	/// True while what a link points at may still turn up: an account is still connecting, or
	/// they all connected only a moment ago and their Neurons are still being listed.
	/// </summary>
	private bool MayStillCome()
	{
		if (this.Accounts.Count == 0)
		{
			this.settledSince = null;
			return false;
		}

		if (this.Accounts.Any(a => !a.IsOnline && !a.IsFailed))
		{
			this.settledSince = null;
			return true;
		}

		this.settledSince ??= DateTime.UtcNow;
		return DateTime.UtcNow - this.settledSince < TimeSpan.FromSeconds(8);
	}

	/// <summary>The Neuron a link is on, by its domain, through whichever account reaches it. One that reports first.</summary>
	private FleetRowViewModel? NeuronOf(string domain)
	{
		FleetRowViewModel? Find() => this.Fleet.Rows
			.Where(r => NatLink.DomainOf(r.BareJid) == domain)
			.OrderBy(r => r.State == WatchState.Live ? 0 : 1)
			.FirstOrDefault();

		FleetRowViewModel? row = Find();
		if (row is null)
		{
			this.Fleet.Refresh();
			row = Find();
		}

		return row;
	}

	/// <summary>The person or room a link points at, through whichever account has the conversation.</summary>
	private PersonViewModel? PersonOf(NatLink link)
	{
		bool room = link.Kind == LinkKind.Room;
		PersonViewModel? Find() => this.Chat.Everyone
			.Where(p => !p.IsPrivate && string.Equals(p.BareJid, link.Id, StringComparison.OrdinalIgnoreCase))
			.OrderBy(p => p.IsRoom == room ? 0 : 1)
			.FirstOrDefault();

		PersonViewModel? p = Find();
		if (p is null)
		{
			this.Chat.Refresh();
			p = Find();
		}

		return p;
	}

	/// <summary>True when one of your accounts reaches what the link is on.</summary>
	private bool CanReach(NatLink link)
	{
		if (link.Kind is LinkKind.Chat or LinkKind.Room)
		{
			if (this.PersonOf(link) is not null)
				return true;

			// An account on that domain, or under it, has nobody there yet: that is said where it opens.
			return this.Accounts.Any(a => DomainOf(a) is string d && (link.Domain == d || link.Domain.EndsWith("." + d, StringComparison.Ordinal)));
		}

		return this.NeuronOf(link.Domain) is not null;
	}

	private static string? DomainOf(AccountItemViewModel a) => a.Session.Profile.EffectiveDomain?.ToLowerInvariant();

	private async Task<LinkOutcome> GoAsync(NatLink link)
	{
		switch (link.Kind)
		{
			case LinkKind.Chat:
			case LinkKind.Room:
				if (this.PersonOf(link) is not PersonViewModel p)
				{
					Ui.Say("No conversation with " + link.Id + " yet");
					return LinkOutcome.Gone;
				}

				this.Chat.Open(p.AccountId, p.BareJid);
				this.Workspace = WorkspaceKind.Chat;
				return LinkOutcome.Opened;
		}

		FleetRowViewModel row = this.NeuronOf(link.Domain)!;
		switch (link.Kind)
		{
			case LinkKind.Neuron:
				this.OpenNeuron(row.BareJid, row.AccountId, Links.ScreenOf(link.Tab));
				return LinkOutcome.Opened;

			case LinkKind.Problem:
				this.Events.Flush();
				Issue? issue = this.fleet.Monitors.Hub.Issues().FirstOrDefault(i => string.Equals(i.Fingerprint, link.Id, StringComparison.OrdinalIgnoreCase));
				if (issue is null)
				{
					this.OpenNeuron(row.BareJid, row.AccountId, WorkspaceKind.NeuronEvents);
					Ui.Say(ProblemGone);
					return LinkOutcome.Gone;
				}

				this.ShowIssue(issue.Fingerprint);
				return LinkOutcome.Opened;

			case LinkKind.Event:
				NeuronEvent? e = this.EventOf(link);
				this.OpenNeuron(e?.Source.NeuronJid ?? row.BareJid, e?.Source.AccountId ?? row.AccountId, WorkspaceKind.NeuronEvents);
				this.NeuronEvents.SearchText = string.Empty;
				this.NeuronEvents.SeverityFilter = SeverityFilter.All;
				this.NeuronEvents.Flush();
				if (e is not null)
				{
					this.NeuronEvents.SelectedEvent = this.NeuronEvents.Stream.FirstOrDefault(x => x.Sequence == e.Sequence) ?? e;
					return LinkOutcome.Opened;
				}

				// Older than what is kept: the events around its time, and a word that it is gone.
				if (link.At is DateTime at)
				{
					DateTime local = at.ToLocalTime();
					this.NeuronEvents.SelectedEvent = this.NeuronEvents.Stream.MinBy(x => Math.Abs((x.Timestamp - local).Ticks));
				}

				Ui.Say(EventGone);
				return LinkOutcome.Gone;

			case LinkKind.Node:
				this.OpenNeuron(row.BareJid, row.AccountId, WorkspaceKind.Sources);
				if (await this.Sources.RevealAsync(link.Parts))
					return LinkOutcome.Opened;

				Ui.Say(NodeGone);
				return LinkOutcome.Gone;
		}

		Ui.Say(NotALink);
		return LinkOutcome.Broken;
	}

	/// <summary>The kept event a link points at. Its time narrows the search before any hashing.</summary>
	private NeuronEvent? EventOf(NatLink link)
	{
		long? second = link.At?.Ticks / TimeSpan.TicksPerSecond;
		return this.fleet.Monitors.Hub.FindLast(x =>
			(second is null || x.Timestamp.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond == second) &&
			NatLink.DomainOf(x.Source.NeuronJid) == link.Domain &&
			NatLink.EventId(x) == link.Id);
	}

	/// <summary>What a link points at, as it is now, for its card in a chat message.</summary>
	public LinkInfo DescribeLink(NatLink link)
	{
		if (link.Kind is LinkKind.Chat or LinkKind.Room)
		{
			PersonViewModel? p = this.Chat.Everyone.FirstOrDefault(x => !x.IsPrivate && string.Equals(x.BareJid, link.Id, StringComparison.OrdinalIgnoreCase));
			if (p is null)
				return new LinkInfo(link.Id, link.Kind == LinkKind.Room ? "A room you are not in" : "Nobody you talk to", "idle");

			return p.IsRoom
				? new LinkInfo(p.Label, "Room", "idle")
				: new LinkInfo(p.Label, string.IsNullOrEmpty(p.PresenceText) ? "Person" : p.PresenceText, p.IsOnline ? "ok" : p.IsAway ? "busy" : "idle");
		}

		FleetRowViewModel? row = this.Fleet.Rows.Where(r => NatLink.DomainOf(r.BareJid) == link.Domain).OrderBy(r => r.State == WatchState.Live ? 0 : 1).FirstOrDefault();
		if (row is null)
			return new LinkInfo(link.Domain, "None of your accounts is there", "idle");

		string name = Converters.NeuronNameConverter.Name(row.BareJid);
		string neuronTone = row.State == WatchState.Live ? "ok" : row.State is WatchState.Failed or WatchState.Forbidden ? "bad" : "idle";
		switch (link.Kind)
		{
			case LinkKind.Neuron:
				string? tab = link.Tab is string t ? char.ToUpperInvariant(t[0]) + t[1..] : null;
				return new LinkInfo(tab is null ? name : name + ", " + tab, row.StateText, neuronTone);

			case LinkKind.Problem:
				Issue? issue = this.fleet.Monitors.Hub.Issues().FirstOrDefault(i => string.Equals(i.Fingerprint, link.Id, StringComparison.OrdinalIgnoreCase));
				if (issue is null)
					return new LinkInfo("A problem on " + name, "No longer kept", "idle");

				string state = issue.State switch
				{
					IssueState.Acknowledged => "Acknowledged",
					IssueState.Muted => "Muted",
					IssueState.Resolved => "Fixed",
					_ => "Open",
				};
				string tone = issue.State switch
				{
					IssueState.Resolved => "ok",
					IssueState.Muted => "idle",
					_ => issue.Severity >= EventSeverity.Error ? "bad" : "busy",
				};
				return new LinkInfo(issue.Title, $"{state}, {Ui.Count(issue.Count, "event")} on {name}", tone);

			case LinkKind.Event:
				NeuronEvent? e = this.EventOf(link);
				if (e is null)
				{
					string when = link.At is DateTime at ? " from " + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : string.Empty;
					return new LinkInfo("An event on " + name + when, "No longer kept", "idle");
				}

				return new LinkInfo(e.Title, $"{e.Severity} on {name} at {e.Timestamp:yyyy-MM-dd HH:mm:ss}",
					e.Severity >= EventSeverity.Error ? "bad" : e.Severity == EventSeverity.Warning ? "busy" : "idle");

			default:
				return new LinkInfo(link.Id, "In the sources of " + name, neuronTone);
		}
	}
}
