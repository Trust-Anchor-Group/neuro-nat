using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Sniffing;

namespace Neuro.Nat.Engine.Tests;

public class EventTests
{
	private static readonly EventSource source = new(Guid.Empty, "neuron@alpha");

	private static SnifferLine Line(SnifferDirection d, string text, DateTime? at = null)
		=> new(1, at ?? new DateTime(2026, 11, 9, 14, 2, 0), d, text);

	[Fact]
	public void ParsesFieldsAndTagsFromSniffedEvent()
	{
		// Exactly the shape the Neuron's EventSinkToSniffer produces (seen on the local test Neuron).
		string text = "Script evaluated.\n\nObject: /Evaluate\nActor: neuronode\nEventId: ScriptEval\nRemoteEndPoint: [::1]:61959\nScript: LogError(\"x\")";

		NeuronEvent? e = SniffedEventParser.Parse(Line(SnifferDirection.Information, text), source);

		Assert.NotNull(e);
		Assert.Equal(EventSeverity.Info, e!.Severity);
		Assert.Equal("Script evaluated.", e.Message);
		Assert.Equal("/Evaluate", e.Object);
		Assert.Equal("neuronode", e.Actor);
		Assert.Equal("ScriptEval", e.EventId);
		Assert.Equal(["RemoteEndPoint", "Script"], e.Tags.Select(t => t.Key));
		Assert.Equal("[::1]:61959", e.Tags[0].Value);
	}

	[Fact]
	public void KeepsMultiLineStackTraceTogether()
	{
		string text = "Connection reset by peer\n\nModule: Waher.Networking\nStackTrace:    at A.B.C()\n   at D.E.F() in X.cs:line 12\n   --- End of inner exception stack trace ---\n   at G.H()\nEndpoint: 10.0.0.1:5269";

		NeuronEvent e = SniffedEventParser.Parse(Line(SnifferDirection.Error, text), source)!;

		Assert.Equal(EventSeverity.Error, e.Severity);
		Assert.Equal("Waher.Networking", e.Module);
		Assert.Contains("at D.E.F()", e.StackTrace);
		Assert.Contains("at G.H()", e.StackTrace);
		Assert.Equal("Endpoint", Assert.Single(e.Tags).Key);
	}

	[Fact]
	public void MapsSnifferClassesToSeverity()
	{
		Assert.Equal(EventSeverity.Warning, SniffedEventParser.Parse(Line(SnifferDirection.Warning, "w"), source)!.Severity);
		Assert.Equal(EventSeverity.Critical, SniffedEventParser.Parse(Line(SnifferDirection.Exception, "c"), source)!.Severity);
		Assert.Null(SniffedEventParser.Parse(Line(SnifferDirection.Received, "<x/>"), source));
	}

	[Fact]
	public void SameProblemSameFingerprintAcrossValues()
	{
		NeuronEvent a = SniffedEventParser.Parse(Line(SnifferDirection.Error, "SocketException: Connection reset by peer (beta.lab.local:5269) after 3 retries, id 0f8fad5b-d9cb-469f-a165-70867728950e"), source)!;
		NeuronEvent b = SniffedEventParser.Parse(Line(SnifferDirection.Error, "SocketException: Connection reset by peer (gamma.lab.local:5269) after 17 retries, id 7c9e6679-7425-40de-944b-e07fc1f90ae7"), new EventSource(Guid.Empty, "neuron@beta"))!;
		NeuronEvent c = SniffedEventParser.Parse(Line(SnifferDirection.Error, "TimeoutException in ReadSensorData"), source)!;

		Assert.Equal(a.Fingerprint, b.Fingerprint);
		Assert.NotEqual(a.Fingerprint, c.Fingerprint);
	}

	[Fact]
	public void HubDropsDuplicatesAndGroupsIssues()
	{
		EventHub hub = new(capacity: 100);
		DateTime t = new(2026, 11, 9, 14, 2, 0);

		NeuronEvent e1 = SniffedEventParser.Parse(Line(SnifferDirection.Error, "Connection reset by peer 1", t), source)!;
		NeuronEvent e2 = SniffedEventParser.Parse(Line(SnifferDirection.Error, "Connection reset by peer 2", t.AddSeconds(1)), new EventSource(Guid.Empty, "neuron@beta"))!;
		NeuronEvent info = SniffedEventParser.Parse(Line(SnifferDirection.Information, "Started", t), source)!;

		Assert.True(hub.Add(e1));
		Assert.False(hub.Add(e1));  // Same event again (for example after a sniffer renewal).
		Assert.True(hub.Add(e2));
		Assert.True(hub.Add(info));

		Assert.Equal(3, hub.Count);
		Issue issue = Assert.Single(hub.Issues());   // Informational events are not issues.
		Assert.Equal(2, issue.Count);
		Assert.Equal(2, issue.Neurons.Count);
		Assert.Equal(2, issue.CountSince(t.AddMinutes(1), TimeSpan.FromMinutes(5)));
	}

	[Fact]
	public void ResolvedIssueReopensWhenItComesBack()
	{
		EventHub hub = new();
		DateTime t = DateTime.Now;
		NeuronEvent e1 = SniffedEventParser.Parse(Line(SnifferDirection.Error, "Backup failed", t), source)!;
		hub.Add(e1);
		hub.SetState(e1.Fingerprint, IssueState.Resolved);

		hub.Add(SniffedEventParser.Parse(Line(SnifferDirection.Error, "Backup failed", t.AddMinutes(5)), source)!);

		Issue issue = Assert.Single(hub.Issues());
		Assert.Equal(IssueState.Open, issue.State);
		Assert.True(issue.Reopened);
	}
}
