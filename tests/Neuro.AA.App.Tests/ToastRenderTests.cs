using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Neuro.AA.App.Watch;
using Neuro.AA.Engine.Alerts;
using Neuro.AA.Engine.Events;

namespace Neuro.AA.App.Tests;

/// <summary>Renders the corner alerts for design review.</summary>
public class ToastRenderTests
{
	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public void RendersAlerts(string theme)
	{
		Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

		// Synthetic sample data.
		Alert[] alerts =
		[
			new() { Id = "1", Kind = AlertKind.Error, Severity = EventSeverity.Error, Title = "New: Unable to upload backup to backup@vault.prod.example", Detail = "Production", At = DateTime.Now },
			new() { Id = "2", Kind = AlertKind.Notification, Severity = EventSeverity.Info, Title = "Legal identity application received", Detail = "Lab · needs you", At = DateTime.Now },
			new() { Id = "3", Kind = AlertKind.NeuronDown, Severity = EventSeverity.Error, Title = "Gothenburg edge is not answering", Detail = "Its event stream stopped.", At = DateTime.Now },
		];

		int i = 0;
		foreach (Alert a in alerts)
		{
			ToastWindow t = new(a, a.Id == "3" ? 4 : 0, () => { });
			t.Show();
			Dispatcher.UIThread.RunJobs();
			var frame = t.CaptureRenderedFrame();
			Assert.NotNull(frame);
			Assert.True(t.Bounds.Height is > 60 and < 200, $"Height {t.Bounds.Height}");

			DirectoryInfo? d = new(AppContext.BaseDirectory);
			while (d is not null && !File.Exists(Path.Combine(d.FullName, "Neuro.AA.slnx")))
				d = d.Parent;
			string folder = Path.Combine(d?.FullName ?? Path.GetTempPath(), ".impeccable", "review");
			Directory.CreateDirectory(folder);
			frame!.Save(Path.Combine(folder, $"toast-{++i}-{theme.ToLowerInvariant()}.png"));
			t.Close();
		}

		Assert.NotNull(AppIcon.Get(attention: true));
	}
}
