using System.Globalization;

namespace Neuro.Nat.Features.Telemetry.Data;

/// <summary>Readable names for the catalog's fixed values and dimensions, and dates as they are shown.</summary>
public static class Words
{
	private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
	{
		["ReadSucceeded"] = "NFC read worked",
		["ReadFailed"] = "NFC read failed",
		["ErrorBeforeRead"] = "Stopped before the read",
		["SwitchedMethod"] = "Switched method",
		["Abandoned"] = "Gave up",
		["NoReadResult"] = "No read result",
		["SubmissionFailed"] = "Sending failed",
		["NotSubmitted"] = "Not sent",
		["NoFurtherActivity"] = "Nothing more happened",
		["NfcSelected"] = "NFC chosen",
		["MrzAvailable"] = "Document text read",
		["NfcInstructionsShown"] = "Instructions shown",
		["NfcScanStarted"] = "Scan started",
		["NfcConnected"] = "Chip connected",
		["ReadStarted"] = "Read started",
		["ReadCompleted"] = "Read done",
		["SubmissionRequested"] = "Sending asked",
		["SubmissionAcknowledged"] = "Sent",
		["ResultReceived"] = "Decision received",
		["Approved"] = "Approved",
		["Rejected"] = "Rejected",
		["Undecided"] = "Undecided",
		["ManualReview"] = "Manual review",
		["PeerReview"] = "Peer review",
	};

	private static readonly Dictionary<string, string> Dimensions = new(StringComparer.Ordinal)
	{
		["neuron"] = "Neuron",
		["method"] = "Method",
		["nationality"] = "Nationality",
		["platform"] = "Platform",
		["appVersion"] = "App version",
		["outcome"] = "How it ended",
		["applicationOutcome"] = "Application outcome",
		["rejectionReason"] = "Rejection reason",
		["failureCode"] = "Failure code",
		["readDuration"] = "Read time",
		["step"] = "Step",
		["stopReason"] = "Why it stopped",
		["chipResult"] = "Chip result",
		["lastChipFile"] = "Last chip file",
		["failureStage"] = "Failure stage",
	};

	/// <summary>The name of a dimension's value: a readable one for the known ones, else as the app sent it.</summary>
	public static string Value(string? v) => string.IsNullOrEmpty(v) ? "Unknown" : Values.TryGetValue(v, out string? n) ? n : v;

	/// <summary>The name of a value of a dimension. App versions read "App 2.5.1".</summary>
	public static string Value(string dimension, string? v) => dimension == "appVersion" && !string.IsNullOrEmpty(v) && v != "Unknown" ? "App " + v : Value(v);

	public static string Dimension(string d) => Dimensions.TryGetValue(d, out string? n) ? n : d;

	/// <summary>"4 Nov"</summary>
	public static string Day(DateTime d) => d.ToString("d MMM", CultureInfo.InvariantCulture);

	/// <summary>"Wed 4 Nov"</summary>
	public static string LongDay(DateTime d) => d.ToString("ddd d MMM", CultureInfo.InvariantCulture);

	/// <summary>"4 Nov 2026"</summary>
	public static string FullDay(DateTime d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

	/// <summary>"just now", "12 min ago", "3 h ago", "2 days ago"</summary>
	public static string Ago(DateTime? utc, DateTime utcNow)
	{
		if (utc is not DateTime t)
			return "Never";

		TimeSpan d = utcNow - t.ToUniversalTime();
		return d.TotalMinutes < 1 ? "Just now"
			: d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
			: d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
			: (int)d.TotalDays == 1 ? "1 day ago" : $"{(int)d.TotalDays} days ago";
	}

	public static string N(double v) => v.ToString("N0", CultureInfo.InvariantCulture);

	public static string Pct(double v) => v.ToString("0.0", CultureInfo.InvariantCulture) + "%";

	public static string Pts(double v) => v.ToString("0.0", CultureInfo.InvariantCulture) + (Math.Abs(v - 1) < 0.05 ? " point" : " points");
}
