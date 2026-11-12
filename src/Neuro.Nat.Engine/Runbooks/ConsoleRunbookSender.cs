using Neuro.Nat.Engine.Chat;

namespace Neuro.Nat.Engine.Runbooks;

/// <summary>
/// Sends a runbook's commands through the console of each Neuron, as the console itself does,
/// and takes the answer once the Neuron has stopped adding to it.
/// </summary>
public sealed class ConsoleRunbookSender(ChatService chat) : IRunbookSender
{
	/// <summary>How long a command may go without any answer.</summary>
	public TimeSpan AnswerWithin { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>An answer is whole when nothing new has come for this long.</summary>
	public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(2);

	public bool IsOnline(RunbookTarget target) => chat.Console(target.AccountId, target.Jid).IsAvailable;

	public async Task<RunbookAnswer> SendAsync(RunbookTarget target, string command, CancellationToken cancel)
	{
		ConsoleSession session = chat.Console(target.AccountId, target.Jid);
		ConsoleEntry entry = await session.SendAsync(command, hidden: true);
		if (entry.Error is not null)
			return new RunbookAnswer(string.Empty, entry.Error);

		DateTime start = DateTime.UtcNow, last = start;
		string seen = string.Empty;
		while (true)
		{
			cancel.ThrowIfCancellationRequested();
			IReadOnlyList<ConsoleResponse> r = entry.Responses;
			string now = string.Join("\n\n", r.Select(x => x.Markdown));
			if (now != seen)
			{
				seen = now;
				last = DateTime.UtcNow;
			}
			else if (r.Count > 0 && DateTime.UtcNow - last >= this.Quiet)
				return new RunbookAnswer(now);

			if (r.Count == 0 && DateTime.UtcNow - start >= this.AnswerWithin)
			{
				// A Neuron told to restart may go before it answers: that is not a failure.
				return ConsoleSession.ChangesTheNeuron(command)
					? new RunbookAnswer(string.Empty)
					: new RunbookAnswer(string.Empty, "No answer in " + RunbookStep.Duration((int)this.AnswerWithin.TotalSeconds));
			}

			await Task.Delay(200, cancel);
		}
	}
}
