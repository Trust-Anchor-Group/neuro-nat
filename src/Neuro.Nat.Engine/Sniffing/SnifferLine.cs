namespace Neuro.Nat.Engine.Sniffing;

/// <summary>What a sniffed line represents.</summary>
public enum SnifferDirection
{
	Received,
	Transmitted,
	Information,
	Warning,
	Error,
	Exception,
}

/// <summary>One line of sniffed communication or a comment from the protocol stack.</summary>
/// <param name="Sequence">Monotonic number within one sniffer, for stable ordering and de-duplication.</param>
public sealed record SnifferLine(long Sequence, DateTime Timestamp, SnifferDirection Direction, string Text);
