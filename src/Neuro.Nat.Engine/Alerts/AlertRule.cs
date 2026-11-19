using Neuro.Nat.Engine.Attention;
using Neuro.Nat.Engine.Events;

namespace Neuro.Nat.Engine.Alerts;

public enum AlertRuleMode { Mute, Hide, Track }

/// <summary>A fleet-wide preference for recurrences, shared by forwarded notifications and the event stream.</summary>
public sealed record AlertRule(string Key, string Title, AlertRuleMode Mode, DateTimeOffset? Until = null)
{
	[System.Text.Json.Serialization.JsonIgnore]
	public string Description => this.Mode switch
	{
		AlertRuleMode.Track => "Tracking recurrences across all Neurons",
		AlertRuleMode.Mute => "Muted across all Neurons",
		_ when this.Until is DateTimeOffset end => $"Muted and hidden until {end.LocalDateTime:g}",
		_ => "Muted and hidden across all Neurons",
	};

	public static string KeyFor(NeuronNotification n)
		=> KeyFor(n.Kind == NotificationKind.Alert ? "event" : n.Kind.ToString(), n.EventId, n.Title);

	public static string KeyFor(NeuronEvent e) => KeyFor("event", e.EventId, e.Title);

	private static string KeyFor(string kind, string? id, string title) => string.IsNullOrWhiteSpace(id)
		? kind + ":title:" + EventFingerprint.NormalizeMessage(title).ToUpperInvariant()
		: kind + ":id:" + id.Trim().ToUpperInvariant();
}
