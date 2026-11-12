using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Concentrator;
using Neuro.Nat.Engine.Events;
using Waher.Networking.XMPP.Concentrator;
using Waher.Networking.XMPP.DataForms;
using Waher.Things;

namespace Neuro.Nat.Engine.Tests;

/// <summary>
/// Browses the local test Neuron's sources and nodes the way the Sources workspace does:
/// sources, nodes, commands, an edit form (not saved) and a report.
/// </summary>
[Collection("Live Neuron")]
public class LiveConcentratorTests(ITestOutputHelper output)
{
	private static async Task<(AccountManager, NeuronMonitor, NeuronConcentrator)?> ConnectAsync(ITestOutputHelper output)
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? neuron = Environment.GetEnvironmentVariable("NAT_TEST_NEURON");

		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(neuron))
			Live.Skip("NAT_TEST_HOST", "NAT_TEST_USER", "NAT_TEST_PASSWORD", "NAT_TEST_NEURON");

		EngineRuntime.Initialize();
		string file = Path.Combine(Path.GetTempPath(), "Nat.Live." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(file), new InMemoryCredentialVault());
		AccountSession s = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true }, AccountSecret.FromPassword(password), connect: false);
		NeuronMonitor monitor = new(s, new EventHub());
		await s.ConnectAsync();
		await StreamSnifferTests.WaitFor(() => monitor.Concentrator(neuron) is not null, 30000);
		return (manager, monitor, monitor.Concentrator(neuron)!);
	}

	[Fact]
	public async Task BrowsesSourcesNodesAndCommands()
	{
		if (await ConnectAsync(output) is not var (manager, monitor, c))
			return;

		await using (manager)
		await using (monitor)
		{
			IReadOnlyList<DataSourceReference> roots = await c.GetRootSourcesAsync();
			Assert.NotEmpty(roots);

			NodeInformation? withCommands = null;
			foreach (DataSourceReference src in roots)
			{
				output.WriteLine($"Source {src.SourceID} '{src.Name}' children:{src.HasChildren}");
				foreach (DataSourceReference child in src.HasChildren ? await c.GetChildSourcesAsync(src.SourceID) : [])
					output.WriteLine($"  Source {child.SourceID} '{child.Name}'");

				IReadOnlyList<NodeInformation> nodes = await c.GetRootNodesAsync(src.SourceID);
				foreach (NodeInformation n in nodes)
				{
					output.WriteLine($"  Node {n.LocalId} ({n.NodeType}) '{n.DisplayName}' state:{n.NodeState} children:{n.HasChildren} cmds:{n.HasCommands} read:{n.IsReadable} params:{n.ParameterList?.Length}");
					if (n.HasCommands && withCommands is null)
						withCommands = n;
					if (n.HasChildren)
					{
						foreach (NodeInformation ch in (await c.GetChildNodesAsync(n)).Take(8))
						{
							output.WriteLine($"    Node {ch.LocalId} ({ch.NodeType}) cmds:{ch.HasCommands} children:{ch.HasChildren}");
							if (ch.HasCommands && withCommands is null)
								withCommands = ch;
						}
					}
				}
			}

			foreach (ThingReference r in new[] { new ThingReference("Root", "Groups", ""), new ThingReference("Root", "MeteringTopology", ""),
				new ThingReference("Search Event Log", "Reports", ""), new ThingReference("CPU", "Reports", ""), new ThingReference("Local Web Server", "MeteringTopology", "") })
			{
				try
				{
					NodeInformation n = await c.GetNodeAsync(r);
					output.WriteLine($"Node {r.SourceId}/{r.NodeId} params: " + string.Join("; ", (n.ParameterList ?? []).Select(p => $"{p.Name}={p.StringValue}")));
					IReadOnlyList<NodeCommand> cmds = await c.GetCommandsAsync(n);
					output.WriteLine($"Commands on {r.SourceId}/{r.NodeId}: " + string.Join(", ", cmds.Select(x => $"{x.Name} [{x.Type}] ({x.Command}) cat:{x.SortCategory}")));
				}
				catch (ConcentratorException ex)
				{
					output.WriteLine($"Commands on {r.SourceId}/{r.NodeId}: refused: {ex.Message}");
				}
			}

			NodeInformation metering = await c.GetNodeAsync(new ThingReference("Root", "MeteringTopology", ""));
			NodeInformation web = await c.GetNodeAsync(new ThingReference("Local Web Server", "MeteringTopology", ""));
			FormSession edit = await c.EditAsync(web);
			output.WriteLine($"Edit form '{edit.Form.Title}' pages:{edit.Form.Pages?.Length}: " + string.Join(", ", edit.Form.Fields.Select(f => $"{f.Var}:{f.TypeName}{(f.ReadOnly ? "(ro)" : "")}={f.ValueString}")));
			await edit.Form.Cancel();

			IReadOnlyList<LocalizedString> addable = await c.GetAddableTypesAsync(metering);
			output.WriteLine("Addable under metering root: " + string.Join(", ", addable.Select(a => $"{a.Localized} ({a.Unlocalized})")));
		}
	}

	/// <summary>Removes test nodes through chat admin script, which runs for notification addresses.</summary>
	private async Task CleanUpAsync(NeuronConcentrator c)
	{
		TaskCompletionSource<string> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task OnChat(object? sender, Waher.Networking.XMPP.Events.MessageEventArgs e)
		{
			if (string.Equals(e.FromBareJID, c.NeuronJid, StringComparison.OrdinalIgnoreCase) && e.To.Contains('/'))
				reply.TrySetResult(e.Body);
			return Task.CompletedTask;
		}

		c.Client.OnChatMessage += OnChat;
		try
		{
			await c.Client.SendChatMessage(c.FullJid,
				"V:=select * from Waher.Things.Virtual.VirtualNode where NodeId >= \"NAT test\" and NodeId < \"NAT tesu\"; " +
				"foreach N in V do (Waher.Things.Metering.MeteringTopology.Root.RemoveAsync(N); N.DestroyAsync()); " +
				"Nodes:=select * from Waher.Things.Metering.NodeTypes.NodeCollection where NodeId >= \"NAT test\" and NodeId < \"NAT tesu\"; " +
				"foreach N in Nodes do (Waher.Things.Metering.MeteringTopology.Root.RemoveAsync(N); N.DestroyAsync()); " +
				"\"Removed \" + Str(Nodes.Length + V.Length)");
			output.WriteLine("Clean-up: " + await reply.Task.WaitAsync(TimeSpan.FromSeconds(15)));
		}
		finally
		{
			c.Client.OnChatMessage -= OnChat;
		}
	}

	[Fact]
	public async Task ReadsControlsCopiesPastesAndSearches()
	{
		if (await ConnectAsync(output) is not var (manager, monitor, c))
			return;

		await using (manager)
		await using (monitor)
		{
			await CleanUpAsync(c);
			try
			{
				NodeInformation root = await c.GetNodeAsync(new ThingReference("Root", "MeteringTopology", ""));
				string id = "NAT test " + Guid.NewGuid().ToString("N")[..6];

				FormSession add = await c.AddAsync(root, "Waher.Things.Metering.NodeTypes.NodeCollection");
				await add.Form["NodeId"].SetValue(id);
				FormOutcome folder = await add.SubmitAsync();
				Assert.True(folder.Ok, folder.Error);

				FormSession addV = await c.AddAsync(folder.Node!, "Waher.Things.Virtual.VirtualNode");
				await addV.Form["NodeId"].SetValue(id + " v");
				FormOutcome virt = await addV.SubmitAsync();
				Assert.True(virt.Ok, virt.Error);
				output.WriteLine($"Virtual node readable:{virt.Node!.IsReadable} controllable:{virt.Node.IsControllable}");

				// Sensor data
				Readout readout = await c.ReadAsync(virt.Node, Waher.Things.SensorData.FieldType.All);
				await StreamSnifferTests.WaitFor(() => readout.IsFinished, 20000);
				output.WriteLine($"Readout {readout.State}: " + string.Join(", ", readout.Fields.Select(f => $"{f.Name}={f.ValueString}")) +
					" errors: " + string.Join(", ", readout.Errors.Select(e => e.ErrorMessage)));
				// A virtual node without configured fields is not readable; the Neuron says so through the readout.
				Assert.True(readout.State == Waher.Networking.XMPP.Sensor.SensorDataReadoutState.Done || readout.Errors.Count > 0);

				// Control
				try
				{
					DataForm control = await c.GetControlFormAsync(virt.Node);
					output.WriteLine("Control form: " + string.Join(", ", control.Fields.Select(f => $"{f.Var}:{f.TypeName}")));
				}
				catch (ConcentratorException ex)
				{
					output.WriteLine("Control: " + ex.Message);
				}

				// Copy the folder with its virtual node, paste it next to itself.
				NodeCopy copy = await NodeCopy.CopyAsync(c, folder.Node!);
				output.WriteLine($"Copied {copy.Count} nodes: {copy.Label} with " + string.Join(", ", copy.Values.Keys));
				Assert.Equal(2, copy.Count);

				NodeCopy.PasteResult pasted = await copy.PasteAsync(c, root);
				output.WriteLine($"Pasted {pasted.Created}: top {pasted.Top?.NodeId}; problems: " + string.Join("; ", pasted.Problems));
				Assert.Equal(id + " (copy)", pasted.Top?.NodeId);
				Assert.True(pasted.Created >= 1);

				// Search
				IReadOnlyList<NodeInformation> all = await c.GetAllNodesAsync("MeteringTopology");
				List<NodeInformation> found = [.. all.Where(n => n.NodeId.Contains(id, StringComparison.OrdinalIgnoreCase))];
				output.WriteLine($"Search: {all.Count} nodes in source, found " + string.Join(", ", found.Select(n => n.NodeId)));
				Assert.True(found.Count >= 3);
			}
			finally
			{
				await CleanUpAsync(c);
			}
		}
	}

	/// <summary>Runs NAT_PROBE_SCRIPT through chat admin and prints the reply. Exploration only.</summary>
	[Fact]
	public async Task ProbeScript()
	{
		string? script = Environment.GetEnvironmentVariable("NAT_PROBE_SCRIPT");
		if (string.IsNullOrEmpty(script))
			Live.Skip("NAT_PROBE_SCRIPT");

		if (await ConnectAsync(output) is not var (manager, monitor, c))
			return;

		await using (manager)
		await using (monitor)
			output.WriteLine("Reply: " + (await this.ScriptAsync(c, script)).Replace("\r", "").Replace("\n", " | "));
	}

	private async Task<string> ScriptAsync(NeuronConcentrator c, string script)
	{
		TaskCompletionSource<string> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task OnChat(object? sender, Waher.Networking.XMPP.Events.MessageEventArgs e)
		{
			if (string.Equals(e.FromBareJID, c.NeuronJid, StringComparison.OrdinalIgnoreCase) && e.To.Contains('/'))
				reply.TrySetResult(e.Body);
			return Task.CompletedTask;
		}

		c.Client.OnChatMessage += OnChat;
		try
		{
			await c.Client.SendChatMessage(c.FullJid, script);
			return await reply.Task.WaitAsync(TimeSpan.FromSeconds(15));
		}
		finally
		{
			c.Client.OnChatMessage -= OnChat;
		}
	}

	[Fact]
	public async Task AddsEditsAndDeletesANode()
	{
		if (await ConnectAsync(output) is not var (manager, monitor, c))
			return;

		await using (manager)
		await using (monitor)
		{
			await CleanUpAsync(c);

			NodeInformation root = await c.GetNodeAsync(new ThingReference("Root", "MeteringTopology", ""));
			string id = "NAT test " + Guid.NewGuid().ToString("N")[..6];

			// A clash with an existing node id comes back as a remark on the field.
			FormSession add = await c.AddAsync(root, "Waher.Things.Metering.NodeTypes.NodeCollection");
			output.WriteLine("New node form: " + string.Join(", ", add.Form.Fields.Select(f => $"{f.Var}:{f.TypeName}{(f.Required ? "*" : "")}")));
			await add.Form["NodeId"].SetValue("Local Web Server");
			FormOutcome clash = await add.SubmitAsync();
			output.WriteLine($"Clash: ok {clash.Ok}, field errors {clash.HasFieldErrors}: " + string.Join("; ", clash.Form?.Fields.Where(f => f.HasError).Select(f => $"{f.Var}: {f.Error}") ?? []) + $" / {clash.Error}");
			Assert.False(clash.Ok);
			Assert.True(clash.HasFieldErrors);

			await add.Form["NodeId"].SetValue(id);
			FormOutcome created = await add.SubmitAsync();
			output.WriteLine($"Created: {created.Ok} {created.Node?.NodeId} {created.Error}");
			Assert.True(created.Ok);

			try
			{
				NodeInformation node = await c.GetNodeAsync(new ThingReference(id, "MeteringTopology", ""));
				FormSession edit = await c.EditAsync(node);
				await edit.Form["NodeId"].SetValue(id + " b");
				FormOutcome saved = await edit.SubmitAsync();
				output.WriteLine($"Renamed: {saved.Ok} {saved.Node?.NodeId} {saved.Error}");
				Assert.True(saved.Ok);
				id += " b";
			}
			finally
			{
				try
				{
					await c.DestroyAsync(new ThingReference(id, "MeteringTopology", ""));
				}
				catch (ConcentratorException ex) when (ex.Message.Contains("thing registry", StringComparison.OrdinalIgnoreCase))
				{
					// Metering nodes are unregistered from the thing registry when deleted, and the
					// local test Neuron has none. Neurons in production do. Clean up through script.
					output.WriteLine("Delete refused by this Neuron: " + ex.Message);
					await CleanUpAsync(c);
				}
			}

			IReadOnlyList<NodeInformation> after = await c.GetChildNodesAsync(root);
			output.WriteLine("Left under root: " + string.Join(", ", after.Select(n => n.NodeId)));
			Assert.DoesNotContain(after, n => n.NodeId.StartsWith("NAT test", StringComparison.Ordinal));
		}
	}

	[Fact]
	public async Task SniffsHttpTraffic()
	{
		if (await ConnectAsync(output) is not var (manager, monitor, c))
			return;

		await using (manager)
		await using (monitor)
		{
			NodeInformation protocols = await c.GetNodeAsync(new ThingReference("Protocols", "Reports", ""));
			foreach (NodeInformation n in await c.GetChildNodesAsync(protocols))
				output.WriteLine($"Protocol node {n.NodeId} sniffable:{n.Sniffable}");
			foreach (NodeInformation n in (await c.GetChildNodesAsync(await c.GetNodeAsync(new ThingReference("C2S", "Connections", "")))).Take(3))
				output.WriteLine($"C2S node {n.NodeId} sniffable:{n.Sniffable}");

			List<NodeInformation> sniffable = [];
			async Task Walk(IEnumerable<NodeInformation> nodes, int depth)
			{
				foreach (NodeInformation n in nodes)
				{
					if (n.Sniffable)
					{
						sniffable.Add(n);
						output.WriteLine($"Sniffable {n.SourceId}/{n.NodeId} ({n.NodeType})");
					}
					if (n.HasChildren && depth < 2 && !n.NodeType.Contains("ProgramData") && !n.NodeType.Contains("Connection"))
						await Walk(await c.GetChildNodesAsync(n), depth + 1);
				}
			}

			foreach (DataSourceReference src in await c.GetRootSourcesAsync())
			{
				try
				{
					await Walk(await c.GetRootNodesAsync(src.SourceID), 0);
				}
				catch (ConcentratorException ex)
				{
					output.WriteLine($"{src.SourceID}: {ex.Message}");
				}
			}

			NodeInformation http = sniffable.First(n => n.NodeType.Contains("Web", StringComparison.OrdinalIgnoreCase) || n.NodeType.Contains("Http", StringComparison.OrdinalIgnoreCase));

			await using NodeSniffer sniffer = new(() => c, http, "HTTP");
			await sniffer.StartAsync();
			output.WriteLine($"Sniffer: {sniffer.State} {sniffer.Error}");
			Assert.Equal(NodeSnifferState.Live, sniffer.State);

			string web = Environment.GetEnvironmentVariable("NAT_NEURON_WEB") ?? "http://localhost:8081";
			using HttpClient http2 = new();
			string marker = "nat-" + Guid.NewGuid().ToString("N")[..8];
			try { await http2.GetAsync(web + "/" + marker); } catch (HttpRequestException) { }

			await StreamSnifferTests.WaitFor(() => sniffer.Sniffer.Snapshot().Any(l => l.Text.Contains(marker)), 15000);
			foreach (var l in sniffer.Sniffer.Snapshot().Take(8))
				output.WriteLine($"  [{l.Direction}] {l.Text[..Math.Min(160, l.Text.Length)].Replace("\r", "").Replace("\n", " | ")}");
		}
	}

	[Fact]
	public async Task RunsAReport()
	{
		if (await ConnectAsync(output) is not var (manager, monitor, c))
			return;

		await using (manager)
		await using (monitor)
		{
			NodeInformation node = await c.GetNodeAsync(new ThingReference("Search Event Log", "Reports", string.Empty));
			NodeCommand cmd = (await c.GetCommandsAsync(node)).First(x => x.Type == CommandType.Query);
			DataForm form = await c.GetCommandFormAsync(node, cmd);
			if (form["MaxCount"] is Field max)
				await max.SetValue("25");

			using QueryRun run = await c.StartQueryAsync(node, cmd, form);
			await StreamSnifferTests.WaitFor(() => run.State != QueryRunState.Running, 30000);

			output.WriteLine($"{run.Title}: {run.State}, {run.RowCount} rows, {run.Messages.Count} messages, error {run.Error}");
			Assert.Equal(QueryRunState.Done, run.State);
			Assert.True(run.RowCount > 0);
		}
	}
}
