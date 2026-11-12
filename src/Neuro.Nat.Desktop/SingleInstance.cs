using System.IO.Pipes;

namespace Neuro.Nat.Desktop;

/// <summary>
/// Keeps one NAT running per user. The first instance holds a named mutex and listens on a
/// named pipe; a second start asks it to show its window, then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
	private static readonly string name = "Neuro.Nat." + Environment.UserName;
	private readonly Mutex mutex;
	private readonly CancellationTokenSource stop = new();

	private SingleInstance(Mutex mutex) => this.mutex = mutex;

	/// <summary>Claims the instance, or returns null when another NAT already runs.</summary>
	public static SingleInstance? TryClaim()
	{
		Mutex m = new(true, name, out bool created);
		if (created)
			return new SingleInstance(m);

		m.Dispose();
		return null;
	}

	/// <summary>Tells the running NAT to show its window.</summary>
	public static void AskRunningToShow()
	{
		try
		{
			using NamedPipeClientStream client = new(".", name, PipeDirection.Out);
			client.Connect(3000);
			client.WriteByte(1);
		}
		catch (Exception)
		{
			// The running instance is starting or stopping; nothing more to do.
		}
	}

	/// <summary>Calls <paramref name="show"/> whenever another start asks for the window.</summary>
	public void Listen(Action show)
	{
		_ = Task.Run(async () =>
		{
			while (!this.stop.IsCancellationRequested)
			{
				try
				{
					using NamedPipeServerStream server = new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
					await server.WaitForConnectionAsync(this.stop.Token);
					if (server.ReadByte() == 1)
						show();
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception)
				{
					await Task.Delay(1000);
				}
			}
		});
	}

	public void Dispose()
	{
		this.stop.Cancel();
		try
		{
			this.mutex.ReleaseMutex();
		}
		catch (ApplicationException)
		{
		}
		this.mutex.Dispose();
	}
}
