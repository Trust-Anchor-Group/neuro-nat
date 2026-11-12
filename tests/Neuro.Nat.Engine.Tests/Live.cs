using System.Diagnostics.CodeAnalysis;

namespace Neuro.Nat.Engine.Tests;

/// <summary>Live tests need the local test Neuron's keys (see tools/neuron-dev/README.md). Without them they show as skipped, never as passed.</summary>
internal static class Live
{
	/// <summary>Skips the test, naming those of <paramref name="keys"/> that are not set.</summary>
	[DoesNotReturn]
	public static void Skip(params string[] keys)
	{
		string[] missing = [.. keys.Where(k => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k)))];
		Assert.Skip("Needs " + string.Join(", ", missing.Length > 0 ? missing : keys) + ".");
		throw new InvalidOperationException();
	}
}
