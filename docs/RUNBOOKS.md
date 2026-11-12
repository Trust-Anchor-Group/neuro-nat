# Runbooks

A runbook is a named series of console commands, saved once and run again on one Neuron or many.
Runbooks are listed in the Console, under the Neurons you send to. A runbook runs on the Neurons
ticked there.

## Making one

- **From the transcript** (the main way): "Save as runbook" over the transcript ticks every command
  in it. Untick what should not go in, give it a name, and "Save runbook". The right-click menu of a
  command has "Save as runbook…" too, which ticks only that one.
- **From nothing**: the + beside "Runbooks". A name, then one command per line.
- **From a file**: the folder beside "Runbooks" opens a runbook file. NAT shows its steps and offers
  to keep it. Nothing is sent until you press Run.

NAT comes with three runbooks that only read: Status check (`Domain`, `Started`, `Runtime`,
`Time`), Clients and servers (`NrClients`, `NrServers`, `Clients`, `Servers`) and Software and
backups (`BuildTime`, `Versions`, `Installed`, `Backups`). They cannot be changed in place; Edit
makes one of your own from them.

## Steps

- A **command** is sent as it is, as if typed in the console. Script works too.
- **`wait 30 seconds`** (or minutes) waits on each Neuron.
- **`wait until back`** waits until each Neuron has gone away and come back online, as after a
  restart. A Neuron that does not go away within a minute counts as back; one that does not come
  back within ten minutes stops there.
- A **blank** such as `{account}` is asked for when the runbook runs, and what you filled in is
  remembered for that runbook. `{neuron}` is filled with each Neuron's own domain and never asked
  for.
- A **marked** step asks first, every time, also in the middle of a run, and names the Neurons it
  goes to: type the Neuron's name (or, for several, how many) to go ahead. Commands that restart,
  stop or change the Neuron are marked when saved, by the same rule the console uses before such
  a command (`reboot`, `stop`, `repair`, `reset`, `remove`, `disable`, `block`, `install`,
  `update`, `cmd`), and cannot be unmarked. Any other command can be marked by hand ("Ask first"
  beside the step).

## Running

- **Dry run** shows exactly which command goes to which Neuron, with the blanks filled in. It sends
  nothing and asks nothing.
- **Run** goes step by step: each step goes to every Neuron still going, and the next step starts
  when all of them have answered. Each Neuron's answer shows under the step.
- A step **fails** on a Neuron when the Neuron is not online, does not answer within a minute, or
  answers with an error. The runbook then stops for that Neuron only; the others go on. A command
  that restarts the Neuron may go unanswered without failing.
- The end shows one line per Neuron: done, or stopped at which step and why.
- Each run leaves a **record**: the account, the time, the Neurons and the answer to each step.
  The latest show under "Earlier runs"; NAT keeps the last 100 runs of each runbook.

Runbooks, what their blanks remember, and the records are kept in NAT's encrypted database (see
[DATA.md](DATA.md)).

## The file

"Save" writes a runbook as a plain text file. It reads as what it is:

```text
NAT runbook: Restart and check
# One step per line. "> " sends a command, "! " asks first and names the Neurons.
# "wait 30 seconds" and "wait until back" wait on each Neuron. {name} is a blank, asked for at run time.
> Versions
! Reboot
wait until back
wait 30 seconds
> NrClients
! reset password {account}
```

- The first line that is not empty is the title line: `NAT runbook:` and the name.
- `> command` is a command.
- `! command` is a marked command: it asks first. A command that changes the Neuron is marked when
  the file is read, whatever the file says.
- `wait 30 seconds`, `wait 2 minutes` and `wait until back` are waits.
- `{name}` in a command is a blank. A blank is a name in braces that starts with a letter, so
  script such as `{x: 1}` is left alone.
- A line that starts with two spaces goes on with the command above, for script over several lines.
- A line that starts with `#` is a note.
- What the blanks remember is not written to the file.

When you type a runbook in NAT, a line without `>` or `!` is a command too, so a plain list of
commands is a runbook. The format is read and written by `src/Neuro.Nat.Engine/Runbooks/RunbookFile.cs`.
