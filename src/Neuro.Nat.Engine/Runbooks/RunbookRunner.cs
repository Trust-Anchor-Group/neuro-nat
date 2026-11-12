using System.Text.Json.Serialization;

namespace Neuro.Nat.Engine.Runbooks;

/// <summary>A Neuron a runbook runs on, through one account.</summary>
/// <param name="Name">The Neuron's name on screen (<see cref="Events.NeuronNames"/>).</param>
/// <param name="Account">The short label of the account the commands go through.</param>
public sealed record RunbookTarget(Guid AccountId, string Jid, string Name, string Account = "");

/// <summary>What a Neuron answered to a command. <paramref name="Failure"/> says why it failed, when it did.</summary>
public sealed record RunbookAnswer(string Markdown, string? Failure = null);

/// <summary>Sends a runbook's commands. The console does it for real; tests use stand-in Neurons.</summary>
public interface IRunbookSender
{
	/// <summary>True when the Neuron is online through the account.</summary>
	bool IsOnline(RunbookTarget target);

	/// <summary>Sends one command and waits for the whole answer.</summary>
	Task<RunbookAnswer> SendAsync(RunbookTarget target, string command, CancellationToken cancel);
}

/// <summary>A marked step about to run: what it sends and to which Neurons. Asked every time.</summary>
public sealed record RunbookAsk(int Number, RunbookStep Step, IReadOnlyList<RunbookTarget> Neurons, IReadOnlyList<string> Commands);

/// <summary>Where one step stands on one Neuron.</summary>
public enum RunbookCellState
{
	/// <summary>Not run (yet, or at all, when the runbook stopped earlier for that Neuron).</summary>
	NotRun,

	/// <summary>Waiting for a marked step to be confirmed.</summary>
	Asking,

	Running,
	Done,
	Failed,
}

/// <summary>One step on one Neuron, as it runs.</summary>
public sealed record RunbookCell(RunbookCellState State, string Command, string Markdown = "", string? Failure = null);

/// <summary>The answer to one step on one Neuron, as kept.</summary>
public sealed record RunbookStepRecord(int Number, string Command, string Answer, string? Failure = null);

/// <summary>How a run went on one Neuron.</summary>
public sealed record RunbookNeuronRecord(Guid AccountId, string Jid, string Name, string Account, int? StoppedAt, string? Reason, IReadOnlyList<RunbookStepRecord> Steps)
{
	[JsonIgnore]
	public bool Done => this.StoppedAt is null;

	/// <summary>"Done" or "Stopped at step 3: No answer".</summary>
	[JsonIgnore]
	public string Outcome => this.Done ? "Done" : $"Stopped at step {this.StoppedAt}: {this.Reason}";
}

/// <summary>What a run left behind: who, when, on which Neurons, and the answer to each step.</summary>
public sealed record RunbookRecord
{
	public Guid Id { get; init; } = Guid.NewGuid();
	public Guid RunbookId { get; init; }
	public string Title { get; init; } = string.Empty;

	/// <summary>The steps as they were when it ran.</summary>
	public IReadOnlyList<RunbookStep> Steps { get; init; } = [];

	public DateTime Started { get; init; }
	public DateTime Finished { get; init; }
	public IReadOnlyList<RunbookNeuronRecord> Neurons { get; init; } = [];

	/// <summary>The accounts the commands went through.</summary>
	[JsonIgnore]
	public IReadOnlyList<string> Accounts => [.. this.Neurons.Select(n => n.Account).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// Runs a runbook on several Neurons, step by step: each step goes to every Neuron still going,
/// and the next step starts when all have answered. A failure stops the runbook for that Neuron
/// only. A marked step asks first, every time, and names the Neurons it goes to.
/// </summary>
public sealed class RunbookRunner(IRunbookSender sender)
{
	/// <summary>Asks about a marked step. False, or no answer, stops the run.</summary>
	public Func<RunbookAsk, CancellationToken, Task<bool>> Ask { get; init; } = (_, _) => Task.FromResult(false);

	/// <summary>How the runner waits. Tests make time pass at once.</summary>
	public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

	public TimeSpan PollEvery { get; init; } = TimeSpan.FromSeconds(1);

	/// <summary>"Wait until back": how long a Neuron has to go away before it counts as back already.</summary>
	public TimeSpan GoneWithin { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>"Wait until back": how long it has to come back.</summary>
	public TimeSpan BackWithin { get; init; } = TimeSpan.FromMinutes(10);

	/// <summary>Raised as a step changes on a Neuron: step index, Neuron index, where it stands. Any thread.</summary>
	public event Action<int, int, RunbookCell>? CellChanged;

	/// <summary>The dry run: exactly what each step sends to each Neuron (step, then Neuron). Sends nothing and asks nothing.</summary>
	public static IReadOnlyList<IReadOnlyList<string>> Plan(Runbook runbook, IReadOnlyList<RunbookTarget> targets, IReadOnlyDictionary<string, string> values)
		=> [.. runbook.Steps.Select(s => (IReadOnlyList<string>)[.. targets.Select(t => s.Fill(values, t.Jid))])];

	public async Task<RunbookRecord> RunAsync(Runbook runbook, IReadOnlyList<RunbookTarget> targets, IReadOnlyDictionary<string, string> values, CancellationToken cancel = default)
	{
		DateTime started = DateTime.Now;
		int n = targets.Count;
		int?[] stoppedAt = new int?[n];
		string?[] reason = new string?[n];
		List<RunbookStepRecord>[] answers = [.. Enumerable.Range(0, n).Select(_ => new List<RunbookStepRecord>())];

		for (int i = 0; i < runbook.Steps.Count; i++)
		{
			RunbookStep step = runbook.Steps[i];
			int[] active = [.. Enumerable.Range(0, n).Where(k => stoppedAt[k] is null)];
			if (active.Length == 0)
				break;

			string[] commands = [.. targets.Select(t => step.Fill(values, t.Jid))];

			if (cancel.IsCancellationRequested)
			{
				foreach (int k in active)
					(stoppedAt[k], reason[k]) = (i + 1, "Stopped by you");
				break;
			}

			if (step.Marked)
			{
				foreach (int k in active)
					this.Raise(i, k, new RunbookCell(RunbookCellState.Asking, commands[k]));

				bool yes;
				try
				{
					yes = await this.Ask(new RunbookAsk(i + 1, step, [.. active.Select(k => targets[k])], [.. active.Select(k => commands[k])]), cancel);
				}
				catch (OperationCanceledException)
				{
					yes = false;
				}

				if (!yes)
				{
					string why = cancel.IsCancellationRequested ? "Stopped by you" : "Not confirmed";
					foreach (int k in active)
					{
						(stoppedAt[k], reason[k]) = (i + 1, why);
						this.Raise(i, k, new RunbookCell(RunbookCellState.Failed, commands[k], Failure: why));
					}
					break;
				}
			}

			int index = i;
			await Task.WhenAll(active.Select(async k =>
			{
				RunbookStepRecord r = await this.StepAsync(index, k, step, targets[k], commands[k], cancel);
				answers[k].Add(r);
				if (r.Failure is not null)
					(stoppedAt[k], reason[k]) = (index + 1, r.Failure);
			}));
		}

		return new RunbookRecord
		{
			RunbookId = runbook.Id,
			Title = runbook.Title,
			Steps = runbook.Steps,
			Started = started,
			Finished = DateTime.Now,
			Neurons = [.. targets.Select((t, k) => new RunbookNeuronRecord(t.AccountId, t.Jid, t.Name, t.Account, stoppedAt[k], reason[k], answers[k]))],
		};
	}

	private async Task<RunbookStepRecord> StepAsync(int step, int neuron, RunbookStep what, RunbookTarget target, string command, CancellationToken cancel)
	{
		this.Raise(step, neuron, new RunbookCell(RunbookCellState.Running, command));
		string answer = string.Empty;
		string? failure;

		try
		{
			switch (what.Kind)
			{
				case RunbookStepKind.Wait:
					await this.Delay(TimeSpan.FromSeconds(what.Seconds), cancel);
					failure = null;
					break;

				case RunbookStepKind.WaitUntilBack:
					failure = await this.WaitUntilBackAsync(target, cancel);
					answer = failure is null ? "Online" : string.Empty;
					break;

				default:
					if (!sender.IsOnline(target))
					{
						failure = "Not online";
						break;
					}

					RunbookAnswer a = await sender.SendAsync(target, command, cancel);
					answer = a.Markdown;
					failure = a.Failure ?? FailureIn(a.Markdown);
					break;
			}
		}
		catch (OperationCanceledException)
		{
			failure = "Stopped by you";
		}
		catch (Exception ex)
		{
			failure = ex.Message;
		}

		this.Raise(step, neuron, new RunbookCell(failure is null ? RunbookCellState.Done : RunbookCellState.Failed, command, answer, failure));
		return new RunbookStepRecord(step + 1, command, answer, failure);
	}

	/// <summary>Waits until the Neuron has gone away and come back. Null when it is back, else why not.</summary>
	private async Task<string?> WaitUntilBackAsync(RunbookTarget target, CancellationToken cancel)
	{
		// First it should go away. When it does not within a while, it restarted faster than we looked, or not at all: either way it is there.
		TimeSpan waited = TimeSpan.Zero;
		while (sender.IsOnline(target) && waited < this.GoneWithin)
		{
			await this.Delay(this.PollEvery, cancel);
			waited += this.PollEvery;
		}

		waited = TimeSpan.Zero;
		while (!sender.IsOnline(target))
		{
			if (waited >= this.BackWithin)
				return "Not back after " + RunbookStep.Duration((int)this.BackWithin.TotalSeconds);

			await this.Delay(this.PollEvery, cancel);
			waited += this.PollEvery;
		}

		return null;
	}

	private void Raise(int step, int neuron, RunbookCell cell) => this.CellChanged?.Invoke(step, neuron, cell);

	/// <summary>
	/// The reason in an answer the Neuron marks as an error (its chat admin wraps errors in
	/// <c>&lt;font class="error"&gt;</c>), or null when the answer is not one.
	/// </summary>
	public static string? FailureIn(string? markdown)
	{
		const string open = "<font class=\"error\">";
		if (string.IsNullOrEmpty(markdown))
			return null;

		int at = markdown.IndexOf(open, StringComparison.OrdinalIgnoreCase);
		if (at < 0)
			return null;

		string inner = markdown[(at + open.Length)..];
		int end = inner.IndexOf("</font>", StringComparison.OrdinalIgnoreCase);
		if (end >= 0)
			inner = inner[..end];

		// The message, not the stack trace under it; output in a code block only when that is all there is.
		bool inCode = false;
		string? firstCode = null;
		foreach (string raw in inner.Replace("\r\n", "\n").Split('\n'))
		{
			string line = raw.Trim();
			if (line.StartsWith("```", StringComparison.Ordinal))
			{
				inCode = !inCode;
				continue;
			}

			if (line.Length == 0)
				continue;

			if (inCode)
			{
				firstCode ??= line;
				continue;
			}

			return Short(MarkdownUnescape(line));
		}

		return firstCode is null ? "The Neuron answered with an error" : Short(firstCode);

		static string Short(string s) => s.Length > 160 ? s[..157] + "..." : s;

		// The Neuron escapes its text for Markdown ("denied\." and "C:\\Data"): the reason is shown as plain text.
		static string MarkdownUnescape(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\\(.)", "$1");
	}
}
