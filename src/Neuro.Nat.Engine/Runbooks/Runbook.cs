using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Neuro.Nat.Engine.Chat;

namespace Neuro.Nat.Engine.Runbooks;

/// <summary>What a step of a runbook does.</summary>
public enum RunbookStepKind
{
	/// <summary>Sends a console command (or script) to each Neuron.</summary>
	Command,

	/// <summary>Waits a number of seconds on each Neuron.</summary>
	Wait,

	/// <summary>Waits until each Neuron has gone and come back online (after a restart).</summary>
	WaitUntilBack,
}

/// <summary>
/// One step of a runbook. A command may hold blanks such as <c>{account}</c>, asked for when the
/// runbook runs. A marked step asks first, and names the Neurons, every time it is about to run.
/// </summary>
public sealed partial record RunbookStep(RunbookStepKind Kind, string Command = "", int Seconds = 0, bool Marked = false)
{
	/// <summary>The blank that is filled with each Neuron's own domain; never asked for.</summary>
	public const string NeuronBlank = "neuron";

	public static RunbookStep Send(string command, bool marked = false) => new RunbookStep(RunbookStepKind.Command, command, 0, marked).WithMark();

	public static RunbookStep WaitFor(int seconds) => new(RunbookStepKind.Wait, string.Empty, Math.Max(1, seconds));

	public static RunbookStep WaitUntilBack() => new(RunbookStepKind.WaitUntilBack);

	[JsonIgnore]
	public bool IsCommand => this.Kind == RunbookStepKind.Command;

	/// <summary>True for a command that restarts, stops or changes the Neuron: the console's own rule.</summary>
	[JsonIgnore]
	public bool ChangesTheNeuron => this.IsCommand && ConsoleSession.ChangesTheNeuron(this.Command);

	/// <summary>
	/// The step marked when it changes the Neuron. Such a step is always marked; any other
	/// command can be marked by hand. Waits send nothing and are never marked.
	/// </summary>
	public RunbookStep WithMark() => this.IsCommand ? (this.ChangesTheNeuron && !this.Marked ? this with { Marked = true } : this) : this with { Marked = false };

	/// <summary>The blanks in the command, in order, without the one each Neuron fills itself.</summary>
	[JsonIgnore]
	public IEnumerable<string> Blanks
		=> this.IsCommand
			? BlankPattern().Matches(this.Command).Select(m => m.Groups[1].Value).Where(b => !string.Equals(b, NeuronBlank, StringComparison.OrdinalIgnoreCase))
			: [];

	/// <summary>The step as a person reads it: the command, or what the wait waits for.</summary>
	public string Describe() => this.Kind switch
	{
		RunbookStepKind.Wait => "Wait " + Duration(this.Seconds),
		RunbookStepKind.WaitUntilBack => "Wait until the Neuron is back",
		_ => this.Command,
	};

	/// <summary>The command as it goes to one Neuron: blanks filled from <paramref name="values"/>, {neuron} with its domain.</summary>
	public string Fill(IReadOnlyDictionary<string, string> values, string neuronJid)
	{
		if (!this.IsCommand)
			return this.Describe();

		return BlankPattern().Replace(this.Command, m =>
		{
			string name = m.Groups[1].Value;
			if (string.Equals(name, NeuronBlank, StringComparison.OrdinalIgnoreCase))
				return Events.NeuronNames.Domain(neuronJid);

			foreach (KeyValuePair<string, string> v in values)
			{
				if (string.Equals(v.Key, name, StringComparison.OrdinalIgnoreCase))
					return v.Value;
			}

			return m.Value;
		});
	}

	/// <summary>"30 seconds", "2 minutes", "1 minute 30 seconds".</summary>
	public static string Duration(int seconds)
	{
		int m = seconds / 60, s = seconds % 60;
		string Part(int n, string one) => n == 1 ? "1 " + one : n + " " + one + "s";
		return m == 0 ? Part(s, "second") : s == 0 ? Part(m, "minute") : Part(m, "minute") + " " + Part(s, "second");
	}

	/// <summary>A blank: a name in braces, starting with a letter. <c>{x: 1}</c> in script is not one.</summary>
	[GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_\-]{0,39})\}")]
	internal static partial Regex BlankPattern();
}

/// <summary>A named series of console commands, saved once and run again on one Neuron or many.</summary>
public sealed record Runbook
{
	public Guid Id { get; init; } = Guid.NewGuid();

	public string Title { get; init; } = string.Empty;

	public IReadOnlyList<RunbookStep> Steps { get; init; } = [];

	/// <summary>What was filled in for each blank the last time, by blank name.</summary>
	public IReadOnlyDictionary<string, string> Remembered { get; init; } = new Dictionary<string, string>();

	/// <summary>Shipped with NAT. Not kept in the database, and not changed in place.</summary>
	public bool ReadyMade { get; init; }

	public DateTime Changed { get; init; } = DateTime.UtcNow;

	/// <summary>Every blank in the runbook, once each, in the order they first appear.</summary>
	[JsonIgnore]
	public IReadOnlyList<string> Blanks
		=> [.. this.Steps.SelectMany(s => s.Blanks).Distinct(StringComparer.OrdinalIgnoreCase)];

	/// <summary>The runbook with every step that changes the Neuron marked.</summary>
	public Runbook WithMarks() => this with { Steps = [.. this.Steps.Select(s => s.WithMark())] };

	/// <summary>How many steps ask first.</summary>
	[JsonIgnore]
	public int MarkedCount => this.Steps.Count(s => s.Marked);
}
