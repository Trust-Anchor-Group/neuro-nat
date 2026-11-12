using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neuro.Nat.App.Features;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Software;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The contract between NAT and features in projects of their own: without any, NAT shows
/// nothing of them; with one, what it adds shows in its places. No live Neuron.
/// </summary>
public class FeatureContractTests
{
	private const string Lab = "lab.tagroot.io";

	private sealed record Sample(MainWindowViewModel Vm, Guid Ops);

	private static async Task<Sample> SampleAsync(FeatureSet? features)
	{
		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Features." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = Lab, UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		MonitorService monitors = new(manager, new EventHub());
		monitors.For(ops.Profile.Id)!.Preview(new WatchedNeuron { AccountId = ops.Profile.Id, BareJid = Lab, State = WatchState.Live });

		ChatService chat = new(manager, monitors);
		SoftwareService software = new(monitors, chat)
		{
			ReadStandIn = (account, jid) => new NeuronSoftware { AccountId = account, NeuronJid = jid, BuildTime = "2026-11-10 08:12", ReadAt = DateTime.Now },
		};

		MainWindowViewModel vm = new(manager, new FleetService(monitors), chat, null, null, null, software, features: features);
		await vm.StartAsync();
		vm.Fleet.Refresh();
		Dispatcher.UIThread.RunJobs();
		return new Sample(vm, ops.Profile.Id);
	}

	private static List<string> SearchFor(MainWindowViewModel vm, string text)
	{
		vm.Search.Open();
		vm.Search.Query = text;
		List<string> found = [.. vm.Search.Results.Select(r => r.Title)];
		vm.Search.Close();
		return found;
	}

	[AvaloniaFact]
	public async Task WithoutFeaturesNatShowsNothingOfThem()
	{
		// The app's own projects know no feature project: features come only through App.Features.
		foreach (System.Reflection.Assembly a in new[] { typeof(MainWindowViewModel).Assembly, typeof(EngineRuntime).Assembly })
			Assert.DoesNotContain(a.GetReferencedAssemblies(), r => r.Name!.StartsWith("Neuro.Nat.Features.", StringComparison.Ordinal));

		Sample s = await SampleAsync(null);
		MainWindowViewModel vm = s.Vm;
		Assert.Empty(vm.Features.Features);

		vm.OpenNeuron(Lab, s.Ops, WorkspaceKind.Updates);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.IsUpdates);
		Assert.Equal("Built 2026-11-10 08:12, manual updates", vm.Updates.Selected?.Meta);

		Assert.Empty(vm.FeaturePlaces);
		Assert.Empty(vm.PageFeatureTabs);
		Assert.False(vm.Updates.HasFeatureLines);
		Assert.Empty(vm.Updates.FeatureLines);
		Assert.DoesNotContain(vm.Inbox.Everything, r => r.IsFeatureItem);
		Assert.Empty(SearchFor(vm, "telemetry"));

		// On screen: the Software tab and the rail say nothing of telemetry.
		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
			t => t.IsEffectivelyVisible && (t.Text ?? string.Empty).Contains("telemetry", StringComparison.OrdinalIgnoreCase));
		window.Close();
	}

	[AvaloniaFact]
	public async Task AFeatureShowsWhatItAddsWhereItBelongs()
	{
		StandInFeature feature = new();
		FeatureSet features = new([feature]);
		Sample s = await SampleAsync(features);
		MainWindowViewModel vm = s.Vm;
		Assert.Same(features, feature.Host);

		vm.OpenNeuron(Lab, s.Ops, WorkspaceKind.Updates);
		Dispatcher.UIThread.RunJobs();
		Assert.Empty(vm.PageFeatureTabs);
		Assert.False(vm.Updates.HasFeatureLines);

		// The feature finds itself on the Neuron.
		feature.Account = s.Ops;
		feature.Present = true;
		feature.Raise();
		Dispatcher.UIThread.RunJobs();

		Assert.Equal(["Stand-in: installed"], vm.Updates.FeatureLines);
		Assert.Equal(["Numbers", "Parts"], vm.PageFeatureTabs.Select(t => t.Title));
		Assert.Equal(["App numbers"], vm.FeaturePlaces.Select(p => p.Title));
		vm.Inbox.Refresh();
		Assert.Contains(vm.Inbox.Everything, r => r.IsFeatureItem && r.Title == "Fewer NFC reads" && r.Jid == Lab);
		Assert.Contains("NFC reads on Lab", SearchFor(vm, "NFC"));
		Assert.Contains("App numbers", SearchFor(vm, "App numbers"));

		// Its tab on the Neuron's page, then its place in the rail.
		vm.ShowFeatureTabCommand.Execute("standin.tab");
		Assert.True(vm.IsNeuronFeature);
		Assert.True(vm.IsNeuronPage);
		Assert.Equal("tab content", vm.FeatureContent);
		Assert.True(vm.PageFeatureTabs.First().IsCurrent);

		vm.ShowFeaturePlaceCommand.Execute("standin.place");
		Assert.True(vm.IsFeature);
		Assert.Equal("App numbers", vm.WorkspaceTitle);
		Assert.Equal("place content", vm.FeatureContent);
		Assert.True(vm.FeaturePlaces.Single().IsCurrent);

		// A search hit leads to the tab on the Neuron's page.
		vm.Search.Open();
		vm.Search.Query = "NFC reads";
		vm.Search.Run();
		Assert.True(vm.IsNeuronFeature);

		// Gone again: the tab and the place go, and the screen goes back to the page.
		feature.Present = false;
		feature.Raise();
		Dispatcher.UIThread.RunJobs();
		Assert.Empty(vm.PageFeatureTabs);
		Assert.Empty(vm.FeaturePlaces);
		Assert.False(vm.IsNeuronFeature);
		Assert.False(vm.Updates.HasFeatureLines);
	}

	[AvaloniaFact]
	public async Task WithoutFeaturesTheBigScreenKeepsItsOwnPagesAndNothingOffersALink()
	{
		Sample s = await SampleAsync(null);
		List<IBigScreenPage> added = [];
		s.Vm.Features.FeedBigScreen(added);
		Dispatcher.UIThread.RunJobs();
		Assert.Empty(added);

		BigScreenViewModel screen = new(s.Vm.Fleet, new EventHub(), null, added);
		screen.Start();
		Assert.Equal([BigScreenPages.Neurons, BigScreenPages.Problems], screen.Pages.Select(p => p.Key));
		screen.Stop();

		// Links are the shell's to make; until it does, there are none.
		Assert.Null(FeatureLinks.Make(new FeatureTarget("any")));
	}

	[AvaloniaFact]
	public async Task AFeaturesPagesComeAndGoOnTheBigScreenAndAPartOpensInsideItsContent()
	{
		StandInFeature feature = new();
		Sample s = await SampleAsync(new FeatureSet([feature]));
		List<IBigScreenPage> added = [];
		s.Vm.Features.FeedBigScreen(added);
		Assert.Empty(added);

		feature.Account = s.Ops;
		feature.Present = true;
		feature.Raise();
		Dispatcher.UIThread.RunJobs();
		IBigScreenPage page = Assert.Single(added);
		Assert.Equal(("standin.screen", "App numbers", "screen content"), (page.Key, page.Title, page.Content));

		// A target with a part: the tab opens, and its content is asked to show the part.
		s.Vm.OpenFeature(new FeatureTarget("standin.parts", s.Ops, Lab) { Part = "nfc" });
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("nfc", Assert.IsType<PartsContent>(s.Vm.FeatureContent).Shown);

		feature.Present = false;
		feature.Raise();
		Dispatcher.UIThread.RunJobs();
		Assert.Empty(added);
	}

	private sealed class PartsContent : IFeatureContent
	{
		public string? Shown { get; private set; }

		public void ShowPart(string part) => this.Shown = part;
	}

	[Fact]
	public void TheFeaturesHearOfASoftwareChangeOnlyWhenItChanged()
	{
		FeatureSet features = FeatureSet.None();
		List<FeatureNeuron> heard = [];
		features.SoftwareChanged += (_, n) => heard.Add(n);
		Guid account = Guid.NewGuid();

		NeuronSoftware Read(string build, params string[] installed)
			=> new() { AccountId = account, NeuronJid = Lab, BuildTime = build, Installed = installed, ReadAt = DateTime.Now };

		features.NoteSoftware(Read("2026-11-01", "TAG.Neuron.package"));
		features.NoteSoftware(Read("2026-11-01", "TAG.Neuron.package"));
		Assert.Empty(heard);

		features.NoteSoftware(Read("2026-11-10", "TAG.Neuron.package"));
		Assert.Equal([new FeatureNeuron(account, Lab)], heard);

		features.NoteSoftware(Read("2026-11-10", "TAG.Neuron.package", "TAG.Payments.package"));
		Assert.Equal(2, heard.Count);

		// A read that failed says nothing about the software.
		features.NoteSoftware(new NeuronSoftware { AccountId = account, NeuronJid = Lab, Error = "No answer", ReadAt = DateTime.Now });
		features.NoteSoftware(Read("2026-11-10", "TAG.Neuron.package", "TAG.Payments.package"));
		Assert.Equal(2, heard.Count);
	}

	/// <summary>A feature that adds one of everything once it is "on" the Neuron.</summary>
	private sealed class StandInFeature : INatFeature
	{
		public IFeatureHost? Host { get; private set; }
		public bool Present { get; set; }
		public Guid Account { get; set; }

		public string Key => "standin";

		public event EventHandler<FeatureNeuron?>? Changed;

		public void Start(IFeatureHost host) => this.Host = host;

		public void Raise() => this.Changed?.Invoke(this, null);

		public FeaturePresence PresenceOn(Guid accountId, string neuronJid) => this.Present ? FeaturePresence.Present : FeaturePresence.Absent;

		public IReadOnlyList<string> SoftwareLines(Guid accountId, string neuronJid) => this.Present ? ["Stand-in: installed"] : [];

		public IReadOnlyList<FeatureTab> NeuronTabs(Guid accountId, string neuronJid)
			=> this.Present ? [new FeatureTab("standin.tab", "Numbers", () => "tab content"), new FeatureTab("standin.parts", "Parts", () => new PartsContent())] : [];

		public IReadOnlyList<FeatureScreenPage> BigScreenPages() => this.Present ? [new FeatureScreenPage("standin.screen", "App numbers", () => "screen content")] : [];

		public IReadOnlyList<FeaturePlace> Places() => this.Present ? [new FeaturePlace("standin.place", "App numbers", "Fleet", () => "place content")] : [];

		public IReadOnlyList<FeatureInboxItem> InboxItems()
			=> this.Present ? [new FeatureInboxItem("nfc", this.Account, Lab, "App numbers", "Fewer NFC reads", DateTime.Now)] : [];

		public IReadOnlyList<FeatureSearchHit> SearchHits()
			=> this.Present ? [new FeatureSearchHit("NFC reads on Lab", "App numbers", new FeatureTarget("standin.tab", this.Account, Lab))] : [];

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
