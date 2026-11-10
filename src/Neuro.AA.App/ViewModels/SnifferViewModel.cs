using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Sniffing;

namespace Neuro.AA.App.ViewModels;

/// <summary>
/// Live view of a sniffer. Lines arrive on network threads and are flushed to the UI in
/// batches a few times per second, so a chatty connection cannot flood the UI thread.
/// </summary>
public sealed partial class SnifferViewModel : ObservableObject, IDisposable
{
	private const int MaxLines = 2000;
	private readonly StreamSniffer sniffer;
	private readonly ConcurrentQueue<SnifferLine> pending = new();
	private readonly DispatcherTimer flush;
	private long lastSequence;

	public SnifferViewModel(StreamSniffer sniffer)
	{
		this.sniffer = sniffer;

		foreach (SnifferLine l in sniffer.Snapshot())
			this.pending.Enqueue(l);

		this.IsEmpty = this.pending.IsEmpty;

		sniffer.LineAdded += this.Sniffer_LineAdded;
		this.flush = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => this.Flush());
		this.flush.Start();
	}

	/// <summary>Captured lines, oldest first. Batches go in as one change, so busy sniffers do not stall the list.</summary>
	public Avalonia.Collections.AvaloniaList<SnifferLine> Lines { get; } = [];

	[ObservableProperty]
	private bool isPaused;

	/// <summary>True when nothing has been captured (or the view was cleared).</summary>
	[ObservableProperty]
	private bool isEmpty = true;

	private void Sniffer_LineAdded(object? sender, SnifferLine e) => this.pending.Enqueue(e);

	/// <summary>Moves pending lines into <see cref="Lines"/> now instead of on the next tick.</summary>
	public void Flush()
	{
		if (this.IsPaused || this.pending.IsEmpty)
			return;

		List<SnifferLine> batch = [];
		while (this.pending.TryDequeue(out SnifferLine? line))
		{
			if (line.Sequence <= this.lastSequence)
				continue;  // Already shown (snapshot and live event can overlap).

			this.lastSequence = line.Sequence;
			batch.Add(line);
		}

		if (batch.Count > MaxLines)
			batch.RemoveRange(0, batch.Count - MaxLines);

		int overflow = this.Lines.Count + batch.Count - MaxLines;
		if (overflow > 0)
			this.Lines.RemoveRange(0, Math.Min(overflow, this.Lines.Count));
		this.Lines.AddRange(batch);

		this.IsEmpty = this.Lines.Count == 0;
	}

	[RelayCommand]
	private void Clear()
	{
		this.Lines.Clear();
		this.IsEmpty = true;
	}

	[RelayCommand]
	private void TogglePause() => this.IsPaused = !this.IsPaused;

	public void Dispose()
	{
		this.sniffer.LineAdded -= this.Sniffer_LineAdded;
		this.flush.Stop();
	}
}
