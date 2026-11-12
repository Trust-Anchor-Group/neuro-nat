using Waher.Networking.Sniffers;
using Waher.Networking.Sniffers.Model;

namespace Neuro.Nat.Engine.Sniffing;

/// <summary>
/// Sniffer that keeps a bounded ring of recent lines and raises <see cref="LineAdded"/> for
/// live viewers. Attach it to any Waher communication layer (XMPP client, concentrator node).
/// </summary>
public sealed class StreamSniffer : SnifferBase
{
	private readonly object ringLock = new();
	private readonly SnifferLine[] ring;
	private int start;
	private int count;
	private long sequence;

	/// <param name="capacity">Number of lines kept for viewers that open later.</param>
	public StreamSniffer(string name, int capacity = 5000)
		: base(name)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
		this.ring = new SnifferLine[capacity];
	}

	/// <summary>Raised for every new line, on the sniffer's processing thread.</summary>
	public event EventHandler<SnifferLine>? LineAdded;

	/// <summary>Binary data is summarized by size; XMPP is text.</summary>
	public override BinaryPresentationMethod BinaryPresentationMethod => BinaryPresentationMethod.ByteCount;

	/// <summary>Copy of the lines currently kept, oldest first.</summary>
	public IReadOnlyList<SnifferLine> Snapshot()
	{
		lock (this.ringLock)
		{
			SnifferLine[] result = new SnifferLine[this.count];
			for (int i = 0; i < this.count; i++)
				result[i] = this.ring[(this.start + i) % this.ring.Length];
			return result;
		}
	}

	private Task Add(DateTime timestamp, SnifferDirection direction, string text)
	{
		SnifferLine line;

		lock (this.ringLock)
		{
			line = new SnifferLine(++this.sequence, timestamp, direction, text);

			if (this.count < this.ring.Length)
				this.ring[(this.start + this.count++) % this.ring.Length] = line;
			else
			{
				this.ring[this.start] = line;
				this.start = (this.start + 1) % this.ring.Length;
			}
		}

		this.LineAdded?.Invoke(this, line);
		return Task.CompletedTask;
	}

	private static string Binary(SnifferBinaryEvent e) => $"[{e.Count} bytes of binary data]";

	public override Task Process(SnifferRxBinary Event, CancellationToken Cancel) => this.Add(Event.Timestamp, SnifferDirection.Received, Binary(Event));
	public override Task Process(SnifferTxBinary Event, CancellationToken Cancel) => this.Add(Event.Timestamp, SnifferDirection.Transmitted, Binary(Event));
	public override Task Process(SnifferRxText Event, CancellationToken Cancel) => this.Add(Event.Timestamp, SnifferDirection.Received, Event.Text);
	public override Task Process(SnifferTxText Event, CancellationToken Cancel) => this.Add(Event.Timestamp, SnifferDirection.Transmitted, Event.Text);
	public override Task Process(SnifferInformation Event, CancellationToken Cancel) => this.Add(Event.Timestamp, SnifferDirection.Information, Event.Text);
	public override Task Process(SnifferWarning Event, CancellationToken Cancel) => this.Add(Event.Timestamp, SnifferDirection.Warning, Event.Text);
	public override Task Process(SnifferError Event, CancellationToken Cancel) => this.Add(Event.Timestamp, SnifferDirection.Error, Event.Text);
	public override Task Process(SnifferException Event, CancellationToken Cancel) => this.Add(Event.Timestamp, SnifferDirection.Exception, Event.Text);
}
