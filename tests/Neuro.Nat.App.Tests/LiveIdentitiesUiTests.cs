using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Views;
using Neuro.Nat.Engine;
using Neuro.Nat.Engine.Accounts;
using Neuro.Nat.Engine.Chat;
using Neuro.Nat.Engine.Events;
using Neuro.Nat.Engine.Identity;
using Waher.Security;

namespace Neuro.Nat.App.Tests;

/// <summary>
/// The Identities workspace against the local test Neuron: a coworker applies for a legal identity
/// through the form, the operator sees it waiting under Neurons, and the application is retired
/// again. Saves frames for review. Needs NAT_TEST_* (with USER2) and NAT_TEST_NEURON.
/// </summary>
[Collection("Live Neuron")]
public class LiveIdentitiesUiTests
{
	private static async Task Until(Func<bool> condition, int timeoutMs = 30000)
	{
		long end = Environment.TickCount64 + timeoutMs;
		while (!condition())
		{
			if (Environment.TickCount64 > end)
				throw new TimeoutException("Condition not met in time.");
			Dispatcher.UIThread.RunJobs();
			await Task.Delay(50);
		}
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public async Task ApplyReviewAndRetire(string theme)
	{
		string? host = Environment.GetEnvironmentVariable("NAT_TEST_HOST");
		string? user = Environment.GetEnvironmentVariable("NAT_TEST_USER");
		string? password = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD");
		string? user2 = Environment.GetEnvironmentVariable("NAT_TEST_USER2");
		string? password2 = Environment.GetEnvironmentVariable("NAT_TEST_PASSWORD2");
		string? neuron = Environment.GetEnvironmentVariable("NAT_TEST_NEURON");
		if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(user2) || string.IsNullOrEmpty(password2) || string.IsNullOrEmpty(neuron))
			Live.Skip("NAT_TEST_HOST", "NAT_TEST_USER", "NAT_TEST_PASSWORD", "NAT_TEST_USER2", "NAT_TEST_PASSWORD2", "NAT_TEST_NEURON");

		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		EngineRuntime.Initialize();
		string dir = Path.Combine(Path.GetTempPath(), "Nat.LiveUi." + Guid.NewGuid().ToString("N"));
		await using AccountManager manager = new(new JsonAccountStore(Path.Combine(dir, "accounts.json")), new InMemoryCredentialVault());
		AccountSession op = await manager.AddAsync(new AccountProfile { Host = host, UserName = user, TrustServerCertificate = true, ConnectOnStartup = false, ColorIndex = 0 },
			AccountSecret.FromPassword(password), connect: false);
		AccountSession kim = await manager.AddAsync(new AccountProfile { Host = host, UserName = user2, TrustServerCertificate = true, ConnectOnStartup = false, ColorIndex = 3 },
			AccountSecret.FromPassword(password2), connect: false);
		await using MonitorService monitors = new(manager, new EventHub());
		await using ChatService chat = new(manager, monitors);
		await using IdentityService ids = new(manager, chat);
		MainWindowViewModel vm = new(manager, new FleetService(monitors), chat, ids);
		await vm.StartAsync();
		await op.ConnectAsync();
		await kim.ConnectAsync();
		await Until(() => ids.IsAvailable(kim.Profile.Id) && ids.IsAvailable(op.Profile.Id), 40000);

		MainWindow window = new() { DataContext = vm, Width = 1440, Height = 900 };
		window.Show();
		vm.ShowIdentitiesCommand.Execute(null);
		IdentitiesViewModel v = vm.Identities;

		// Apply through the form.
		v.Selected = v.Rows.First(r => r.Kind == IdentityRowKind.Account && r.AccountId == kim.Profile.Id);
		Assert.True(v.CanApply, v.DetailNote);
		void Set(string name, string value) => v.ApplyFields.First(f => f.Name == name).Value = value;
		Set("FIRST", "Kim");
		Set("LAST", "Lindqvist");
		Set("CITY", "Gothenburg");
		Set("COUNTRY", "se");
		Set("ORGNAME", "Neuro Lab");
		Set("ORGROLE", "Operator");
		await Task.Delay(200);
		Save(window, $"identities-apply-{theme.ToLowerInvariant()}");

		await v.ApplyCommand.ExecuteAsync(null);
		Assert.True(v.DetailError is null, v.DetailError);
		await Until(() => v.ShowsIdentity && v.Selected?.Identity?.State == IdentityState.Created);
		string appliedId = v.Selected!.Identity!.Id;
		Assert.Contains(v.Properties, p => p.Name == "Country" && p.Value == "SE");

		await v.ValidateCommand.ExecuteAsync(null);
		Assert.NotNull(v.Validation);
		await Task.Delay(300);
		Save(window, $"identities-mine-{theme.ToLowerInvariant()}");

		// The operator sees it waiting on the Neuron.
		await Until(() => monitors.For(op.Profile.Id)?.Concentrator(neuron) is not null, 30000);
		v.SetModeCommand.Execute(IdentitiesMode.Neurons);
		await Until(() => v.Rows.Any(r => r.Kind == IdentityRowKind.Neuron && r.AccountId == op.Profile.Id));
		v.Selected = v.Rows.First(r => r.Kind == IdentityRowKind.Neuron && r.AccountId == op.Profile.Id);
		await Until(() => v.PendingRows.Any(p => p.Model.Id == appliedId), 30000);
		PendingApplicationRow row = v.PendingRows.First(p => p.Model.Id == appliedId);
		Assert.Equal("Kim Lindqvist", row.Name);
		Assert.Contains("/LegalIdentity.md?Id=", row.ReviewUrl);
		await Task.Delay(300);
		Save(window, $"identities-neurons-{theme.ToLowerInvariant()}");

		// Retire it again, through the confirmation.
		v.SetModeCommand.Execute(IdentitiesMode.Mine);
		await Until(() => v.Rows.Any(r => r.Identity?.Id == appliedId));
		v.Selected = v.Rows.First(r => r.Identity?.Id == appliedId);
		v.AskRetireCommand.Execute("obsolete");
		await Task.Delay(200);
		Save(window, $"identities-retire-{theme.ToLowerInvariant()}");
		await v.ConfirmCommand.ExecuteAsync(null);
		Assert.True(v.DetailError is null, v.DetailError);

		// Retired identities are folded away until asked for.
		await Until(() => !v.Rows.Any(r => r.Identity?.Id == appliedId));
		v.ShowRetired = true;
		await Until(() => v.Rows.Any(r => r.Identity?.Id == appliedId && r.Identity.State is IdentityState.Rejected or IdentityState.Obsoleted));

		window.Close();
	}

	private static void Save(MainWindow window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);

		DirectoryInfo? d = new(AppContext.BaseDirectory);
		while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
			d = d.Parent;
		string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
		Directory.CreateDirectory(folder);
		frame!.Save(Path.Combine(folder, name + ".png"));
	}
}
