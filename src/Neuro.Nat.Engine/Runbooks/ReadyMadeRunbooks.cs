namespace Neuro.Nat.Engine.Runbooks;

/// <summary>
/// Runbooks that come with NAT. Only commands that read: every one is in the help the Neuron's
/// chat admin lists (Domain, Started, Runtime, Time, NrClients, NrServers, Clients, Servers,
/// BuildTime, Versions, Installed, Backups). Their ids are fixed, so their records stay theirs.
/// </summary>
public static class ReadyMadeRunbooks
{
	public static IReadOnlyList<Runbook> All { get; } =
	[
		Make("5d0c3a10-6f0e-4d38-9a5e-2a1f00000001", "Status check", "Domain", "Started", "Runtime", "Time"),
		Make("5d0c3a10-6f0e-4d38-9a5e-2a1f00000002", "Clients and servers", "NrClients", "NrServers", "Clients", "Servers"),
		Make("5d0c3a10-6f0e-4d38-9a5e-2a1f00000003", "Software and backups", "BuildTime", "Versions", "Installed", "Backups"),
	];

	private static Runbook Make(string id, string title, params string[] commands) => new()
	{
		Id = Guid.Parse(id),
		Title = title,
		Steps = [.. commands.Select(c => RunbookStep.Send(c))],
		ReadyMade = true,
		Changed = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc),
	};
}
