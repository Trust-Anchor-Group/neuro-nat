using System.Text.RegularExpressions;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The words on screen: no middle dot between facts, and a Neuron goes by its roster name or
/// its domain, with the full address only when two would look the same.
/// </summary>
public sealed partial class TextPassTests
{
	private const char MiddleDot = '·';

	private static string Root()
	{
		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;
		return d?.FullName ?? throw new DirectoryNotFoundException("Neuro.Nat.slnx not found above " + AppContext.BaseDirectory);
	}

	[GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
	private static partial Regex XmlComment();

	[GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
	private static partial Regex BlockComment();

	// A string literal, or the start of a line comment (which ends the line's code).
	[GeneratedRegex(@"""(?:[^""\\\r\n]|\\.)*""|'(?:[^'\\\r\n]|\\.)+'|//")]
	private static partial Regex LiteralOrComment();

	[Fact]
	public void NoMiddleDotInWhatTheViewsShow()
	{
		string root = Root();
		List<string> found = [];

		foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "Neuro.Nat.App"), "*.axaml", SearchOption.AllDirectories))
		{
			string text = XmlComment().Replace(File.ReadAllText(file), m => new string('\n', m.Value.Count(c => c == '\n')));
			string[] lines = text.Split('\n');
			for (int i = 0; i < lines.Length; i++)
			{
				if (lines[i].Contains(MiddleDot))
					found.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
			}
		}

		foreach (string dir in new[] { Path.Combine(root, "src", "Neuro.Nat.App"), Path.Combine(root, "src", "Neuro.Nat.Engine") })
		{
			foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
			{
				if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
					|| file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
					continue;

				string text = BlockComment().Replace(File.ReadAllText(file), m => new string('\n', m.Value.Count(c => c == '\n')));
				string[] lines = text.Split('\n');
				for (int i = 0; i < lines.Length; i++)
				{
					if (lines[i].Contains("AppLog.", StringComparison.Ordinal))
						continue;

					foreach (Match m in LiteralOrComment().Matches(lines[i]))
					{
						if (m.Value == "//")
							break;
						if (m.Value.Contains(MiddleDot))
							found.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {m.Value}");
					}
				}
			}
		}

		Assert.True(found.Count == 0, "A middle dot is shown on screen. Make the facts separate pieces, or one short phrase:\n" + string.Join("\n", found));
	}

	[Fact]
	public void TheScanFindsADotInALiteralButNotInAComment()
	{
		string line = "x = \"a · b\"; // c · d";
		List<string> hits = [.. LiteralOrComment().Matches(line).Cast<Match>().TakeWhile(m => m.Value != "//").Select(m => m.Value).Where(v => v.Contains(MiddleDot))];
		Assert.Equal(["\"a · b\""], hits);

		string comment = "// only · here \"and · here\"";
		Assert.DoesNotContain(LiteralOrComment().Matches(comment).Cast<Match>().TakeWhile(m => m.Value != "//"), m => m.Value.Contains(MiddleDot));
	}

	[Fact]
	public void ANeuronGoesByItsRosterNameElseItsDomain()
	{
		Assert.Equal("Lab", NeuronNames.Of("Lab", "neuron-7f3a9c@lab.tagroot.io"));
		Assert.Equal("Lab", NeuronNames.Of("  Lab ", "neuron-7f3a9c@lab.tagroot.io"));
		Assert.Equal("lab.tagroot.io", NeuronNames.Of(null, "neuron-7f3a9c@lab.tagroot.io"));
		Assert.Equal("neuronode", NeuronNames.Of(null, "neuronode@"));   // a local Neuron without a domain
		Assert.Equal("lab.tagroot.io", NeuronNames.Of("  ", "neuron-7f3a9c@lab.tagroot.io"));
		Assert.Equal("lab.tagroot.io", NeuronNames.Of(null, "lab.tagroot.io"));
		Assert.Equal("lab.tagroot.io", NeuronNames.Domain("neuron@lab.tagroot.io/abc123"));
		Assert.Equal("lab.tagroot.io", NeuronNames.Domain("lab.tagroot.io"));

		WatchedNeuron named = new() { AccountId = Guid.NewGuid(), BareJid = "a1@lab.example", Name = "Lab" };
		WatchedNeuron plain = new() { AccountId = Guid.NewGuid(), BareJid = "a1@prod.example" };
		Assert.Equal("Lab", named.Label);
		Assert.Equal("prod.example", plain.Label);
	}

	[Fact]
	public void TwoNeuronsThatWouldLookTheSameShowTheirFullAddresses()
	{
		IReadOnlyDictionary<string, string> names = NeuronNames.Distinct(
		[
			(null, "edge-1@site.example"),
			(null, "edge-2@site.example"),
			("Lab", "x@lab.example"),
			(null, "lab.example"),
			(null, "prod.example"),
			("Prod", "p@prod.example"),
			("prod", "q@other.example"),
		]);

		Assert.Equal("edge-1@site.example", names["edge-1@site.example"]);
		Assert.Equal("edge-2@site.example", names["EDGE-2@site.example"]);
		Assert.Equal("Lab", names["x@lab.example"]);
		Assert.Equal("lab.example", names["lab.example"]);
		Assert.Equal("prod.example", names["prod.example"]);
		Assert.Equal("p@prod.example", names["p@prod.example"]);
		Assert.Equal("q@other.example", names["q@other.example"]);

		// The same Neuron through two accounts is one Neuron, not two that look the same.
		Guid a = Guid.NewGuid(), b = Guid.NewGuid();
		WatchedNeuron[] known =
		[
			new() { AccountId = a, BareJid = "n@lab.example" },
			new() { AccountId = b, BareJid = "n@lab.example" },
			new() { AccountId = a, BareJid = "m@site.example" },
			new() { AccountId = a, BareJid = "k@site.example" },
		];
		Assert.Equal("lab.example", NeuronNames.For(known, "n@lab.example"));
		Assert.Equal("m@site.example", NeuronNames.For(known, "m@site.example"));
		Assert.Equal("unknown.example", NeuronNames.For(known, "x@unknown.example"));
	}
}
