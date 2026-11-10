using Neuro.AA.Engine.Attention;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.Engine.Tests;

/// <summary>Notification wording is copied from the Neuron's source (IoTGateway and the broker).</summary>
public class AttentionTests
{
	private static readonly Guid account = Guid.NewGuid();

	private static IncomingNotification In(string markdown, string? id = null, string? replaces = null, DateTime? at = null, string jid = "neuron@lab.local")
		=> new(account, jid, id, replaces, at ?? DateTime.Now, markdown, markdown);

	[Theory]
	[InlineData("Presence subscription request received from **alice@lab\\.local**. You can accept or decline the request from the roster configuration in the Administration portal.",
		NotificationKind.RosterRequest, true, "Contact request from alice@lab.local")]
	[InlineData("No approved Legal Identity registered for the gateway.", NotificationKind.NeuronIdentity, true, "The Neuron has no approved legal identity")]
	[InlineData("The approved Legal Identity for the gateway **expires in 12 days**.", NotificationKind.NeuronIdentity, true, "The Neuron's legal identity expires in 12 days")]
	[InlineData("Legal identity application received: [`2f1c@legal.lab.local`](https://lab.local/ValidateLegalId.md?ID=2f1c)", NotificationKind.LegalIdApplication, true, "Legal identity application to review")]
	[InlineData("Legal identity application has been automatically approved: [`x@legal`](https://lab.local/x) (by `Peer review`)", NotificationKind.LegalIdDecision, false, "Legal identity approved automatically")]
	[InlineData("**Reminder**: Smart Contract [c@legal](https://lab.local/SignatureRequest.md?RequestId=1) is waiting for your signature.", NotificationKind.SignatureRequest, true, "Reminder: a contract waits for the Neuron's signature")]
	[InlineData("Unable to upload backup to `backup@other`.", NotificationKind.Backup, true, "Backup upload failed")]
	[InlineData("Backup file has been successfully been uploaded. The initial attempt to upload the backup file failed, but a sequent rescheduled upload succeeded.", NotificationKind.Backup, false, "Backup uploaded after a retry")]
	[InlineData("Left room `ops@muc.lab.local`; room is full.", NotificationKind.ChatRoom, false, "Left a chat room: room is full")]
	[InlineData("User session locked.\n\n| Details ||\n|:----|:---|\n| User | ops |", NotificationKind.MachineSession, false, "User session locked")]
	[InlineData("Something nobody has seen before.", NotificationKind.Other, false, "Something nobody has seen before.")]
	public void RecognizesWording(string markdown, NotificationKind kind, bool action, string title)
	{
		NeuronNotification n = NeuronNotification.From(In(markdown));
		Assert.Equal(kind, n.Kind);
		Assert.Equal(action, n.NeedsAction);
		Assert.Equal(title, n.Title);
	}

	[Fact]
	public void ReadsAccountTableAndSubject()
	{
		string md = "XMPP Account created:\n\n| Account Information ||\n|:-----|:------|\n| User Name: | [kalle](https://lab.local/Account.md?UserName=kalle) |\n| e-Mail: | kalle@example\\.com |\n";
		NeuronNotification n = NeuronNotification.From(In(md));

		Assert.Equal(NotificationKind.AccountCreated, n.Kind);
		Assert.Equal("kalle", n.Subject);
		Assert.Contains(n.Fields, f => f.Key == "e-Mail:" && f.Value == "kalle@example.com");
		Assert.Contains(n.Links, l => l.Url == "https://lab.local/Account.md?UserName=kalle");
	}

	[Fact]
	public void AlertCarriesEventSeverityAndObject()
	{
		string md = "Alert\n===============\n\nDatabase corrupt, repair started.\n\n| Information ||\n|:------|:-----|\n| Timestamp | 2026-11-09, 21:00:00 |\n| Event ID | DbCorrupt |\n| Object | Ledger |\n";
		NeuronNotification n = NeuronNotification.From(In(md));

		Assert.Equal(NotificationKind.Alert, n.Kind);
		Assert.Equal(EventSeverity.Critical, n.Severity);
		Assert.True(n.NeedsAction);
		Assert.Equal("Database corrupt, repair started.", n.Title);
		Assert.Equal("Ledger", n.Subject);
	}

	[Fact]
	public void SoftwareUpdateNeedsActionOnlyWhenManual()
	{
		string auto = "New version of installed software has been downloaded\n=========\n\n| Package ||\n|:--|:--|\n| Filename: | `TAG.Neuron.package` |\n\nThe new software will be automatically installed in 5 minutes, unless cancelled from the chat interface.";
		string manual = "New version of installed software has been downloaded\n=========\n\nYou can update the server with the new package from the chat interface.";

		Assert.False(NeuronNotification.From(In(auto)).NeedsAction);
		Assert.Equal("TAG.Neuron.package", NeuronNotification.From(In(auto)).Subject);
		Assert.True(NeuronNotification.From(In(manual)).NeedsAction);
	}

	[Fact]
	public void CopiesThroughAnotherAccountAreDropped()
	{
		AttentionInbox inbox = new();
		DateTime t = DateTime.Now;
		Assert.NotNull(inbox.Add(In("Account requested.", at: t)));
		Assert.Null(inbox.Add(In("Account requested.", at: t.AddSeconds(5))));
		Assert.NotNull(inbox.Add(In("Account requested.", at: t.AddMinutes(10))));
		Assert.NotNull(inbox.Add(In("Account requested.", at: t, jid: "other@lab.local")));
		Assert.Equal(3, inbox.Items.Count);
	}

	[Fact]
	public void CorrectionUpdatesTheSameItem()
	{
		AttentionInbox inbox = new();
		NeuronNotification first = inbox.Add(In("Unable to upload backup to `x`.", id: "m1"))!;
		inbox.SetState(first.Id, AttentionState.Read);

		NeuronNotification? updated = inbox.Add(In("Backup file has been successfully been uploaded.", id: "m2", replaces: "m1"));

		Assert.NotNull(updated);
		Assert.Single(inbox.Items);
		Assert.Equal(first.Id, updated!.Id);
		Assert.NotNull(updated.Updated);
		Assert.False(updated.NeedsAction);
		Assert.Equal(AttentionState.Unread, updated.State);
	}

	[Fact]
	public void OpenCountFollowsState()
	{
		AttentionInbox inbox = new();
		NeuronNotification a = inbox.Add(In("Account requested."))!;
		inbox.Add(In("XMPP Account created:"));
		Assert.Equal(1, inbox.OpenCount);
		Assert.Equal(2, inbox.UnreadCount);

		inbox.SetState(a.Id, AttentionState.Handled);
		Assert.Equal(0, inbox.OpenCount);
		Assert.NotNull(inbox.Get(a.Id)!.HandledAt);

		inbox.MarkAllRead();
		Assert.Equal(0, inbox.UnreadCount);
		Assert.Equal(2, inbox.ClearDone());
		Assert.Empty(inbox.Items);
	}

	[Fact]
	public void CapacityDropsDoneItemsFirst()
	{
		AttentionInbox inbox = new() { Capacity = 3 };
		DateTime t = DateTime.Now.AddHours(-1);
		NeuronNotification open = inbox.Add(In("Account requested.", at: t))!;
		NeuronNotification info = inbox.Add(In("XMPP Account created:", at: t.AddMinutes(1)))!;
		inbox.SetState(info.Id, AttentionState.Read);
		inbox.Add(In("Feedback received.", at: t.AddMinutes(2)));
		inbox.Add(In("API key requested.", at: t.AddMinutes(3)));

		Assert.Equal(3, inbox.Items.Count);
		Assert.Null(inbox.Get(info.Id));
		Assert.NotNull(inbox.Get(open.Id));
	}

	[Fact]
	public async Task StoreKeepsItemsAndState()
	{
		string file = Path.Combine(Path.GetTempPath(), "NeuroAA.Attention." + Guid.NewGuid().ToString("N"), "attention.json");
		JsonAttentionStore store = new(file);

		AttentionInbox inbox = new(store);
		NeuronNotification a = inbox.Add(In("Account requested.\n\n| Account request ||\n|:--|:--|\n| Name: | Kalle |"))!;
		inbox.SetState(a.Id, AttentionState.Handled);
		await inbox.DisposeAsync();

		AttentionInbox again = new(store);
		again.Load();
		NeuronNotification b = Assert.Single(again.Items);
		Assert.Equal(a.Id, b.Id);
		Assert.Equal(AttentionState.Handled, b.State);
		Assert.Equal(NotificationKind.AccountRequest, b.Kind);
		Assert.Equal("Kalle", b.Subject);
	}
}
