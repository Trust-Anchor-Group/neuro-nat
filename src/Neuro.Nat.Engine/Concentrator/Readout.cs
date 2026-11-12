using Waher.Networking.XMPP.Sensor;
using Waher.Things;
using Waher.Things.SensorData;

namespace Neuro.Nat.Engine.Concentrator;

/// <summary>A sensor data readout in progress: fields and errors as the Neuron sends them.</summary>
public sealed class Readout
{
	private readonly List<Field> fields = [];
	private readonly List<ThingError> errors = [];

	internal Readout(SensorDataClientRequest request)
	{
		this.Request = request;
		request.OnFieldsReceived += (_, f) =>
		{
			lock (this.fields)
				this.fields.AddRange(f);
			return this.Raise();
		};
		request.OnErrorsReceived += (_, e) =>
		{
			lock (this.errors)
				this.errors.AddRange(e);
			return this.Raise();
		};
		request.OnStateChanged += (_, _) => this.Raise();
	}

	public SensorDataClientRequest Request { get; }
	public SensorDataReadoutState State => this.Request.State;
	public bool IsFinished => this.State is SensorDataReadoutState.Done or SensorDataReadoutState.Cancelled or SensorDataReadoutState.Failure;

	public IReadOnlyList<Field> Fields
	{
		get
		{
			lock (this.fields)
				return [.. this.fields];
		}
	}

	public IReadOnlyList<ThingError> Errors
	{
		get
		{
			lock (this.errors)
				return [.. this.errors];
		}
	}

	/// <summary>Raised when fields, errors or the state change. Any thread.</summary>
	public event EventHandler? Changed;

	private Task Raise()
	{
		this.Changed?.Invoke(this, EventArgs.Empty);
		return Task.CompletedTask;
	}
}
