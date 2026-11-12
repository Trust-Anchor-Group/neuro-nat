using System.Collections.Concurrent;
using System.Security.Cryptography;
using Neuro.Nat.Engine.Runbooks;
using Neuro.Nat.Engine.Storage;
using Waher.Persistence.Files;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// Runbooks on stand-in Neurons: a dry run sends nothing, a failure stops only that Neuron, a
/// marked step asks every time, a saved file reads back the same, and what a run leaves is kept.
/// </summary>
public class RunbookTests : IDisposable
{
	private static readonly Guid Account = Guid.NewGuid();
	private readonly string folder = Path.Combine(Path.GetTempPath(), "Nat.Runbooks." + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(this.folder))
				Directory.Delete(this.folder, true);
		}
		catch (IOException)
		{
		}
	}

	private static RunbookTarget Neuron(string name) => new(Account, name + "@" + name + ".lab.local", name + ".lab.local", "ops");

	private static readonly RunbookTarget Alpha = Neuron("alpha"), Beta = Neuron("beta"), Gamma = Neuron("gamma");

	/// <summary>Stand-in Neurons: they note what they are sent and answer at once.</summary>
	internal sealed class StandIns : IRunbookSender
	{
		private readonly ConcurrentDictionary<string, int> downFor = new(StringComparer.OrdinalIgnoreCase);

		public ConcurrentQueue<(string Jid, string Command)> Sent { get; } = new();

		/// <summary>Neurons that are not online.</summary>
		public HashSet<string> Offline { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Answers by Neuron; others answer with the command in back quotes.</summary>
		public Dictionary<string, Func<string, string>> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>After "Reboot", a Neuron is away for this many looks.</summary>
		public int RebootLooks { get; init; } = 3;

		public bool IsOnline(RunbookTarget target)
		{
			if (this.Offline.Contains(target.Jid))
				return false;

			if (this.downFor.TryGetValue(target.Jid, out int left) && left > 0)
			{
				this.downFor[target.Jid] = left - 1;
				return false;
			}

			return true;
		}

		public Task<RunbookAnswer> SendAsync(RunbookTarget target, string command, CancellationToken cancel)
		{
			this.Sent.Enqueue((target.Jid, command));
			if (command.Equals("Reboot", StringComparison.OrdinalIgnoreCase))
				this.downFor[target.Jid] = this.RebootLooks;

			string answer = this.Answers.TryGetValue(target.Jid, out Func<string, string>? a) ? a(command) : "`" + command + "`";
			return Task.FromResult(new RunbookAnswer(answer));
		}
	}

	private static RunbookRunner Runner(IRunbookSender sender, Func<RunbookAsk, Task<bool>>? ask = null)
		=> new(sender)
		{
			Ask = (a, _) => ask?.Invoke(a) ?? Task.FromResult(true),
			Delay = (_, _) => Task.CompletedTask,
		};

	private static readonly Dictionary<string, string> NoValues = [];

	// ----- Dry run -----

	[Fact]
	public void ADryRunShowsWhatGoesWhereAndSendsNothing()
	{
		StandIns neurons = new();
		Runbook r = new()
		{
			Title = "Reset one",
			Steps = RunbookFile.ReadSteps("Versions\n! reset password {account}\nwait 30 seconds\n> echo {neuron}"),
		};

		IReadOnlyList<IReadOnlyList<string>> plan = RunbookRunner.Plan(r, [Alpha, Beta], new Dictionary<string, string> { ["account"] = "kim" });

		Assert.Equal(["Versions", "Versions"], plan[0]);
		Assert.Equal(["reset password kim", "reset password kim"], plan[1]);
		Assert.Equal(["Wait 30 seconds", "Wait 30 seconds"], plan[2]);
		Assert.Equal(["echo alpha.lab.local", "echo beta.lab.local"], plan[3]);
		Assert.Empty(neurons.Sent);
	}

	// ----- Failures -----

	[Fact]
	public async Task AFailureStopsTheRunbookForThatNeuronOnly()
	{
		StandIns neurons = new();
		neurons.Offline.Add(Gamma.Jid);
		// Wording as the Neuron's chat admin writes an error.
		neurons.Answers[Beta.Jid] = c => c == "Backups"
			? "An error occurred when processing request:\n\n<font class=\"error\">\n\nAccess to the path 'C:\\\\ProgramData\\\\Backup' is denied\\.\n\n```\n   at Waher.Backup()\n```\n\n</font>"
			: "`" + c + "`";

		Runbook r = new() { Title = "Check", Steps = RunbookFile.ReadSteps("Versions\nBackups\nNrClients") };
		RunbookRecord record = await Runner(neurons).RunAsync(r, [Alpha, Beta, Gamma], NoValues, TestContext.Current.CancellationToken);

		Assert.Equal([(Alpha.Jid, "Versions"), (Alpha.Jid, "Backups"), (Alpha.Jid, "NrClients")], neurons.Sent.Where(s => s.Jid == Alpha.Jid));
		Assert.Equal(["Versions", "Backups"], neurons.Sent.Where(s => s.Jid == Beta.Jid).Select(s => s.Command));
		Assert.DoesNotContain(neurons.Sent, s => s.Jid == Gamma.Jid);

		RunbookNeuronRecord a = record.Neurons[0], b = record.Neurons[1], g = record.Neurons[2];
		Assert.True(a.Done);
		Assert.Equal("Done", a.Outcome);
		Assert.Equal(3, a.Steps.Count);
		Assert.Equal(2, b.StoppedAt);
		Assert.Equal(@"Access to the path 'C:\ProgramData\Backup' is denied.", b.Reason);
		Assert.StartsWith("Stopped at step 2: ", b.Outcome);
		Assert.Equal(1, g.StoppedAt);
		Assert.Equal("Not online", g.Reason);
	}

	// ----- Marked steps -----

	[Fact]
	public async Task AMarkedStepAsksEveryTimeAlsoInTheMiddleOfARun()
	{
		StandIns neurons = new();
		Runbook r = new()
		{
			Title = "Restart and check",
			Steps = [RunbookStep.Send("Versions"), RunbookStep.Send("Reboot"), RunbookStep.WaitUntilBack(), RunbookStep.Send("NrClients", marked: true)],
		};
		Assert.True(r.Steps[1].Marked, "Reboot is marked when saved.");
		Assert.False(r.Steps[0].Marked);

		List<RunbookAsk> asked = [];
		RunbookRunner runner = Runner(neurons, a =>
		{
			asked.Add(a);
			return Task.FromResult(true);
		});

		RunbookRecord first = await runner.RunAsync(r, [Alpha, Beta], NoValues, TestContext.Current.CancellationToken);
		RunbookRecord second = await runner.RunAsync(r, [Alpha, Beta], NoValues, TestContext.Current.CancellationToken);

		// Twice per run, at steps 2 and 4, naming both Neurons and what they get.
		Assert.Equal([2, 4, 2, 4], asked.Select(a => a.Number));
		Assert.All(asked, a => Assert.Equal([Alpha.Name, Beta.Name], a.Neurons.Select(n => n.Name)));
		Assert.Equal(["Reboot", "Reboot"], asked[0].Commands);
		Assert.All([first, second], rec => Assert.All(rec.Neurons, n => Assert.True(n.Done, n.Outcome)));
		Assert.Equal("Online", first.Neurons[0].Steps[2].Answer);

		// Asked about step 2 only after step 1 had gone out.
		List<RunbookAsk> early = [];
		StandIns fresh = new();
		await Runner(fresh, a =>
		{
			early.Add(a);
			if (a.Number == 2)
				Assert.Equal(["Versions", "Versions"], fresh.Sent.Select(s => s.Command));
			return Task.FromResult(a.Number == 2);
		}).RunAsync(r, [Alpha, Beta], NoValues, TestContext.Current.CancellationToken);

		// A no in the middle stops there: step 4 was asked about and nothing went after it.
		Assert.Equal([2, 4], early.Select(a => a.Number));
		Assert.DoesNotContain(fresh.Sent, s => s.Command == "NrClients");
	}

	[Fact]
	public async Task ANoStopsTheRunAndSaysSo()
	{
		StandIns neurons = new();
		Runbook r = new() { Title = "Stop it", Steps = [RunbookStep.Send("Versions"), RunbookStep.Send("Stop"), RunbookStep.Send("Time")] };

		RunbookRecord record = await Runner(neurons, _ => Task.FromResult(false)).RunAsync(r, [Alpha], NoValues, TestContext.Current.CancellationToken);

		Assert.Equal(["Versions"], neurons.Sent.Select(s => s.Command));
		Assert.Equal("Stopped at step 2: Not confirmed", record.Neurons[0].Outcome);
	}

	[Fact]
	public async Task ANeuronThatDoesNotComeBackStopsThere()
	{
		StandIns neurons = new() { RebootLooks = 1000 };
		Runbook r = new() { Title = "Restart", Steps = [RunbookStep.Send("Reboot"), RunbookStep.WaitUntilBack(), RunbookStep.Send("Time")] };

		RunbookRecord record = await Runner(neurons).RunAsync(r, [Alpha], NoValues, TestContext.Current.CancellationToken);

		Assert.Equal("Stopped at step 2: Not back after 10 minutes", record.Neurons[0].Outcome);
		Assert.DoesNotContain(neurons.Sent, s => s.Command == "Time");
	}

	// ----- The file -----

	[Fact]
	public void ASavedFileOpensWithTheSameStepsBlanksAndMarks()
	{
		Runbook r = new Runbook
		{
			Title = "Restart and check",
			Steps =
			[
				RunbookStep.Send("Versions"),
				RunbookStep.Send("Reboot"),
				RunbookStep.WaitUntilBack(),
				RunbookStep.WaitFor(90),
				RunbookStep.WaitFor(120),
				RunbookStep.Send("reset password {account}"),
				RunbookStep.Send("NrClients", marked: true),
				RunbookStep.Send("x:={a: 1};\nx.a + {count}"),
			],
			Remembered = new Dictionary<string, string> { ["account"] = "kim" },
		}.WithMarks();

		string text = RunbookFile.Write(r);
		Runbook back = RunbookFile.Read(text);

		Assert.Equal(r.Title, back.Title);
		Assert.Equal(r.Steps, back.Steps);
		Assert.Equal(["account", "count"], back.Blanks);
		Assert.Equal([false, true, false, false, false, true, true, false], back.Steps.Select(s => s.Marked));

		// It reads as what it is, and keeps what the blanks remember to itself.
		Assert.StartsWith("NAT runbook: Restart and check", text);
		Assert.Contains("\n! Reboot", text.Replace("\r\n", "\n"));
		Assert.Contains("\nwait until back", text.Replace("\r\n", "\n"));
		Assert.Contains("\nwait 90 seconds", text.Replace("\r\n", "\n"));
		Assert.Contains("\nwait 2 minutes", text.Replace("\r\n", "\n"));
		Assert.Contains("\n  x.a + {count}", text.Replace("\r\n", "\n"));
		Assert.DoesNotContain("kim", text);
	}

	[Fact]
	public void SomeoneElsesFileCannotLeaveARestartUnmarked()
	{
		Runbook r = RunbookFile.Read("\r\nNAT runbook: Quick\r\n# no marks here\r\n> Reboot\r\n> Versions\r\n");
		Assert.Equal([true, false], r.Steps.Select(s => s.Marked));
		Assert.Throws<FormatException>(() => RunbookFile.Read("Versions\nReboot"));
		Assert.Throws<FormatException>(() => RunbookFile.Read("NAT runbook: Empty\n# nothing"));
	}

	[Fact]
	public void TheReadyMadeRunbooksOnlyRead()
	{
		Assert.InRange(ReadyMadeRunbooks.All.Count, 2, 3);
		Assert.All(ReadyMadeRunbooks.All.SelectMany(r => r.Steps), s =>
		{
			Assert.True(s.IsCommand);
			Assert.False(s.Marked);
			Assert.False(s.ChangesTheNeuron);
		});
		Assert.Equal(ReadyMadeRunbooks.All.Count, ReadyMadeRunbooks.All.Select(r => r.Id).Distinct().Count());
	}

	// ----- Kept in the database -----

	private async Task<FilesProvider> OpenAsync(byte[] key)
	{
		EngineRuntime.Initialize();
		DatabaseOpening o = await LocalDatabase.OpenAsync(Path.Combine(this.folder, "database"), key);
		Assert.True(o.IsOpen, o.Problem + " " + o.Error);
		return o.Database!;
	}

	[Fact]
	public async Task BlanksAreRememberedAndTheRecordIsKept()
	{
		byte[] key = RandomNumberGenerator.GetBytes(32);
		FilesProvider db = await this.OpenAsync(key);
		DatabaseRunbookStore store = new(db);

		Runbook r = new Runbook { Title = "Reset one", Steps = RunbookFile.ReadSteps("reset password {account}\nVersions") }
			with { Remembered = new Dictionary<string, string> { ["account"] = "kim" } };
		await store.SaveAsync(r);

		StandIns neurons = new();
		RunbookRecord record = await Runner(neurons).RunAsync(r, [Alpha, Beta], r.Remembered, TestContext.Current.CancellationToken);
		await store.AddRecordAsync(record);
		await db.Stop();

		db = await this.OpenAsync(key);
		store = new DatabaseRunbookStore(db);
		Runbook back = Assert.Single(await store.LoadAsync());
		Assert.Equal("kim", back.Remembered["account"]);
		Assert.Equal(r.Steps, back.Steps);

		RunbookRecord kept = Assert.Single(await store.RecordsAsync(r.Id));
		Assert.Equal(["ops"], kept.Accounts);
		Assert.Equal([Alpha.Name, Beta.Name], kept.Neurons.Select(n => n.Name));
		Assert.Equal(["reset password kim", "Versions"], kept.Neurons[1].Steps.Select(s => s.Command));
		Assert.Equal("`Versions`", kept.Neurons[1].Steps[1].Answer);
		Assert.Equal(record.Started, kept.Started);

		// Deleting the runbook keeps the records of its runs.
		await store.DeleteAsync(r.Id);
		Assert.Empty(await store.LoadAsync());
		Assert.Single(await store.RecordsAsync(r.Id));
		await db.Stop();
	}
}
