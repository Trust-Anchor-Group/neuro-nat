using Waher.Networking.XMPP.Concentrator.Queries;
using Waher.Networking.XMPP.DataForms;
using Waher.Things.Queries;

namespace Neuro.Nat.Engine.Concentrator;

public enum QueryRunState
{
	Running,
	Done,
	Aborted,
	Failed,
}

/// <summary>A message a query sent along with its results, and the section it was working on then.</summary>
public sealed record QueryMessage(DateTime Timestamp, QueryEventType Type, QueryEventLevel Level, string Text)
{
	/// <summary>The section the Neuron was filling when it sent the message; null for the report as a whole.</summary>
	public QuerySection? Section { get; init; }
}

/// <summary>
/// A query (report) running on a Neuron. Results arrive in sections, tables and objects, as
/// the Neuron sends them; <see cref="Changed"/> is raised for every update.
/// </summary>
public sealed class QueryRun : IDisposable
{
	private readonly List<QueryMessage> messages = [];
	private NodeQuery? query;
	private QuerySection? current;

	internal QueryRun(string commandName)
	{
		this.Title = commandName;
	}

	public string Title { get; private set; }
	public string? Status { get; private set; }
	public QueryRunState State { get; private set; } = QueryRunState.Running;
	public string? Error { get; private set; }

	/// <summary>The parameter form again, with remarks, when the Neuron did not accept the values.</summary>
	public DataForm? Rejected { get; private set; }

	/// <summary>Result tree: sections, tables and objects.</summary>
	public IReadOnlyList<QueryItem> Items => this.query?.Result ?? [];

	public IReadOnlyList<QueryMessage> Messages
	{
		get
		{
			lock (this.messages)
				return [.. this.messages];
		}
	}

	/// <summary>Total rows received in all tables.</summary>
	public int RowCount => Count(this.Items);

	/// <summary>Raised when anything arrives. Any thread.</summary>
	public event EventHandler? Changed;

	internal async Task AttachAsync(NodeQuery q)
	{
		this.query = q;

		q.NewTitle += (_, _) => { this.Title = string.IsNullOrWhiteSpace(q.Title) ? this.Title : q.Title; return this.Raise(); };
		q.Started += (_, _) => this.Raise();
		q.Done += (_, _) => { this.State = QueryRunState.Done; this.Status = null; return this.Raise(); };
		q.Aborted += (_, _) =>
		{
			if (this.State == QueryRunState.Running)
				this.State = QueryRunState.Aborted;
			return this.Raise();
		};
		q.TableAdded += (_, _) => this.Raise();
		q.TableUpdated += (_, _) => this.Raise();
		q.TableCompleted += (_, _) => this.Raise();
		q.ObjectAdded += (_, _) => this.Raise();
		q.SectionAdded += (_, e) => { this.current = e.Section; return this.Raise(); };
		q.SectionCompleted += (_, e) => { this.current = e.Section.Parent as QuerySection; return this.Raise(); };
		q.StatusMessageReceived += (_, e) => { this.Status = e.StatusMessage; return this.Raise(); };
		q.EventMessageReceived += (_, e) =>
		{
			lock (this.messages)
				this.messages.Add(new QueryMessage(DateTime.Now, e.EventType, e.EventLevel, e.EventMessage) { Section = this.current });
			return this.Raise();
		};

		// The query is paused until handlers are attached; whatever arrived meanwhile is replayed now.
		await q.ResumeAsync();
	}

	internal void Fail(DataForm? rejected, string error)
	{
		this.Rejected = rejected;
		this.Error = error;
		this.State = QueryRunState.Failed;
		this.Changed?.Invoke(this, EventArgs.Empty);
	}

	private Task Raise()
	{
		this.Changed?.Invoke(this, EventArgs.Empty);
		return Task.CompletedTask;
	}

	private static int Count(IEnumerable<QueryItem> items)
	{
		int n = 0;
		foreach (QueryItem i in items)
		{
			if (i is QueryTable t)
				n += t.Records.Length;
			else if (i is QuerySection s)
				n += Count(s.Content);
		}
		return n;
	}

	public void Dispose() => this.query?.Dispose();
}
