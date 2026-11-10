using Waher.Events;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Concentrator;
using Waher.Networking.XMPP.Concentrator.Queries;
using Waher.Networking.XMPP.Control;
using Waher.Networking.XMPP.Sensor;
using Waher.Things.SensorData;
using Waher.Networking.XMPP.DataForms;
using Waher.Networking.Sniffers;
using Waher.Networking.XMPP.Events;
using Waher.Things;

namespace Neuro.AA.Engine.Concentrator;

/// <summary>Outcome of sending a form to a Neuron.</summary>
/// <param name="Ok">The Neuron accepted it.</param>
/// <param name="Node">The node as it is now, when the form edited or created one.</param>
/// <param name="Form">The form again, with the Neuron's remarks on each field, when it did not accept the values.</param>
/// <param name="Error">What went wrong otherwise.</param>
public sealed record FormOutcome(bool Ok, NodeInformation? Node = null, DataForm? Form = null, string? Error = null)
{
	public bool HasFieldErrors => this.Form is not null && !this.Ok;
}

/// <summary>
/// The concentrator (sources and nodes) of one Neuron, reached through one account. A thin,
/// awaitable layer over <see cref="ConcentratorClient"/>: every call goes to the Neuron's own
/// client, with the operator's rights; nothing is cached here.
/// </summary>
public sealed class NeuronConcentrator
{
	/// <summary>How long to wait for the Neuron before giving up on a request.</summary>
	public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

	private const string Language = "en";
	private readonly ConcentratorClient cc;
	private readonly SensorClient? sensor;
	private readonly ControlClient? control;

	public NeuronConcentrator(ConcentratorClient cc, Guid accountId, string neuronJid, string fullJid, SensorClient? sensor = null, ControlClient? control = null)
	{
		this.cc = cc;
		this.sensor = sensor;
		this.control = control;
		this.AccountId = accountId;
		this.NeuronJid = neuronJid;
		this.FullJid = fullJid;
	}

	public Guid AccountId { get; }
	public string NeuronJid { get; }
	public string FullJid { get; }

	public XmppClient Client => this.cc.Client;

	// ---- Browsing -------------------------------------------------------------------------

	public async Task<IReadOnlyList<DataSourceReference>> GetRootSourcesAsync()
		=> await this.cc.GetRootDataSourcesAsync(this.FullJid).WaitAsync(Timeout) ?? [];

	public async Task<IReadOnlyList<DataSourceReference>> GetChildSourcesAsync(string sourceId)
		=> await this.cc.GetChildDataSourcesAsync(this.FullJid, sourceId).WaitAsync(Timeout) ?? [];

	public async Task<IReadOnlyList<NodeInformation>> GetRootNodesAsync(string sourceId)
		=> await this.cc.GetRootNodesAsync(this.FullJid, sourceId, true, true, Language, string.Empty, string.Empty, string.Empty).WaitAsync(Timeout) ?? [];

	public async Task<IReadOnlyList<NodeInformation>> GetChildNodesAsync(IThingReference node)
		=> await this.cc.GetChildNodesAsync(this.FullJid, node, true, true, Language, string.Empty, string.Empty, string.Empty).WaitAsync(Timeout) ?? [];

	/// <summary>Every node in a data source, flat. Used for searching.</summary>
	public Task<IReadOnlyList<NodeInformation>> GetAllNodesAsync(string sourceId)
	{
		TaskCompletionSource<IReadOnlyList<NodeInformation>> tcs = NewTcs<IReadOnlyList<NodeInformation>>();
		_ = this.cc.GetAllNodes(this.FullJid, sourceId, false, false, Language, string.Empty, string.Empty, string.Empty, (_, e) =>
		{
			if (e.Ok)
				tcs.TrySetResult(e.NodesInformation ?? []);
			else
				tcs.TrySetException(Fail(e, "The Neuron did not list the source's nodes."));
			return Task.CompletedTask;
		}, null);
		return tcs.Task.WaitAsync(Timeout);
	}

	/// <summary>Reads one node again, with its parameters and messages.</summary>
	public Task<NodeInformation> GetNodeAsync(IThingReference node)
	{
		TaskCompletionSource<NodeInformation> tcs = NewTcs<NodeInformation>();
		_ = this.cc.GetNode(this.FullJid, node, true, true, Language, string.Empty, string.Empty, string.Empty, (_, e) =>
		{
			if (e.Ok && e.NodeInformation is not null)
				tcs.TrySetResult(e.NodeInformation);
			else
				tcs.TrySetException(Fail(e, "The Neuron did not return the node."));
			return Task.CompletedTask;
		}, null);
		return tcs.Task.WaitAsync(Timeout);
	}

	// ---- Commands and queries ---------------------------------------------------------------

	public Task<IReadOnlyList<NodeCommand>> GetCommandsAsync(IThingReference node)
	{
		TaskCompletionSource<IReadOnlyList<NodeCommand>> tcs = NewTcs<IReadOnlyList<NodeCommand>>();
		_ = this.cc.GetNodeCommands(this.FullJid, node, string.Empty, string.Empty, string.Empty, (_, e) =>
		{
			if (e.Ok)
				tcs.TrySetResult(e.Result ?? []);
			else
				tcs.TrySetException(Fail(e, "The Neuron did not list the node's commands."));
			return Task.CompletedTask;
		}, null);
		return tcs.Task.WaitAsync(Timeout);
	}

	/// <summary>The parameter form of a parametrized command or a query.</summary>
	public Task<DataForm> GetCommandFormAsync(IThingReference node, NodeCommand command)
	{
		TaskCompletionSource<DataForm> tcs = NewTcs<DataForm>();

		Task OnForm(object? _, DataFormEventArgs e)
		{
			if (e.Ok && e.Form is not null)
				tcs.TrySetResult(e.Form);
			else
				tcs.TrySetException(Fail(e, "The Neuron did not return the command's parameters."));
			return Task.CompletedTask;
		}

		// The form's own submit path is not used: forms are sent with ExecuteAsync or StartQueryAsync.
		if (command.Type == CommandType.Query)
			_ = this.cc.GetQueryParameters(this.FullJid, node, command.Command, Language, string.Empty, string.Empty, string.Empty, OnForm, (_, _) => Task.CompletedTask, null);
		else
			_ = this.cc.GetCommandParameters(this.FullJid, node, command.Command, Language, string.Empty, string.Empty, string.Empty, OnForm, (_, _) => Task.CompletedTask, null);

		return tcs.Task.WaitAsync(Timeout);
	}

	/// <summary>Runs a simple command, or a parametrized one with its filled-in form.</summary>
	public Task<FormOutcome> ExecuteAsync(IThingReference node, NodeCommand command, DataForm? parameters = null)
	{
		TaskCompletionSource<FormOutcome> tcs = NewTcs<FormOutcome>();

		Task OnResult(object? _, NodeCommandResponseEventArgs e)
		{
			if (e.Ok)
				tcs.TrySetResult(new FormOutcome(true));
			else if (e.ErrorType == ErrorType.Modify && e.Parameters is not null && e.Parameters != parameters)
				tcs.TrySetResult(new FormOutcome(false, Form: e.Parameters, Error: e.ErrorText));
			else
				tcs.TrySetResult(new FormOutcome(false, Error: ErrorText(e, command.FailureString)));
			return Task.CompletedTask;
		}

		if (parameters is null)
			_ = this.cc.ExecuteCommand(this.FullJid, node, command.Command, Language, string.Empty, string.Empty, string.Empty, OnResult, null);
		else
			_ = this.cc.ExecuteCommand(this.FullJid, node, command.Command, parameters, Language, string.Empty, string.Empty, string.Empty, OnResult, null);

		return tcs.Task.WaitAsync(Timeout);
	}

	/// <summary>
	/// Starts a query. The returned run fills in as the Neuron sends results; it is already
	/// listening, so nothing sent before the caller attaches is lost.
	/// </summary>
	public async Task<QueryRun> StartQueryAsync(IThingReference node, NodeCommand command, DataForm? parameters)
	{
		QueryRun run = new(command.Name);
		NodeQuery query = await this.cc.ExecuteQuery(this.FullJid, node.NodeId, node.SourceId, node.Partition, command.Command, parameters, Language,
			string.Empty, string.Empty, string.Empty, (_, e) =>
			{
				if (!e.Ok)
					run.Fail(e.ErrorType == ErrorType.Modify && e.Parameters is not null ? e.Parameters : null, ErrorText(e, command.FailureString));
				return Task.CompletedTask;
			}, null).WaitAsync(Timeout);

		await run.AttachAsync(query);
		return run;
	}

	// ---- Editing ---------------------------------------------------------------------------

	/// <summary>Opens a node's parameters for editing. Send the filled-in form with <see cref="FormSession.SubmitAsync"/>.</summary>
	public async Task<FormSession> EditAsync(IThingReference node)
	{
		FormSession s = new();
		_ = this.cc.GetNodeParametersForEdit(this.FullJid, node, Language, string.Empty, string.Empty, string.Empty, s.OnForm, s.OnNode, null);
		await s.ReadyAsync();
		return s;
	}

	/// <summary>Node types that can be added under a node.</summary>
	public Task<IReadOnlyList<LocalizedString>> GetAddableTypesAsync(IThingReference parent)
	{
		TaskCompletionSource<IReadOnlyList<LocalizedString>> tcs = NewTcs<IReadOnlyList<LocalizedString>>();
		_ = this.cc.GetAddableNodeTypes(this.FullJid, parent, Language, string.Empty, string.Empty, string.Empty, (_, e) =>
		{
			if (e.Ok)
				tcs.TrySetResult(e.Result ?? []);
			else
				tcs.TrySetException(Fail(e, "The Neuron did not list what can be added here."));
			return Task.CompletedTask;
		}, null);
		return tcs.Task.WaitAsync(Timeout);
	}

	/// <summary>Opens the form for a new node of a type under a parent.</summary>
	public async Task<FormSession> AddAsync(IThingReference parent, string nodeType)
	{
		FormSession s = new();
		_ = this.cc.GetParametersForNewNode(this.FullJid, parent, nodeType, Language, string.Empty, string.Empty, string.Empty, s.OnForm, s.OnNode, null);
		await s.ReadyAsync();
		return s;
	}

	public Task DestroyAsync(IThingReference node)
		=> this.IqAsync((cb) => this.cc.DestroyNode(this.FullJid, node, Language, string.Empty, string.Empty, string.Empty, cb, null), "The Neuron did not delete the node.");

	public Task MoveUpAsync(IThingReference node)
		=> this.IqAsync((cb) => this.cc.MoveNodeUp(this.FullJid, node, Language, string.Empty, string.Empty, string.Empty, cb, null), "The Neuron did not move the node.");

	public Task MoveDownAsync(IThingReference node)
		=> this.IqAsync((cb) => this.cc.MoveNodeDown(this.FullJid, node, Language, string.Empty, string.Empty, string.Empty, cb, null), "The Neuron did not move the node.");

	// ---- Sensor data and control ----------------------------------------------------------

	/// <summary>Reads a node's sensor data. Fields arrive on the returned readout as the Neuron sends them.</summary>
	public async Task<Readout> ReadAsync(IThingReference node, FieldType types)
	{
		SensorClient s = this.sensor ?? throw new ConcentratorException("Sensor data cannot be read through this connection.");
		ThingReference r = new(node.NodeId, node.SourceId, node.Partition);
		SensorDataClientRequest request = await s.RequestReadout(this.FullJid, [r], types).WaitAsync(Timeout);
		return new Readout(request);
	}

	/// <summary>The control parameters of a node, as a form. Send it with <see cref="SetControlAsync"/>.</summary>
	public Task<DataForm> GetControlFormAsync(IThingReference node)
	{
		ControlClient c = this.control ?? throw new ConcentratorException("Control is not available through this connection.");
		TaskCompletionSource<DataForm> tcs = NewTcs<DataForm>();
		ThingReference r = new(node.NodeId, node.SourceId, node.Partition);
		_ = c.GetForm(this.FullJid, Language, (_, e) =>
		{
			if (e.Ok && e.Form is not null)
				tcs.TrySetResult(e.Form);
			else
				tcs.TrySetException(Fail(e, "The Neuron did not return the node's control parameters."));
			return Task.CompletedTask;
		}, null, r);
		return tcs.Task.WaitAsync(Timeout);
	}

	/// <summary>Sets control parameters from a filled-in control form.</summary>
	public Task<FormOutcome> SetControlAsync(IThingReference node, DataForm form)
	{
		ControlClient c = this.control ?? throw new ConcentratorException("Control is not available through this connection.");
		TaskCompletionSource<FormOutcome> tcs = NewTcs<FormOutcome>();
		ThingReference r = new(node.NodeId, node.SourceId, node.Partition);
		_ = c.Set(form, (_, e) =>
		{
			tcs.TrySetResult(e.Ok ? new FormOutcome(true) : new FormOutcome(false, Error: ErrorText(e, "The Neuron did not set the values.")));
			return Task.CompletedTask;
		}, null, r);
		return tcs.Task.WaitAsync(Timeout);
	}

	// ---- Sniffing --------------------------------------------------------------------------

	/// <summary>
	/// Starts forwarding a node's traffic to a sniffer, until <paramref name="expires"/>. Only
	/// nodes that are communication layers can be sniffed, and only by the Neuron's admins.
	/// </summary>
	public Task<(string SnifferId, DateTime Expires)> RegisterSnifferAsync(IThingReference node, ISniffer sniffer, DateTime expires)
	{
		TaskCompletionSource<(string, DateTime)> tcs = NewTcs<(string, DateTime)>();
		_ = this.cc.RegisterSniffer(this.FullJid, node.NodeId, node.SourceId, node.Partition, expires, sniffer, string.Empty, string.Empty, string.Empty, (_, e) =>
		{
			if (e.Ok && !string.IsNullOrEmpty(e.SnifferId))
				tcs.TrySetResult((e.SnifferId, e.Expires == DateTime.MinValue ? expires : e.Expires));
			else if (e.ErrorType == ErrorType.Auth || e.StanzaError is Waher.Networking.XMPP.StanzaErrors.ForbiddenException)
				tcs.TrySetException(new ConcentratorException("Only the Neuron's administrators can sniff: a notification address, or a user with the Admin privilege."));
			else
				tcs.TrySetException(Fail(e, "The Neuron did not start the sniffer."));
			return Task.CompletedTask;
		}, null);
		return tcs.Task.WaitAsync(Timeout);
	}

	/// <summary>Stops a sniffer. Best effort: registrations also expire on their own.</summary>
	public async Task UnregisterSnifferAsync(IThingReference node, string snifferId)
	{
		try
		{
			await this.cc.UnregisterSniffer(this.FullJid, node.NodeId, node.SourceId, node.Partition, snifferId, string.Empty, string.Empty, string.Empty, null, null)
				.WaitAsync(TimeSpan.FromSeconds(10));
		}
		catch (Exception)
		{
			// Expires anyway.
		}
	}

	// ---- Helpers ---------------------------------------------------------------------------

	private Task IqAsync(Func<EventHandlerAsync<IqResultEventArgs>, Task> send, string failure)
	{
		TaskCompletionSource<bool> tcs = NewTcs<bool>();
		_ = send((_, e) =>
		{
			if (e.Ok)
				tcs.TrySetResult(true);
			else
				tcs.TrySetException(Fail(e, failure));
			return Task.CompletedTask;
		});
		return tcs.Task.WaitAsync(Timeout);
	}

	private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal static string ErrorText(IqResultEventArgs e, string? fallback = null)
		=> !string.IsNullOrWhiteSpace(e.ErrorText) ? e.ErrorText
			: !string.IsNullOrWhiteSpace(fallback) ? fallback!
			: e.ErrorType switch
			{
				ErrorType.Auth => "This account may not do that on the Neuron.",
				ErrorType.Cancel => "The Neuron refused.",
				_ => "The Neuron did not answer as expected.",
			};

	private static Exception Fail(IqResultEventArgs e, string fallback) => new ConcentratorException(ErrorText(e, fallback));
}

/// <summary>A Neuron refused or failed a concentrator request. The message is meant for the operator.</summary>
public sealed class ConcentratorException(string message) : Exception(message);

/// <summary>
/// An edit or create form, and its round trips with the Neuron: submit, get remarks back on
/// fields, submit again, until the Neuron accepts or the operator cancels.
/// </summary>
public sealed class FormSession
{
	private readonly TaskCompletionSource<DataForm> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private TaskCompletionSource<FormOutcome>? pending;

	public DataForm Form { get; private set; } = null!;

	internal async Task ReadyAsync() => this.Form = await this.first.Task.WaitAsync(NeuronConcentrator.Timeout);

	internal Task OnForm(object? sender, DataFormEventArgs e)
	{
		if (this.pending is TaskCompletionSource<FormOutcome> p)
		{
			// The Neuron sent the form back: it did not accept the values.
			if (e.Ok && e.Form is not null)
			{
				this.Form = e.Form;
				p.TrySetResult(new FormOutcome(false, Form: e.Form, Error: e.ErrorText));
			}
			else
				p.TrySetResult(new FormOutcome(false, Error: NeuronConcentrator.ErrorText(e)));
		}
		else if (e.Ok && e.Form is not null)
			this.first.TrySetResult(e.Form);
		else
			this.first.TrySetException(new ConcentratorException(NeuronConcentrator.ErrorText(e, "The Neuron did not return a form.")));

		return Task.CompletedTask;
	}

	internal Task OnNode(object? sender, NodeInformationEventArgs e)
	{
		if (this.pending is TaskCompletionSource<FormOutcome> p)
			p.TrySetResult(e.Ok ? new FormOutcome(true, Node: e.NodeInformation) : new FormOutcome(false, Error: NeuronConcentrator.ErrorText(e)));
		else
			this.first.TrySetException(new ConcentratorException(NeuronConcentrator.ErrorText(e, "The Neuron refused.")));

		return Task.CompletedTask;
	}

	/// <summary>Sends the form as it is filled in now.</summary>
	public async Task<FormOutcome> SubmitAsync()
	{
		TaskCompletionSource<FormOutcome> p = new(TaskCreationOptions.RunContinuationsAsynchronously);
		this.pending = p;
		try
		{
			await this.Form.Submit();
			return await p.Task.WaitAsync(NeuronConcentrator.Timeout);
		}
		finally
		{
			this.pending = null;
		}
	}
}
