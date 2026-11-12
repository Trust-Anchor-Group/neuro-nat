using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuro.Nat.Engine.Attention;

/// <summary>
/// Everything the Neurons told their administrators, typed and kept until a person has dealt
/// with it. Survives restarts through an optional store.
/// </summary>
public sealed class AttentionInbox : IAsyncDisposable
{
	private static readonly TimeSpan duplicateWindow = TimeSpan.FromMinutes(2);

	private readonly List<NeuronNotification> items = [];
	private readonly object sync = new();
	private readonly IAttentionStore? store;
	private readonly Timer saveTimer;
	private int dirty;

	public AttentionInbox(IAttentionStore? store = null)
	{
		this.store = store;
		this.saveTimer = new Timer(_ => this.SaveIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
	}

	/// <summary>Most items kept. Handled and read for-info items go first when it fills up.</summary>
	public int Capacity { get; init; } = 2000;

	/// <summary>Raised when an item is added, corrected or changes state. Any thread.</summary>
	public event EventHandler<NeuronNotification>? Changed;

	/// <summary>Raised after items were loaded or removed in bulk. Any thread.</summary>
	public event EventHandler? Reset;

	/// <summary>All items, newest first.</summary>
	public IReadOnlyList<NeuronNotification> Items
	{
		get
		{
			lock (this.sync)
				return [.. this.items.OrderByDescending(n => n.Updated ?? n.Received)];
		}
	}

	/// <summary>Items that still need a person.</summary>
	public int OpenCount
	{
		get
		{
			lock (this.sync)
				return this.items.Count(n => n.IsOpen);
		}
	}

	public int UnreadCount
	{
		get
		{
			lock (this.sync)
				return this.items.Count(n => n.State == AttentionState.Unread);
		}
	}

	public NeuronNotification? Get(string id)
	{
		lock (this.sync)
			return this.items.FirstOrDefault(n => n.Id == id);
	}

	/// <summary>
	/// Adds a notification, or applies it as a correction of an earlier one. Returns the item,
	/// or null when it is a copy of one that already arrived through another account.
	/// </summary>
	public NeuronNotification? Add(IncomingNotification m)
	{
		NeuronNotification? result = null;

		lock (this.sync)
		{
			int i = string.IsNullOrEmpty(m.Replaces) ? -1 : this.items.FindIndex(n => SameNeuron(n, m) && n.MessageId == m.Replaces);
			if (i >= 0)
			{
				NeuronNotification old = this.items[i];
				result = NeuronNotification.Classify(old with
				{
					Markdown = m.Markdown,
					Text = m.Text,
					Updated = m.Timestamp,
					State = old.State == AttentionState.Handled ? AttentionState.Handled : AttentionState.Unread,
				});
				this.items[i] = result;
			}
			else
			{
				if (!string.IsNullOrEmpty(m.MessageId) && this.items.Any(n => SameNeuron(n, m) && n.MessageId == m.MessageId))
					return null;

				if (this.items.Any(n => SameNeuron(n, m) && n.Markdown == m.Markdown && (n.Received - m.Timestamp).Duration() <= duplicateWindow))
					return null;

				result = NeuronNotification.From(m);
				this.items.Add(result);
				this.Trim();
			}
		}

		this.Touch();
		this.Changed?.Invoke(this, result);
		return result;
	}

	public void SetState(string id, AttentionState state)
	{
		NeuronNotification? changed = null;

		lock (this.sync)
		{
			int i = this.items.FindIndex(n => n.Id == id);
			if (i >= 0 && this.items[i].State != state)
			{
				changed = this.items[i] with
				{
					State = state,
					HandledAt = state == AttentionState.Handled ? DateTime.Now : null,
				};
				this.items[i] = changed;
			}
		}

		if (changed is not null)
		{
			this.Touch();
			this.Changed?.Invoke(this, changed);
		}
	}

	/// <summary>
	/// Moves a legal identity application to another review state. When it comes to need a person,
	/// it is unread again (unless handled). Returns the changed item, or null when nothing changed.
	/// </summary>
	public NeuronNotification? SetReview(string id, IdentityReview review, string? applicant = null)
	{
		NeuronNotification? changed = null;

		lock (this.sync)
		{
			int i = this.items.FindIndex(n => n.Id == id);
			if (i < 0)
				return null;

			NeuronNotification old = this.items[i];
			if (old.Kind != NotificationKind.LegalIdApplication)
				return null;

			string? who = string.IsNullOrWhiteSpace(applicant) ? old.Applicant : applicant;
			if (old.Review == review && old.Applicant == who)
				return null;

			changed = NeuronNotification.Classify(old with
			{
				Review = review,
				Applicant = who,
				State = review == IdentityReview.NeedsReview && old.Review != IdentityReview.NeedsReview && old.State != AttentionState.Handled
					? AttentionState.Unread
					: old.State,
			});
			this.items[i] = changed;
		}

		this.Touch();
		this.Changed?.Invoke(this, changed);
		return changed;
	}

	/// <summary>Marks every unread item read.</summary>
	public void MarkAllRead()
	{
		bool any = false;

		lock (this.sync)
		{
			for (int i = 0; i < this.items.Count; i++)
			{
				if (this.items[i].State == AttentionState.Unread)
				{
					this.items[i] = this.items[i] with { State = AttentionState.Read };
					any = true;
				}
			}
		}

		if (any)
		{
			this.Touch();
			this.Reset?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>Removes every handled item, and read items that never needed action.</summary>
	public int ClearDone()
	{
		int removed;

		lock (this.sync)
			removed = this.items.RemoveAll(n => n.State == AttentionState.Handled || (!n.NeedsAction && n.State == AttentionState.Read));

		if (removed > 0)
		{
			this.Touch();
			this.Reset?.Invoke(this, EventArgs.Empty);
		}

		return removed;
	}

	/// <summary>Loads stored items. Kind, title and fields are worked out again, so better rules apply to old items too.</summary>
	public void Load()
	{
		if (this.store is null)
			return;

		IReadOnlyList<NeuronNotification> loaded = this.store.Load();

		lock (this.sync)
		{
			this.items.Clear();
			this.items.AddRange(loaded.Select(NeuronNotification.Classify));
			this.Trim();
		}

		this.Reset?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Adds an item as it is, for previews and UI tests.</summary>
	internal void Preview(NeuronNotification n)
	{
		NeuronNotification c = NeuronNotification.Classify(n);
		lock (this.sync)
			this.items.Add(c);

		this.Changed?.Invoke(this, c);
	}

	private static bool SameNeuron(NeuronNotification n, IncomingNotification m)
		=> string.Equals(n.NeuronJid, m.NeuronJid, StringComparison.OrdinalIgnoreCase);

	private void Trim()
	{
		int excess = this.items.Count - this.Capacity;
		if (excess <= 0)
			return;

		foreach (NeuronNotification n in this.items
			.OrderBy(n => n.IsOpen ? 2 : n.State == AttentionState.Unread ? 1 : 0)
			.ThenBy(n => n.Received)
			.Take(excess)
			.ToList())
		{
			this.items.Remove(n);
		}
	}

	private void Touch()
	{
		if (this.store is null)
			return;

		Interlocked.Exchange(ref this.dirty, 1);
		this.saveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
	}

	private void SaveIfDirty()
	{
		if (this.store is null || Interlocked.Exchange(ref this.dirty, 0) == 0)
			return;

		List<NeuronNotification> snapshot;
		lock (this.sync)
			snapshot = [.. this.items];

		try
		{
			this.store.Save(snapshot);
		}
		catch (Exception)
		{
			// Try again with the next change.
			Interlocked.Exchange(ref this.dirty, 1);
		}
	}

	public ValueTask DisposeAsync()
	{
		this.saveTimer.Dispose();
		this.SaveIfDirty();
		return ValueTask.CompletedTask;
	}
}

/// <summary>Keeps Attention items between runs.</summary>
public interface IAttentionStore
{
	IReadOnlyList<NeuronNotification> Load();

	void Save(IReadOnlyList<NeuronNotification> items);
}

/// <summary>
/// Stores Attention items as JSON. Only what arrived and its state are stored; kind, title and
/// fields are worked out again on load.
/// </summary>
public sealed class JsonAttentionStore(string filePath) : IAttentionStore
{
	private static readonly JsonSerializerOptions options = new()
	{
		WriteIndented = false,
		Converters = { new JsonStringEnumConverter() },
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly object gate = new();

	public string FilePath { get; } = filePath;

	private sealed record Stored(
		string Id, Guid AccountId, string NeuronJid, string? MessageId, DateTime Received, DateTime? Updated,
		string Markdown, string Text, AttentionState State, DateTime? HandledAt,
		IdentityReview? Review = null, string? Applicant = null);

	public IReadOnlyList<NeuronNotification> Load()
	{
		lock (this.gate)
		{
			if (!File.Exists(this.FilePath))
				return [];

			try
			{
				List<Stored>? list = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(this.FilePath), options);
				return [.. (list ?? []).Select(s => new NeuronNotification
				{
					Id = s.Id,
					AccountId = s.AccountId,
					NeuronJid = s.NeuronJid,
					MessageId = s.MessageId,
					Received = s.Received,
					Updated = s.Updated,
					Markdown = s.Markdown,
					Text = s.Text,
					State = s.State,
					HandledAt = s.HandledAt,
					Review = s.Review,
					Applicant = s.Applicant,
				})];
			}
			catch (JsonException)
			{
				// A damaged file must not stop the app. Keep it aside for a look later.
				File.Copy(this.FilePath, this.FilePath + ".damaged", true);
				return [];
			}
		}
	}

	public void Save(IReadOnlyList<NeuronNotification> items)
	{
		lock (this.gate)
		{
			string? folder = Path.GetDirectoryName(this.FilePath);
			if (!string.IsNullOrEmpty(folder))
				Directory.CreateDirectory(folder);

			List<Stored> list = [.. items.Select(n => new Stored(n.Id, n.AccountId, n.NeuronJid, n.MessageId, n.Received, n.Updated, n.Markdown, n.Text, n.State, n.HandledAt, n.Review, n.Applicant))];
			string tmp = this.FilePath + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(list, options));
			File.Move(tmp, this.FilePath, true);
		}
	}
}
