using System.Text.RegularExpressions;
using Neuro.AA.Engine.Attention;

namespace Neuro.AA.Engine.Chat;

/// <summary>A reply from the Neuron to a console command. Progress replies are corrected in place.</summary>
public sealed record ConsoleResponse(string? MessageId, DateTime Timestamp, string Markdown, DateTime? Corrected = null);

/// <summary>A command sent to a Neuron's chat admin and what it answered.</summary>
public sealed class ConsoleEntry
{
	private readonly List<ConsoleResponse> responses = [];

	internal ConsoleEntry(string threadId, string command, string? broadcastId, bool hidden)
	{
		this.ThreadId = threadId;
		this.Command = command;
		this.BroadcastId = broadcastId;
		this.Hidden = hidden;
		this.Sent = DateTime.Now;
	}

	/// <summary>Sent with the command; the Neuron answers in the same thread, which ties replies to it.</summary>
	public string ThreadId { get; }

	public string Command { get; }
	public DateTime Sent { get; }

	/// <summary>Set when the command was sent to several Neurons at once.</summary>
	public string? BroadcastId { get; }

	/// <summary>Sent by the app itself (such as reading the command list); not shown.</summary>
	public bool Hidden { get; }

	public string? Error { get; internal set; }

	public IReadOnlyList<ConsoleResponse> Responses
	{
		get
		{
			lock (this.responses)
				return [.. this.responses];
		}
	}

	internal void Add(ConsoleResponse r)
	{
		lock (this.responses)
		{
			if (r.MessageId is not null)
			{
				int i = this.responses.FindIndex(x => x.MessageId == r.MessageId);
				if (i >= 0)
				{
					this.responses[i] = r with { Timestamp = this.responses[i].Timestamp, Corrected = r.Timestamp };
					return;
				}
			}

			this.responses.Add(r);
		}
	}

	internal bool TryCorrect(string messageId, string markdown, DateTime when)
	{
		lock (this.responses)
		{
			int i = this.responses.FindIndex(x => x.MessageId == messageId);
			if (i < 0)
				return false;

			this.responses[i] = this.responses[i] with { Markdown = markdown, Corrected = when };
			return true;
		}
	}
}

/// <summary>
/// The chat admin console of one Neuron, through one account: commands go to the Neuron's own
/// client as chat, each in its own thread, and the replies come back in that thread.
/// </summary>
public sealed partial class ConsoleSession
{
	private readonly List<ConsoleEntry> entries = [];
	private readonly Func<string?> fullJid;
	private readonly Func<string, string, string, Task> send;
	private string[] commands = [];

	internal ConsoleSession(Guid accountId, string neuronJid, Func<string?> fullJid, Func<string, string, string, Task> send)
	{
		this.AccountId = accountId;
		this.NeuronJid = neuronJid;
		this.fullJid = fullJid;
		this.send = send;
	}

	public Guid AccountId { get; }
	public string NeuronJid { get; }

	/// <summary>Commands the Neuron lists in its help, for suggestions.</summary>
	public IReadOnlyList<string> Commands => this.commands;

	/// <summary>Raised when an entry is added or a reply arrives. Any thread.</summary>
	public event EventHandler<ConsoleEntry>? Changed;

	/// <summary>Raised when the command list has been read.</summary>
	public event EventHandler? CommandsLoaded;

	public IReadOnlyList<ConsoleEntry> Entries
	{
		get
		{
			lock (this.entries)
				return [.. this.entries.Where(e => !e.Hidden)];
		}
	}

	/// <summary>True when the Neuron is online through the account, so commands can be sent.</summary>
	public bool IsAvailable => this.fullJid() is not null;

	/// <summary>Sends a command or script. Returns its entry; replies are added as they arrive.</summary>
	public async Task<ConsoleEntry> SendAsync(string command, string? broadcastId = null, bool hidden = false)
	{
		ConsoleEntry e = new("naa-" + Guid.NewGuid().ToString("N")[..12], command, broadcastId, hidden);
		lock (this.entries)
			this.entries.Add(e);

		string? to = this.fullJid();
		if (to is null)
			e.Error = "The Neuron is not online through this account.";
		else
		{
			try
			{
				await this.send(to, command, e.ThreadId);
			}
			catch (Exception ex)
			{
				e.Error = ex.Message;
			}
		}

		this.Changed?.Invoke(this, e);
		return e;
	}

	/// <summary>
	/// Sends a command without showing it in the console and returns the first answer, or null
	/// when the Neuron does not answer in time. Used by other workspaces (identities, topology).
	/// </summary>
	public async Task<string?> AskAsync(string command, TimeSpan timeout)
	{
		TaskCompletionSource<string> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
		ConsoleEntry? entry = null;

		void OnChanged(object? sender, ConsoleEntry e)
		{
			if (ReferenceEquals(e, entry) && e.Responses.Count > 0)
				answer.TrySetResult(e.Responses[0].Markdown);
		}

		this.Changed += OnChanged;
		try
		{
			entry = await this.SendAsync(command, hidden: true);
			if (entry.Error is not null)
				return null;
			if (entry.Responses.Count > 0)
				return entry.Responses[0].Markdown;

			return await answer.Task.WaitAsync(timeout);
		}
		catch (TimeoutException)
		{
			return null;
		}
		finally
		{
			this.Changed -= OnChanged;
		}
	}

	/// <summary>
	/// Sends a command without showing it in the console and waits until the Neuron stops
	/// answering: every reply and correction, in order, once nothing new has come for
	/// <paramref name="quiet"/>. Empty when nothing came in <paramref name="timeout"/>.
	/// For commands that report progress (check, install).
	/// </summary>
	public async Task<IReadOnlyList<string>> AskUntilQuietAsync(string command, TimeSpan quiet, TimeSpan timeout)
	{
		ConsoleEntry entry = await this.SendAsync(command, hidden: true);
		if (entry.Error is not null)
			return [];

		DateTime start = DateTime.UtcNow;
		DateTime last = start;
		int seen = -1;
		string signature = string.Empty;

		while (DateTime.UtcNow - start < timeout)
		{
			IReadOnlyList<ConsoleResponse> r = entry.Responses;
			string now = string.Join("\u0001", r.Select(x => x.Markdown));
			if (r.Count != seen || now != signature)
			{
				seen = r.Count;
				signature = now;
				last = DateTime.UtcNow;
			}
			else if (r.Count > 0 && DateTime.UtcNow - last >= quiet)
				break;

			await Task.Delay(200);
		}

		return [.. entry.Responses.Select(x => x.Markdown)];
	}

	/// <summary>Reads the Neuron's command list from its help, once.</summary>
	public async Task LoadCommandsAsync()
	{
		if (this.commands.Length > 0 || !this.IsAvailable)
			return;

		await this.SendAsync("help", hidden: true);
	}

	/// <summary>Handles a reply from the Neuron. Returns false when it does not belong to this console.</summary>
	internal bool Receive(IncomingNotification n, string? threadId)
	{
		ConsoleEntry? target = null;

		lock (this.entries)
		{
			if (n.Replaces is string replaces)
			{
				foreach (ConsoleEntry e in this.entries)
				{
					if (e.TryCorrect(replaces, n.Markdown, n.Timestamp))
					{
						target = e;
						break;
					}
				}
			}

			// Only replies in a command's thread belong here; anything else is ordinary chat with the Neuron.
			if (target is null && !string.IsNullOrEmpty(threadId))
			{
				target = this.entries.LastOrDefault(e => e.ThreadId == threadId);
				target?.Add(new ConsoleResponse(n.Replaces ?? n.MessageId, n.Timestamp, n.Markdown));
			}
		}

		if (target is null)
			return false;

		if (target.Hidden && target.Command == "help")
			this.ReadCommands(n.Markdown);

		this.Changed?.Invoke(this, target);
		return true;
	}

	private void ReadCommands(string markdown)
	{
		string[] found = [.. Backticked().Matches(markdown).Select(m => m.Groups[1].Value)
			.Where(s => s.Length > 0 && !s.Contains(' ') && s != "COMMAND")
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];

		if (found.Length > 3)
		{
			this.commands = found;
			this.CommandsLoaded?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>Commands that change or stop the Neuron. The console asks for a typed confirmation first.</summary>
	public static bool ChangesTheNeuron(string command)
	{
		string first = command.TrimStart().Split([' ', '\t', '\r', '\n'], 2)[0].ToLowerInvariant();
		return first is "reboot" or "stop" or "repair" or "reset" or "remove" or "disable" or "block" or "install" or "update" or "cmd";
	}

	[GeneratedRegex(@"`([^`\r\n]+)`")]
	private static partial Regex Backticked();
}
