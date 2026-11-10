using Waher.Networking.XMPP.Concentrator;
using Waher.Networking.XMPP.Concentrator.Queries;
using Waher.Networking.XMPP.DataForms;
using Waher.Things.Queries;

namespace Neuro.AA.Engine.Events;

/// <summary>
/// Runs a report (a query command) on a node of a Neuron's concentrator and hands each
/// result row to a callback, with the table's column ids. Waits while the Neuron keeps
/// sending and gives up after a period of silence.
/// </summary>
internal static class ReportQuery
{
	/// <summary>Outcome of a report.</summary>
	/// <param name="Rows">Rows received.</param>
	/// <param name="Complete">False if the Neuron went silent before finishing.</param>
	public sealed record Result(int Rows, bool Complete);

	public static async Task<Result> RunAsync(ConcentratorClient cc, string fullJid, string nodeId, string sourceId, string commandId,
		Func<DataForm, Task> setParameters, Action<object[], IReadOnlyDictionary<string, int>> onRow,
		TimeSpan silence, CancellationToken cancel = default)
	{
		DataForm form = await GetParametersAsync(cc, fullJid, nodeId, sourceId, commandId, cancel);
		await setParameters(form);

		int rows = 0;
		long lastActivity = Environment.TickCount64;
		TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		string? error = null;
		Dictionary<string, int> columns = [];

		NodeQuery query = await cc.ExecuteQuery(fullJid, nodeId, sourceId, string.Empty, commandId, form, "en",
			string.Empty, string.Empty, string.Empty, (_, e) =>
			{
				if (!e.Ok)
				{
					error = e.ErrorText;
					done.TrySetResult(false);
				}
				return Task.CompletedTask;
			}, null);

		query.TableAdded += (_, e) =>
		{
			Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
			Column[] cols = e.Table.TableDefinition.Columns;
			lock (columns)
			{
				columns.Clear();
				for (int i = 0; i < cols.Length; i++)
					columns[cols[i].ColumnId] = i;
			}
			return Task.CompletedTask;
		};

		query.TableUpdated += (_, e) =>
		{
			Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
			lock (columns)
			{
				foreach (Record r in e.NewRecords)
				{
					Interlocked.Increment(ref rows);
					onRow(r.Elements, columns);
				}
			}
			return Task.CompletedTask;
		};

		query.Done += (_, _) => { done.TrySetResult(true); return Task.CompletedTask; };
		query.Aborted += (_, _) => { error ??= "The Neuron aborted the report."; done.TrySetResult(false); return Task.CompletedTask; };

		// The query is paused until handlers are assigned; messages received so far are queued.
		await query.ResumeAsync();

		using (cancel.Register(() => done.TrySetCanceled()))
		{
			while (!done.Task.IsCompleted)
			{
				await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(1), cancel));
				if (!done.Task.IsCompleted && Environment.TickCount64 - Interlocked.Read(ref lastActivity) > silence.TotalMilliseconds)
					return new Result(rows, false);
			}
		}

		if (!await done.Task && error is not null && rows == 0)
			throw new InvalidOperationException(error);

		return new Result(rows, true);
	}

	private static async Task<DataForm> GetParametersAsync(ConcentratorClient cc, string fullJid, string nodeId, string sourceId,
		string commandId, CancellationToken cancel)
	{
		TaskCompletionSource<DataForm> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

		await cc.GetCommandParameters(fullJid, nodeId, sourceId, string.Empty, commandId, "en",
			string.Empty, string.Empty, string.Empty,
			(_, e) =>
			{
				if (e.Ok && e.Form is not null)
					tcs.TrySetResult(e.Form);
				else
					tcs.TrySetException(new InvalidOperationException(e.ErrorText ?? "The Neuron returned no parameters."));
				return Task.CompletedTask;
			},
			(_, e) =>
			{
				tcs.TrySetException(new InvalidOperationException(e.ErrorText ?? "The Neuron refused the report."));
				return Task.CompletedTask;
			}, null);

		using (cancel.Register(() => tcs.TrySetCanceled()))
		{
			Task finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30), cancel));
			if (finished != tcs.Task)
				throw new TimeoutException("The Neuron did not return the report parameters in time.");
		}

		return await tcs.Task;
	}

	/// <summary>Sets a form field if it exists.</summary>
	public static async Task Set(DataForm form, string var, string value)
	{
		if (form[var] is Field f)
			await f.SetValue(value);
	}
}
