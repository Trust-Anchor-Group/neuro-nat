using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;
using Neuro.Nat.App.Watch;
using Neuro.Nat.Engine.Alerts;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.App.Tests;

/// <summary>Renders the desktop pop-ups for design review.</summary>
public class ToastRenderTests
{
	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public void RendersAlerts(string theme)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		// Synthetic sample data.
		NoticeViewModel[] notices =
		[
			new(new Alert { Id = "1", Kind = AlertKind.Error, Severity = EventSeverity.Error, Title = "New: Unable to upload backup to backup@vault.prod.example", Detail = "Production", At = DateTime.Now }),
			new(new Alert { Id = "2", Kind = AlertKind.Notification, Severity = EventSeverity.Info, Title = "Legal identity application received", Detail = "Lab is waiting for you", At = DateTime.Now }),
			new(new Alert { Id = "3", Kind = AlertKind.NeuronDown, Severity = EventSeverity.Error, Title = "Gothenburg edge is not answering", Detail = "Its event stream stopped.", At = DateTime.Now }),
			new(new Alert { Id = "4", Kind = AlertKind.Message, Title = "Kim Lindqvist", Detail = "Did the backup on gamma finish? I see the 02:00 one failed and nothing after it.", At = DateTime.Now })
			{
				Reply = _ => Task.FromResult<string?>(null),
				Mute = _ => { },
			},
			new(new Alert { Id = "5", Kind = AlertKind.ContactRequest, Severity = EventSeverity.Warning, Title = "sam@partner.example wants to be a contact", Detail = "Asks ops@lab.local", At = DateTime.Now })
			{
				PrimaryText = "Accept",
				Primary = () => Task.CompletedTask,
				SecondaryText = "Decline",
				Secondary = () => Task.CompletedTask,
			},
		];
		notices[3].Absorb(notices[3].Alert);

		int i = 0;
		foreach (NoticeViewModel n in notices)
		{
			ToastWindow t = new(n);
			t.Show();
			Dispatcher.UIThread.RunJobs();
			var frame = t.CaptureRenderedFrame();
			Assert.NotNull(frame);
			Assert.True(t.Bounds.Height is > 60 and < 240, $"Height {t.Bounds.Height}");

			DirectoryInfo? d = new(AppContext.BaseDirectory);
			while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.Nat.slnx")))
				d = d.Parent;
			string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
			Directory.CreateDirectory(folder);
			frame!.Save(Path.Combine(folder, $"toast-{++i}-{theme.ToLowerInvariant()}.png"));
			t.Close();
			Assert.True(n.IsClosed);
		}

		Assert.NotNull(AppIcon.Get(IconState.NeedsYou));
		Assert.NotNull(AppIcon.Get(IconState.Down, tray: true));
	}
}
