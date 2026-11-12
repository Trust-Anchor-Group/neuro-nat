using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Events;

[assembly: AvaloniaTestApplication(typeof(Neuro.Nat.App.Tests.TestAppBuilder))]

namespace Neuro.Nat.App.Tests;

public static class TestAppBuilder
{
	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder.Configure<App>()
			.UseSkia()
			.WithInterFont()
			.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the shell headlessly with synthetic sample data and saves frames for visual
/// review in .impeccable/review/ under the repository root.
/// </summary>
public class ShellRenderTests
{
	private static string ReviewFolder()
	{
		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;

		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		return folder;
	}

	private static async Task<MainWindowViewModel> SampleViewModel()
	{
		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		InMemoryCredentialVault vault = new();
		AccountManager manager = new(new JsonAccountStore(store), vault);

		AccountSession alpha = await manager.AddAsync(new AccountProfile { Host = "localhost", Domain = "alpha.lab.local", UserName = "admin", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		await manager.AddAsync(new AccountProfile { Host = "beta.lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		AccountSession gamma = await manager.AddAsync(new AccountProfile { Host = "gamma.lab.local", UserName = "mille", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);

		// Synthetic traffic on the first account.
		alpha.Sniffer.Information("Connecting to localhost:5222");
		alpha.Sniffer.TransmitText("<?xml version='1.0'?><stream:stream to='alpha.lab.local' version='1.0' xml:lang='en' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'>");
		alpha.Sniffer.ReceiveText("<stream:features><starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'><required/></starttls></stream:features>");
		alpha.Sniffer.TransmitText("<starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>");
		alpha.Sniffer.ReceiveText("<proceed xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>");
		alpha.Sniffer.Information("Switching to TLS.");
		alpha.Sniffer.TransmitText("<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='SCRAM-SHA-256'>…</auth>");
		alpha.Sniffer.ReceiveText("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>…</success>");
		alpha.Sniffer.TransmitText("<iq type='get' id='3' xmlns='jabber:client'><query xmlns='jabber:iq:roster'/></iq>");
		alpha.Sniffer.Warning("Server certificate not validated (trusted by account setting).");

		// A realistic failure on the third account: no stored password.
		await vault.RemoveAsync(gamma.Profile.Id);
		await gamma.ConnectAsync();

		MainWindowViewModel vm = new(manager, new FleetService(new MonitorService(manager, new EventHub())));
		await vm.StartAsync();
		await Task.Delay(300);
		foreach (AccountItemViewModel a in vm.Accounts)
			a.Sniffer.Flush();
		return vm;
	}

	private static void Capture(MainWindow window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		frame!.Save(Path.Combine(ReviewFolder(), name + ".png"));
	}

	[AvaloniaTheory]
	[InlineData("Light", 1440, 900)]
	[InlineData("Dark", 1440, 900)]
	[InlineData("Light", 900, 700)]
	[InlineData("Dark", 720, 640)]
	public async Task RendersAccountsWorkspace(string theme, int width, int height)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		MainWindowViewModel vm = await SampleViewModel();
		MainWindow window = new() { DataContext = vm, Width = width, Height = height };
		window.Show();
		Capture(window, $"shell-accounts-{theme.ToLowerInvariant()}-{width}");

		vm.SelectedAccount = vm.Accounts[2];
		Capture(window, $"shell-accounts-failed-{theme.ToLowerInvariant()}-{width}");

		Assert.Equal(3, vm.Accounts.Count);
		Assert.True(vm.Accounts[2].IsFailed);
		window.Close();
	}

	private static void AddSampleEvents(EventHub hub)
	{
		DateTime now = DateTime.Now;
		string[] neurons = ["alpha@lab.local", "beta@lab.local", "gamma@lab.local"];
		Random r = new(7);

		void Add(int secondsAgo, Neuro.Nat.Engine.Sniffing.SnifferDirection d, string neuron, string text)
		{
			Neuro.Nat.Engine.Sniffing.SnifferLine line = new(0, now.AddSeconds(-secondsAgo), d, text);
			if (SniffedEventParser.Parse(line, new EventSource(Guid.Empty, neuron)) is NeuronEvent e)
				hub.Add(e);
		}

		// Synthetic sample data.
		for (int i = 0; i < 60; i++)
		{
			string n = neurons[r.Next(3)];
			Add(i * 45 + r.Next(20), Neuro.Nat.Engine.Sniffing.SnifferDirection.Error, n,
				$"SocketException: Connection reset by peer ({neurons[r.Next(3)].Split('@')[0]}.lab.local:5269)\n\nModule: Waher.Networking.XMPP.Server\nEventId: S2S\nStackTrace:    at Waher.Networking.BinaryTcpClient.ReadAsync()\n   at Waher.Networking.XMPP.Server.S2S.ServerConnection.ReadLoop()\nRemoteEndPoint: 10.0.0.{r.Next(200)}:5269");
		}
		for (int i = 0; i < 12; i++)
			Add(i * 140 + 30, Neuro.Nat.Engine.Sniffing.SnifferDirection.Error, "beta@lab.local", $"TimeoutException in ConcentratorServer.ReadSensorData (node Meter-{r.Next(40)})\n\nModule: Waher.Things\nObject: Meter-{r.Next(40)}");
		Add(620, Neuro.Nat.Engine.Sniffing.SnifferDirection.Exception, "gamma@lab.local", "Backup upload failed: remote host rejected the file\n\nModule: Waher.IoTGateway\nEventId: Backup");
		for (int i = 0; i < 8; i++)
			Add(i * 300 + 10, Neuro.Nat.Engine.Sniffing.SnifferDirection.Warning, "alpha@lab.local", $"Endpoint 203.0.113.{40 + i} blocked after 5 failed logins\n\nModule: Waher.Security.LoginMonitor\nEventId: Blocked");
		for (int i = 0; i < 30; i++)
			Add(i * 50 + 5, Neuro.Nat.Engine.Sniffing.SnifferDirection.Information, neurons[r.Next(3)], "12 clients connected, 3 disconnected in the last minute\n\nModule: Waher.Networking.XMPP.Server");
	}

	[AvaloniaTheory]
	[InlineData("Light", 1440, 900, false)]
	[InlineData("Dark", 1440, 900, true)]
	[InlineData("Light", 1100, 760, true)]
	public async Task RendersEventsWorkspace(string theme, int width, int height, bool selectIssue)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		await manager.AddAsync(new AccountProfile { Host = "localhost", UserName = "neuroaa", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		MonitorService monitors = new(manager, new EventHub());
		AddSampleEvents(monitors.Hub);

		// Synthetic sample data: the Neurons the events come from, and one this account cannot read.
		Guid account = manager.Sessions[0].Profile.Id;
		foreach ((string jid, WatchState state) in new[] { ("alpha@lab.local", WatchState.Live), ("beta@lab.local", WatchState.Live), ("gamma@lab.local", WatchState.Live), ("delta@prod.example", WatchState.Forbidden) })
		{
			monitors.For(account)!.Preview(new WatchedNeuron
			{
				AccountId = account,
				BareJid = jid,
				State = state,
				HistoryState = state == WatchState.Live ? HistoryState.Loaded : HistoryState.NotLoaded,
				HistoryCount = state == WatchState.Live ? 212 : 0,
			});
		}

		MainWindowViewModel vm = new(manager, new FleetService(monitors));
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Events;
		vm.Fleet.Refresh();
		vm.Events.Flush();

		if (selectIssue)
		{
			vm.Events.SelectedIssue = vm.Events.Issues[0];
			vm.Events.SelectedEvent = vm.Events.Stream[0];
		}

		MainWindow window = new() { DataContext = vm, Width = width, Height = height };
		window.Show();
		Capture(window, $"events-{theme.ToLowerInvariant()}-{width}{(selectIssue ? "-issue" : "")}");

		Assert.True(vm.Events.Issues.Count >= 3);
		Assert.Equal(4, vm.NeuronChips.Count);
		Assert.Equal("3 of 4 Neurons reporting", vm.StatusNeurons);
		window.Close();
	}

	[AvaloniaFact]
	public async Task SearchBoxWorksFromTheKeyboardAndWithAClick()
	{
		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		await manager.AddAsync(new AccountProfile { Host = "localhost", UserName = "neuroaa", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		MainWindowViewModel vm = new(manager, new FleetService(new MonitorService(manager, new EventHub())));
		await vm.StartAsync();

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		Avalonia.Controls.TextBox box = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBox>(window, "SearchBox")!;

		// Ctrl+K opens it, typing narrows, Enter goes there and closes it.
		window.KeyPressQwerty(Avalonia.Input.PhysicalKey.K, Avalonia.Input.RawInputModifiers.Control);
		Dispatcher.UIThread.RunJobs();
		Assert.True(box.IsFocused);
		Assert.True(vm.Search.IsOpen);

		window.KeyTextInput("sett");
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("Settings", vm.Search.Selected?.Title);

		window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(WorkspaceKind.Settings, vm.Workspace);
		Assert.False(vm.Search.IsOpen);
		Assert.False(box.IsFocused);
		Assert.Equal(string.Empty, vm.Search.Query);

		// Down moves the highlight; Escape closes without going anywhere.
		window.KeyPressQwerty(Avalonia.Input.PhysicalKey.K, Avalonia.Input.RawInputModifiers.Control);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("Inbox", vm.Search.Selected?.Title);
		window.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowDown, Avalonia.Input.RawInputModifiers.None);
		Assert.Equal("Neurons", vm.Search.Selected?.Title);
		window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.False(vm.Search.IsOpen);
		Assert.Equal(WorkspaceKind.Settings, vm.Workspace);

		// A click on a result goes there too.
		window.KeyPressQwerty(Avalonia.Input.PhysicalKey.K, Avalonia.Input.RawInputModifiers.Control);
		Dispatcher.UIThread.RunJobs();
		Avalonia.Controls.ListBox results = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.ListBox>(window, "SearchResults")!;
		Avalonia.Controls.Control third = results.ContainerFromIndex(2)!;
		Point at = third.TranslatePoint(new Point(third.Bounds.Width / 2, third.Bounds.Height / 2), window)!.Value;
		window.MouseDown(at, Avalonia.Input.MouseButton.Left);
		window.MouseUp(at, Avalonia.Input.MouseButton.Left);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(WorkspaceKind.Events, vm.Workspace);
		Assert.False(vm.Search.IsOpen);

		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task NeuronChipFiltersEventsAndSearchGoesPlaces(string theme)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		await manager.AddAsync(new AccountProfile { Host = "localhost", UserName = "neuroaa", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		MonitorService monitors = new(manager, new EventHub());
		AddSampleEvents(monitors.Hub);

		Guid account = manager.Sessions[0].Profile.Id;
		foreach (string jid in new[] { "alpha@lab.local", "beta@lab.local", "gamma@lab.local" })
			monitors.For(account)!.Preview(new WatchedNeuron { AccountId = account, BareJid = jid, State = WatchState.Live, HistoryState = HistoryState.Loaded, HistoryCount = 212 });

		MainWindowViewModel vm = new(manager, new FleetService(monitors));
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Events;
		vm.Fleet.Refresh();
		vm.Events.SeverityFilter = SeverityFilter.All;
		vm.Events.Flush();
		int everything = vm.Events.Stream.Count;
		int allIssues = vm.Events.Issues.Count;

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();

		// One chip: only that Neuron's events and issues remain.
		vm.ToggleNeuronCommand.Execute(vm.NeuronChips.First(c => c.BareJid == "gamma@lab.local"));
		Capture(window, $"events-chip-{theme.ToLowerInvariant()}-1440");
		Assert.Equal("gamma@lab.local", vm.Events.NeuronFilter);
		Assert.NotEmpty(vm.Events.Stream);
		Assert.All(vm.Events.Stream, e => Assert.Equal("gamma@lab.local", e.Source.NeuronJid));
		Assert.True(vm.Events.Stream.Count < everything);
		Assert.True(vm.Events.Issues.Count < allIssues);
		Assert.False(vm.IsAllNeurons);

		// The same chip again, or All Neurons, shows everything.
		vm.ShowAllNeuronsCommand.Execute(null);
		Assert.Null(vm.Events.NeuronFilter);
		Assert.Equal(everything, vm.Events.Stream.Count);

		// The search box narrows as you type and goes where you choose.
		vm.Search.Open();
		vm.Search.Query = "ma";
		Capture(window, $"search-{theme.ToLowerInvariant()}-1440");
		Assert.Equal("Map", vm.Search.Results[0].Title);
		vm.Search.Run();
		Assert.Equal(WorkspaceKind.Topology, vm.Workspace);
		Assert.Equal(PlaceKind.Neurons, vm.Place);
		Assert.False(vm.Search.IsOpen);

		// A Neuron in the search list opens its page, and "beta soft" goes straight to a tab of it.
		vm.Search.Open();
		vm.Search.Query = "beta";
		vm.Search.Run();
		Assert.Equal(WorkspaceKind.Neuron, vm.Workspace);
		Assert.Equal("beta@lab.local", vm.Page?.BareJid);
		Assert.Equal(PlaceKind.Neurons, vm.Place);
		vm.Search.Open();
		vm.Search.Query = "beta soft";
		vm.Search.Run();
		Assert.Equal(WorkspaceKind.Updates, vm.Workspace);
		Assert.Equal("beta@lab.local", vm.Page?.BareJid);

		// Back leaves the page for the list or the map, whichever you came from.
		vm.BackToNeuronsCommand.Execute(null);
		Assert.Equal(WorkspaceKind.Topology, vm.Workspace);

		// Leaving a place and coming back returns to the screen you were on.
		vm.ShowTopologyCommand.Execute(null);
		vm.ShowChatCommand.Execute(null);
		vm.ShowNeuronsCommand.Execute(null);
		Assert.Equal(WorkspaceKind.Topology, vm.Workspace);
		Capture(window, $"neurons-map-tab-{theme.ToLowerInvariant()}-1440");

		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light", 1440, 900)]
	[InlineData("Dark", 1440, 900)]
	[InlineData("Light", 1100, 720)]
	public async Task RendersFleetWorkspace(string theme, int width, int height)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		AccountSession admin = await manager.AddAsync(new AccountProfile { Host = "prod.example", UserName = "admin", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);

		MonitorService monitors = new(manager, new EventHub());
		FleetService fleet = new(monitors);
		AddSampleEvents(monitors.Hub);

		// Synthetic sample data.
		static SampleSeries Series(double start, double end, int n, string unit)
			=> new(end, [.. Enumerable.Range(0, n).Select(i => start + (end - start) * i / (n - 1) + Math.Sin(i) * 3)], unit);

		void Neuron(AccountSession s, string jid, WatchState state, string? error = null, NeuronHealth? h = null)
		{
			monitors.For(s.Profile.Id)!.Preview(new WatchedNeuron
			{
				AccountId = s.Profile.Id,
				BareJid = jid,
				State = state,
				Error = error,
				HistoryState = state == WatchState.Live ? HistoryState.Loaded : HistoryState.NotLoaded,
				HistoryCount = state == WatchState.Live ? 212 : 0,
			});
			if (h is not null)
				fleet.Preview(h with { AccountId = s.Profile.Id, NeuronJid = jid });
		}

		NeuronHealth Health(double cpuFrom, double cpuTo, double alloc, double free, double disk) => new()
		{
			AccountId = Guid.Empty,
			NeuronJid = string.Empty,
			Software = "Waher.IoTGateway 3.11.1.0",
			Cpu = Series(cpuFrom, cpuTo, 30, "%"),
			AllocatedMemory = new SampleSeries(alloc, [alloc], "MB"),
			FreeMemory = new SampleSeries(free, [free], "MB"),
			FreeDisk = new SampleSeries(disk, [disk], "GB"),
			ReadAt = DateTime.Now,
		};

		Neuron(ops, "alpha@lab.local", WatchState.Live, h: Health(12, 18, 640, 5210, 182.4));
		Neuron(ops, "beta@lab.local", WatchState.Live, h: Health(35, 92, 1710, 812, 41.2));
		Neuron(admin, "gamma@lab.local", WatchState.Live, h: Health(8, 9, 410, 9800, 512.0) with { Cpu = SampleSeries.Empty, FreeMemory = SampleSeries.Empty });
		Neuron(admin, "delta@prod.example", WatchState.Forbidden, "This account is not an administrator on the Neuron. Add it as a notification address, or give its user the Admin privilege.");

		MainWindowViewModel vm = new(manager, fleet);
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Fleet;
		vm.Fleet.Refresh();

		MainWindow window = new() { DataContext = vm, Width = width, Height = height };
		window.Show();
		Capture(window, $"fleet-{theme.ToLowerInvariant()}-{width}");

		Assert.Equal(4, vm.Fleet.Rows.Count);
		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light", 1440, 900, false)]
	[InlineData("Dark", 1440, 900, false)]
	[InlineData("Light", 1100, 720, true)]
	public async Task RendersAttentionWorkspace(string theme, int width, int height, bool all)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		AccountSession admin = await manager.AddAsync(new AccountProfile { Host = "prod.example", UserName = "admin", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);

		MonitorService monitors = new(manager, new EventHub());
		DateTime now = DateTime.Now;

		void Add(AccountSession s, string jid, int minutesAgo, string markdown, Engine.Attention.AttentionState state = Engine.Attention.AttentionState.Unread)
			=> monitors.Attention.Preview(new Engine.Attention.NeuronNotification
			{
				Id = Guid.NewGuid().ToString("N"),
				AccountId = s.Profile.Id,
				NeuronJid = jid,
				Received = now.AddMinutes(-minutesAgo),
				Markdown = markdown,
				State = state,
			});

		// Wording as the Neuron writes it (IoTGateway and broker source).
		Add(admin, "beta@prod.example", 3,
			"Alert\n===============\n\nLedger database is corrupt. Automatic repair started; new contracts are rejected until it finishes.\n\n" +
			"| Information ||\n|:------|:-----|\n| Timestamp | 2026-11-09, 21:41:07 |\n| Event ID | `DbRepair` |\n| Actor | `NeuroLedger` |\n| Object | `Contracts` |\n| Module | `Waher.Persistence.Files` |\n| Facility | |\n");
		Add(ops, "alpha@lab.local", 12,
			"Legal identity application received: [`2f9a1c04-57e1-4c1e@legal.lab.local`](https://lab.local/ValidateLegalId.md?ID=2f9a1c04-57e1-4c1e%40legal.lab.local)\n\n" +
			"Other identities registered for the same account:\n\n* [`0b11e2a9@legal.lab.local`](https://lab.local/ValidateLegalId.md?ID=0b11e2a9) (Obsoleted)\n");
		Add(ops, "alpha@lab.local", 34,
			"Account requested.\n\n| Account request ||\n|:------|:-------|\n| Name: | Kim Lindqvist |\n| e-Mail: | <a href=\"mailto:kim@example.com\">kim@example.com</a> |\n| Company: | Example Logistics AB |\n| Message: | We want to connect 40 sensors in the Gothenburg warehouse. |\n");
		Add(admin, "gamma@prod.example", 58, "Unable to upload backup to `backup@vault.prod.example`. Will retry in 15 minutes.");
		Add(admin, "beta@prod.example", 95, "The approved Legal Identity for the gateway **expires in 12 days**.");
		Add(ops, "alpha@lab.local", 140, "Presence subscription request received from **sensor-hub-7@lab\\.local**. You can accept or decline the request from the roster configuration in the Administration portal.");
		Add(ops, "alpha@lab.local", 180,
			"XMPP Account created:\n\n| Account Information ||\n|:-----|:------|\n| User Name: | [kim](https://lab.local/Account.md?UserName=kim) |\n| e-Mail: | kim@example\\.com |\n",
			Engine.Attention.AttentionState.Read);
		Add(admin, "gamma@prod.example", 300,
			"New version of installed software has been downloaded\n==========================================================\n\n| Package ||\n|:------|:-------|\n| Filename: | `TAG.Neuron.3.11.2.package` |\n| Size: | 48.2 MB |\n\nThe new software will be automatically installed in 5 minutes, unless cancelled from the chat interface.");
		Add(admin, "gamma@prod.example", 320, "XMPP server at [`gamma.prod.example`](http://gamma.prod.example/) updated and restarted at 2026-11-09, 16:40:12. [Release Notes](https://lab.tagroot.io/ReleaseNotes)",
			Engine.Attention.AttentionState.Read);

		MainWindowViewModel vm = new(manager, new FleetService(monitors));
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Attention;
		vm.Inbox.ShowDone = all;
		vm.Attention.Refresh();
		vm.Inbox.Refresh();
		vm.Inbox.SelectedRow = vm.Inbox.Rows[all ? 2 : 0];
		vm.Inbox.Refresh();

		MainWindow window = new() { DataContext = vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(300);
		Capture(window, $"attention-{(all ? "all-" : "")}{theme.ToLowerInvariant()}-{width}");

		Assert.Equal(6, vm.Attention.OpenCount);
		Assert.Equal(all ? 9 : 6, vm.Attention.Items.Count);
		Assert.Equal(all ? 9 : 6, vm.Inbox.Rows.Count);
		Assert.Equal(6, vm.Inbox.WaitingCount);
		Assert.True(vm.Inbox.ShowsNotification);
		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light", 1440, 900, false)]
	[InlineData("Dark", 1440, 900, false)]
	[InlineData("Light", 1440, 900, true)]
	[InlineData("Light", 1000, 700, true)]
	public async Task RendersSourcesWorkspace(string theme, int width, int height, bool form)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		MonitorService monitors = new(manager, new EventHub());
		MainWindowViewModel vm = new(manager, new FleetService(monitors));
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Sources;

		SourcesViewModel s = vm.Sources;
		Dispatcher.UIThread.RunJobs();
		s.Neurons.Add(new NeuronChoice(Guid.NewGuid(), "neuron@lab.local", "neuron@lab.local", "ops@lab.local", 0));
		s.SelectedNeuron = s.Neurons[0];
		s.TreeError = null;
		DateTime t = new(2026, 11, 9, 19, 40, 46);

		static Waher.Networking.XMPP.Concentrator.DataSourceReference Src(string id, string name) => new(id, name, false, DateTime.Now);
		static Waher.Networking.XMPP.Concentrator.NodeInformation Node(string id, string src, string type, string display, bool children, bool cmds,
			Waher.Things.NodeState state = Waher.Things.NodeState.None, Waher.Things.DisplayableParameters.Parameter[]? p = null,
			Waher.Things.DisplayableParameters.Message[]? m = null)
			=> new(id, src, string.Empty, type, display, state, id, string.Empty, children, true, false, false, cmds, false, "Root", string.Empty, new DateTime(2026, 11, 9, 19, 40, 46), p ?? [], m ?? []);

		foreach ((string id, string name) in new[] { ("Connections", "Connections"), ("Groups", "Groups"), ("MeteringTopology", "Metering Topology"), ("Reports", "Reports"), ("ProgramData", "Program Data") })
			s.Roots.Add(new SourceItemViewModel(s, null, Src(id, name)));

		SourceItemViewModel metering = (SourceItemViewModel)s.Roots[2];
		NodeItemViewModel root = new(s, metering, Node("Root", "MeteringTopology", "Waher.Things.Metering.NodeTypes.Root", "Root", true, true));
		metering.SetChildren([root]);
		metering.IsExpanded = true;
		NodeItemViewModel web = new(s, root, Node("Local Web Server", "MeteringTopology", "Waher.Things.Http.LocalWebServerNode", "Local Web Server", false, true,
			Waher.Things.NodeState.WarningUnsigned,
			[
				new Waher.Things.DisplayableParameters.StringParameter("NodeId", "Node ID", "Local Web Server"),
				new Waher.Things.DisplayableParameters.StringParameter("Type", "Type", "Local Web Server"),
				new Waher.Things.DisplayableParameters.StringParameter("ParentId", "Parent ID", "Root"),
				new Waher.Things.DisplayableParameters.DateTimeParameter("Created", "Created", t),
				new Waher.Things.DisplayableParameters.Int32Parameter("Requests", "Requests last hour", 1284),
			],
			[new Waher.Things.DisplayableParameters.Message(t.AddMinutes(12), Waher.Things.DisplayableParameters.MessageType.Warning, "CertExpiry", "TLS certificate expires in 9 days.")]));
		root.SetChildren([web,
			new NodeItemViewModel(s, root, Node("Gothenburg warehouse", "MeteringTopology", "Waher.Things.Metering.NodeTypes.NodeCollection", "Node Collection", true, true)),
			new NodeItemViewModel(s, root, Node("MQTT bridge", "MeteringTopology", "Waher.Things.Mqtt.MqttBrokerNode", "MQTT Broker", true, true, Waher.Things.NodeState.ErrorUnsigned))]);
		root.IsExpanded = true;

		s.SelectedItem = web;
		foreach ((string cmd, string name, Waher.Things.CommandType type) in new[] { ("ClearMessages", "Erase messages", Waher.Things.CommandType.Simple), ("LogMessage", "Log message...", Waher.Things.CommandType.Parametrized), ("Stats", "Statistics...", Waher.Things.CommandType.Query) })
			s.Commands.Add(new CommandRow(new Waher.Networking.XMPP.Concentrator.NodeCommand(cmd, name, type, "", "", "", "Node", ""), name.TrimEnd('.'), type.ToString(), "Node"));
		s.HasCommands = true;

		if (form)
		{
			s.Form = Waher.Networking.XMPP.DataForms.DataForm.Parse(null!,
				"<x xmlns='jabber:x:data' type='form'><title>Local Web Server</title>" +
				"<field var='NodeId' type='text-single' label='ID'><desc>Identity of the node within its data source.</desc><required/><value>Local Web Server</value></field>" +
				"<field var='Disabled' type='boolean' label='Disabled'><desc>If checked, the node does not answer requests.</desc><value>false</value></field>" +
				"<field var='Port' type='text-single' label='Port'><validate xmlns='http://jabber.org/protocol/xdata-validate' datatype='xs:int'><range min='1' max='65535'/></validate><value>8080</value></field>" +
				"<field var='Mode' type='list-single' label='Protocol'><value>HTTPS</value><option label='HTTP'><value>HTTP</value></option><option label='HTTPS'><value>HTTPS</value></option></field>" +
				"<field var='Hosts' type='text-multi' label='Host names'><value>lab.local</value><value>www.lab.local</value></field>" +
				"</x>", null!, null!, "neuron@lab.local/x", "ops@lab.local/y");
			await s.Form["Port"].SetValue("80800");
			s.FormTitle = "Edit Local Web Server";
			s.FormSubtitle = "Local Web Server";
			s.FormError = "The Neuron did not accept some values. See the remarks under the fields.";
			s.Mode = InspectorMode.Form;
		}

		MainWindow window = new() { DataContext = vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(200);
		Capture(window, $"sources-{(form ? "form-" : "")}{theme.ToLowerInvariant()}-{width}");
		window.Close();
	}

	[AvaloniaTheory]
	[InlineData("Light", 1440)]
	[InlineData("Dark", 1440)]
	[InlineData("Light", 1000)]
	public async Task RendersConsoleFanOut(string theme, int width)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		EngineRuntime.Initialize();
		string store = Path.Combine(Path.GetTempPath(), "Nat.Render." + Guid.NewGuid().ToString("N"), "accounts.json");
		AccountManager manager = new(new JsonAccountStore(store), new InMemoryCredentialVault());
		AccountSession ops = await manager.AddAsync(new AccountProfile { Host = "lab.local", UserName = "ops", ConnectOnStartup = false }, AccountSecret.FromPassword("x"), connect: false);
		MonitorService monitors = new(manager, new EventHub());
		MainWindowViewModel vm = new(manager, new FleetService(monitors));
		await vm.StartAsync();
		vm.Workspace = WorkspaceKind.Console;
		Dispatcher.UIThread.RunJobs();

		ConsoleViewModel console = vm.Console;
		string[] neurons = ["alpha@lab.local", "beta@lab.local", "gamma@lab.local"];
		foreach (string n in neurons)
			console.Targets.Add(new ConsoleTargetViewModel(ops.Profile.Id, n) { Label = n, AccountLabel = "ops@lab.local", IsOnline = true, IsChecked = true });
		console.Targets.Add(new ConsoleTargetViewModel(ops.Profile.Id, "delta@prod.example") { Label = "delta@prod.example", AccountLabel = "ops@lab.local", IsOnline = false });

		ConsoleRunViewModel Run(string command, Func<int, string?> answer)
		{
			ConsoleRunViewModel run = new(command, DateTime.Now);
			for (int i = 0; i < neurons.Length; i++)
			{
				Engine.Chat.ConsoleEntry e = new("t" + i, command, "b", false);
				if (answer(i) is string md)
					e.Add(new Engine.Chat.ConsoleResponse("m" + i, DateTime.Now, md));
				ConsoleResultViewModel r = new(e, neurons[i]);
				r.Refresh();
				run.Results.Add(r);
			}
			console.Runs.Add(run);
			return run;
		}

		Run("NrClients", i => $"<font class=\"result\">`{new[] { 1284, 977, 46 }[i]}`</font>");
		Run("Versions", i => i == 2 ? null :
			"| Assembly | Version |\n|:---|:---|\n| `Waher.IoTGateway` | `3.11.1.0` |\n| `Waher.Service.IoTBroker` | `" + (i == 0 ? "3.11.1.0" : "3.10.4.0") + "` |\n| `Waher.Networking.XMPP` | `4.4.0.0` |");
		console.HasRuns = true;
		console.CommandText = "Reboot";
		await console.RunCommand.ExecuteAsync(null);

		MainWindow window = new() { DataContext = vm, Width = width, Height = 800 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		await Task.Delay(300);
		Capture(window, $"console-fanout-{theme.ToLowerInvariant()}-{width}");

		Assert.True(console.IsConfirming);
		window.Close();
	}

	[AvaloniaFact]
	public async Task RendersAddForm()
	{
		Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
		MainWindowViewModel vm = await SampleViewModel();
		vm.IsAddFormOpen = true;
		vm.NewUserName = "operator";
		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();
		Capture(window, "shell-accounts-addform-light-1440");
		window.Close();
	}
}
