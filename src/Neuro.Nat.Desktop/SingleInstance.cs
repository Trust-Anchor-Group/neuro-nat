using System.IO.Pipes;
using System.Text;
using Neuro.Nat.Engine.Links;

namespace Neuro.Nat.Desktop;

/// <summary>
/// Keeps one NAT running per user. The first instance holds a named mutex and listens on a
/// named pipe; a second start asks it to show its window, or to open a link, then exits.
/// </summary>
/// <remarks>
/// What goes through the pipe: one byte <see cref="ShowMessage"/> means "show the window", as it
/// always has. <see cref="LinkMessage"/> is followed by the length of a NAT link in two bytes
/// (high byte first, at most <see cref="NatLink.MaxLength"/>) and the link in ASCII: show the
/// window and open what the link points at. Anything else is ignored.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
	/// <summary>"Show the window."</summary>
	internal const byte ShowMessage = 1;

	/// <summary>"Show the window and open this link."</summary>
	internal const byte LinkMessage = 2;

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

	/// <summary>Tells the running NAT to show its window, and to open <paramref name="link"/> when there is one.</summary>
	public static void AskRunningToShow(string? link = null)
	{
		try
		{
			using NamedPipeClientStream client = new(".", name, PipeDirection.Out);
			client.Connect(3000);
			client.Write(Message(link));
			client.Flush();
		}
		catch (Exception)
		{
			// The running instance is starting or stopping; nothing more to do.
		}
	}

	/// <summary>The bytes that ask for the window, with a link to open when one is given and it is a NAT link.</summary>
	internal static byte[] Message(string? link)
	{
		if (link is null || !NatLink.LooksLike(link))
			return [ShowMessage];

		link = link.Trim();
		if (link.Length > NatLink.MaxLength || link.Any(c => c <= ' ' || c > '~'))
			return [ShowMessage];

		byte[] text = Encoding.ASCII.GetBytes(link);
		byte[] message = new byte[3 + text.Length];
		message[0] = LinkMessage;
		message[1] = (byte)(text.Length >> 8);
		message[2] = (byte)text.Length;
		text.CopyTo(message, 3);
		return message;
	}

	/// <summary>What another start asked for.</summary>
	internal readonly record struct Request(string? Link);

	/// <summary>
	/// Reads one message. Null for anything that is not one of the two: an unknown first byte,
	/// a length out of range, a link cut short or with characters a link cannot have. Gives up
	/// after <paramref name="cancel"/>, so a start that connects and says nothing cannot hold the pipe.
	/// </summary>
	internal static async Task<Request?> ReadAsync(Stream input, CancellationToken cancel)
	{
		try
		{
			byte[] head = new byte[1];
			if (await input.ReadAsync(head, cancel) != 1)
				return null;

			if (head[0] == ShowMessage)
				return new Request(null);

			if (head[0] != LinkMessage)
				return null;

			byte[] size = new byte[2];
			await input.ReadExactlyAsync(size, cancel);
			int length = (size[0] << 8) | size[1];
			if (length is 0 or > NatLink.MaxLength)
				return null;

			byte[] text = new byte[length];
			await input.ReadExactlyAsync(text, cancel);
			if (text.Any(b => b <= ' ' || b > '~'))
				return null;

			string link = Encoding.ASCII.GetString(text);
			return NatLink.LooksLike(link) ? new Request(link) : null;
		}
		catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException)
		{
			return null;
		}
	}

	/// <summary>Calls <paramref name="show"/> whenever another start asks for the window, and <paramref name="open"/> with a link it brings.</summary>
	public void Listen(Action show, Action<string> open)
	{
		_ = Task.Run(async () =>
		{
			while (!this.stop.IsCancellationRequested)
			{
				try
				{
					using NamedPipeServerStream server = new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
					await server.WaitForConnectionAsync(this.stop.Token);

					using CancellationTokenSource quick = CancellationTokenSource.CreateLinkedTokenSource(this.stop.Token);
					quick.CancelAfter(TimeSpan.FromSeconds(3));
					if (await ReadAsync(server, quick.Token) is Request request)
					{
						if (request.Link is string link)
							open(link);
						else
							show();
					}
				}
				catch (OperationCanceledException) when (this.stop.IsCancellationRequested)
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

	private bool disposed;

	public void Dispose()
	{
		if (this.disposed)
			return;

		this.disposed = true;
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
