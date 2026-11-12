using System.Text;
using System.Text.RegularExpressions;

namespace Neuro.Nat.Engine.Runbooks;

/// <summary>
/// A runbook as a plain text file that reads as what it is. docs/RUNBOOKS.md says the same.
/// <code>
/// NAT runbook: Restart and check
/// # Notes start with #. One step per line.
/// > Versions
/// ! Reboot
/// wait until back
/// wait 30 seconds
/// > reset password {account}
/// </code>
/// <list type="bullet">
/// <item>The first line that is not empty is the title line: "NAT runbook:" and the title.</item>
/// <item><c>&gt; command</c> sends the command as it is.</item>
/// <item><c>! command</c> is a marked step: it asks first, and names the Neurons. Commands that
/// restart, stop or change the Neuron are always marked, whatever the file says.</item>
/// <item><c>wait 30 seconds</c> (or minutes) waits on each Neuron; <c>wait until back</c> waits
/// until each Neuron has gone and come back online.</item>
/// <item><c>{name}</c> in a command is a blank, asked for when the runbook runs. <c>{neuron}</c>
/// is filled with each Neuron's own domain.</item>
/// <item>A line that starts with two spaces goes on with the command above (for script over
/// several lines).</item>
/// <item>A line that starts with <c>#</c> is a note and is not a step.</item>
/// </list>
/// When typing a runbook in NAT, a line without <c>&gt;</c> or <c>!</c> is a command too.
/// </summary>
public static partial class RunbookFile
{
	public const string TitlePrefix = "NAT runbook:";

	/// <summary>The extension NAT saves runbooks with; the file is plain text.</summary>
	public const string Extension = "txt";

	/// <summary>The runbook as the text of a file.</summary>
	public static string Write(Runbook runbook)
	{
		StringBuilder sb = new();
		sb.Append(TitlePrefix).Append(' ').AppendLine(runbook.Title.Trim());
		sb.AppendLine("# One step per line. \"> \" sends a command, \"! \" asks first and names the Neurons.");
		sb.AppendLine("# \"wait 30 seconds\" and \"wait until back\" wait on each Neuron. {name} is a blank, asked for at run time.");
		sb.Append(WriteSteps(runbook.Steps));
		return sb.ToString();
	}

	/// <summary>The steps alone, one per line, as the file and the editor show them.</summary>
	public static string WriteSteps(IEnumerable<RunbookStep> steps)
	{
		StringBuilder sb = new();
		foreach (RunbookStep s in steps)
		{
			switch (s.Kind)
			{
				case RunbookStepKind.Wait:
					// Whole minutes as minutes, anything else in seconds, so it always reads back.
					sb.Append("wait ").AppendLine(s.Seconds % 60 == 0 ? RunbookStep.Duration(s.Seconds) : s.Seconds + (s.Seconds == 1 ? " second" : " seconds"));
					break;

				case RunbookStepKind.WaitUntilBack:
					sb.AppendLine("wait until back");
					break;

				default:
					string[] lines = s.Command.Replace("\r\n", "\n").Split('\n');
					sb.Append(s.Marked ? "! " : "> ").AppendLine(lines[0]);
					foreach (string more in lines.Skip(1))
						sb.Append("  ").AppendLine(more);
					break;
			}
		}

		return sb.ToString();
	}

	/// <summary>Reads a runbook file. Throws <see cref="FormatException"/> with a short reason when it is not one.</summary>
	public static Runbook Read(string text)
	{
		string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		int i = 0;
		while (i < lines.Length && lines[i].Trim().Length == 0)
			i++;

		string first = i < lines.Length ? lines[i].Trim().TrimStart('﻿') : string.Empty;
		if (!first.StartsWith(TitlePrefix, StringComparison.OrdinalIgnoreCase))
			throw new FormatException("This is not a NAT runbook.");

		string title = first[TitlePrefix.Length..].Trim();
		IReadOnlyList<RunbookStep> steps = ReadSteps(string.Join('\n', lines.Skip(i + 1)));
		if (steps.Count == 0)
			throw new FormatException("The runbook has no steps.");

		return new Runbook { Title = title.Length > 0 ? title : "Runbook", Steps = steps };
	}

	/// <summary>Reads steps, one per line. A line without a mark is a command. Steps that change the Neuron are marked.</summary>
	public static IReadOnlyList<RunbookStep> ReadSteps(string text)
	{
		List<RunbookStep> steps = [];
		foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
		{
			// Two spaces (or a tab) go on with the command above.
			if ((raw.StartsWith("  ", StringComparison.Ordinal) || raw.StartsWith('\t')) && steps.Count > 0 && steps[^1].IsCommand && raw.Trim().Length > 0)
			{
				string more = raw.StartsWith('\t') ? raw[1..] : raw[2..];
				steps[^1] = steps[^1] with { Command = steps[^1].Command + "\n" + more };
				continue;
			}

			string line = raw.Trim();
			if (line.Length == 0 || line.StartsWith('#'))
				continue;

			if (line.StartsWith('>'))
				steps.Add(new RunbookStep(RunbookStepKind.Command, line[1..].Trim()));
			else if (line.StartsWith('!'))
				steps.Add(new RunbookStep(RunbookStepKind.Command, line[1..].Trim(), Marked: true));
			else if (WaitUntilBack().IsMatch(line))
				steps.Add(RunbookStep.WaitUntilBack());
			else if (WaitFor().Match(line) is { Success: true } m)
			{
				int n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
				steps.Add(RunbookStep.WaitFor(m.Groups[2].Value.StartsWith('m') ? n * 60 : n));
			}
			else
				steps.Add(new RunbookStep(RunbookStepKind.Command, line));
		}

		return [.. steps.Where(s => !s.IsCommand || s.Command.Trim().Length > 0).Select(s => s.WithMark())];
	}

	[GeneratedRegex(@"^wait\s+until\s+(the\s+neuron\s+is\s+)?back\.?$", RegexOptions.IgnoreCase)]
	private static partial Regex WaitUntilBack();

	[GeneratedRegex(@"^wait\s+(\d{1,5})\s*(s|sec|secs|second|seconds|m|min|mins|minute|minutes)\.?$", RegexOptions.IgnoreCase)]
	private static partial Regex WaitFor();
}
