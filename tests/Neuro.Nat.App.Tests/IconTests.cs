using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Neuro.Nat.App.Watch;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The NAT icon: the dot's colour for each state, pictures of it for review, and the program's
/// icon file, which is made from the same drawing.
/// </summary>
public class IconTests
{
	[Fact]
	public void TheDotFollowsTheState()
	{
		Assert.Equal(IconState.Quiet, IconArt.StateFor(anyConnected: true, neuronsDown: 0, waiting: 0));
		Assert.Equal(IconState.NeedsYou, IconArt.StateFor(anyConnected: true, neuronsDown: 0, waiting: 3));
		Assert.Equal(IconState.Down, IconArt.StateFor(anyConnected: true, neuronsDown: 1, waiting: 0));

		// Red wins over amber; with no account connected nothing else can be known.
		Assert.Equal(IconState.Down, IconArt.StateFor(anyConnected: true, neuronsDown: 2, waiting: 5));
		Assert.Equal(IconState.NotConnected, IconArt.StateFor(anyConnected: false, neuronsDown: 2, waiting: 5));
		Assert.Equal(IconState.NotConnected, IconArt.StateFor(anyConnected: false, neuronsDown: 0, waiting: 0));

		Assert.Equal(Colors.White, IconArt.DotColor(IconState.Quiet));
		Assert.Equal(Color.Parse("#FFB020"), IconArt.DotColor(IconState.NeedsYou));
		Assert.Equal(Color.Parse("#F04646"), IconArt.DotColor(IconState.Down));
		Assert.Equal(Color.Parse("#787E8A"), IconArt.DotColor(IconState.NotConnected));
	}

	/// <summary>Writes the icon at 16, 32 and 256 pixels in each state, the tray's at 16, and the ring alone, for review.</summary>
	[AvaloniaFact]
	public void RendersTheIconInEachState()
	{
		string folder = Path.Combine(RenameTests.RepositoryRoot(), ".impeccable", "review");
		Directory.CreateDirectory(folder);

		foreach (IconState state in Enum.GetValues<IconState>())
		{
			Color dot = IconArt.DotColor(state);
			foreach (int size in new[] { 16, 32, 256 })
				File.WriteAllBytes(Path.Combine(folder, $"icon-{state.ToString().ToLowerInvariant()}-{size}.png"), IconArt.Png(size, dot));
			File.WriteAllBytes(Path.Combine(folder, $"icon-{state.ToString().ToLowerInvariant()}-16-tray.png"), IconArt.Png(16, dot, tray: true));

			Assert.NotNull(AppIcon.Get(state));
			Assert.NotNull(AppIcon.Get(state, tray: true));
		}

		File.WriteAllBytes(Path.Combine(folder, "icon-ring-alone.png"), IconArt.Png(600, Colors.White, ringOnly: true, small: false));
		File.WriteAllBytes(Path.Combine(folder, "icon-small-variant-256.png"), IconArt.Png(256, Colors.White, small: true));
	}

	/// <summary>
	/// The program's icon file. With NAT_MAKE_ICON=1 it is drawn again from <see cref="IconArt"/>
	/// (tools/installer/Make-Icon.ps1 does that); either way it must hold every size Windows uses.
	/// </summary>
	[AvaloniaFact]
	public void TheIconFileHoldsEverySize()
	{
		string file = Path.Combine(RenameTests.RepositoryRoot(), "src", "Neuro.Nat.Desktop", "Assets", "NAT.ico");
		if (Environment.GetEnvironmentVariable("NAT_MAKE_ICON") == "1")
			File.WriteAllBytes(file, IconArt.Ico(IconArt.FileSizes, IconArt.Quiet));

		using BinaryReader r = new(File.OpenRead(file));
		Assert.Equal(0, r.ReadUInt16());
		Assert.Equal(1, r.ReadUInt16());
		int count = r.ReadUInt16();
		List<int> sizes = [];
		for (int i = 0; i < count; i++)
		{
			byte w = r.ReadByte();
			r.ReadBytes(15);
			sizes.Add(w == 0 ? 256 : w);
		}

		Assert.Equal(IconArt.FileSizes, sizes);
	}
}
