using Neuro.Nat.Engine.Sniffing;

namespace Neuro.Nat.Engine.Tests;

public class StreamSnifferTests
{
	// Sniffer processing goes through the Waher runtime; initialize it first so a cold
	// Types.Initialize in a parallel test class cannot stall these tests.
	public StreamSnifferTests() => EngineRuntime.Initialize();

	[Fact]
	public async Task KeepsOnlyTheNewestLinesInOrder()
	{
		StreamSniffer sniffer = new("test", capacity: 3);
		List<SnifferLine> live = [];
		sniffer.LineAdded += (_, l) => { lock (live) live.Add(l); };

		for (int i = 1; i <= 5; i++)
			sniffer.ReceiveText($"<m{i}/>");

		await WaitFor(() => { lock (live) return live.Count == 5; });

		IReadOnlyList<SnifferLine> kept = sniffer.Snapshot();
		Assert.Equal(["<m3/>", "<m4/>", "<m5/>"], kept.Select(l => l.Text));
		Assert.All(kept, l => Assert.Equal(SnifferDirection.Received, l.Direction));
		Assert.True(kept[0].Sequence < kept[1].Sequence && kept[1].Sequence < kept[2].Sequence);
	}

	[Fact]
	public async Task MapsDirections()
	{
		StreamSniffer sniffer = new("test");
		sniffer.TransmitText("<out/>");
		sniffer.Information("info");
		sniffer.Warning("warn");
		sniffer.Error("err");

		await WaitFor(() => sniffer.Snapshot().Count == 4);

		Assert.Equal(
			[SnifferDirection.Transmitted, SnifferDirection.Information, SnifferDirection.Warning, SnifferDirection.Error],
			sniffer.Snapshot().Select(l => l.Direction));
	}

	internal static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
	{
		DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		while (!condition())
		{
			if (DateTime.UtcNow > until)
				throw new TimeoutException("Condition not met in time.");
			await Task.Delay(10);
		}
	}
}
