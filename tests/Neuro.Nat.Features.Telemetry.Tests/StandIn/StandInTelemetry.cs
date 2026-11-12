using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Neuro.Nat.Features.Telemetry.Tests.StandIn;

/// <summary>
/// A stand-in for the telemetry API of a root Neuron and the two Neurons it collects from, with
/// made-up but realistic numbers: about 300 applications and 400 NFC attempts a day, an app
/// release (2.5.1) that reads worse on Android, the Android read rate falling over the last weeks,
/// and one Neuron that stopped sending three days ago. It answers /Catalog, /Query and /Status in
/// the exact shapes of docs/API.md (Neuro.App.Telemetry), with ETags and 304, and as the team or
/// as a partner (Custom.Telemetry.External) sees it. Ported from the package's example dashboard.
/// </summary>
public sealed class StandInTelemetry
{
	private const int MinimumDenominator = 5;

	private readonly JsonObject catalog;
	private readonly Dictionary<string, JsonObject> metricByKey = [];
	private readonly Dictionary<(string, DateTime), Dictionary<string, Dictionary<string, double>>> dayCache = [];

	public StandInTelemetry(DateTime utcNow, string[]? neurons = null)
	{
		this.Today = utcNow.Date;
		this.Neurons = neurons ?? ["lab.tagroot.io", "gbg.lab.tagroot.io", "sto.lab.tagroot.io"];
		string file = Path.Combine(AppContext.BaseDirectory, "StandIn", "Catalog.json");
		this.catalog = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
		foreach (JsonNode? m in this.catalog["metrics"]!.AsArray())
			this.metricByKey[(string)m!["key"]!] = m.AsObject();
	}

	public DateTime Today { get; }

	public string[] Neurons { get; }

	/// <summary>True: the account sees everything. False: a partner, with only Custom.Telemetry.External.</summary>
	public bool Internal { get; set; } = true;

	/// <summary>The Neuron that stopped sending numbers, or null for none.</summary>
	public int? Stopped { get; set; } = 2;

	/// <summary>Scales every count, to make small numbers (the local test Neuron) or none.</summary>
	public double Scale { get; set; } = 1;

	/// <summary>Requests answered, by path without the query.</summary>
	public List<string> Asked { get; } = [];

	/// <summary>Requests answered 304.</summary>
	public int NotModified { get; private set; }

	private bool CanSeeMetric(string key) => this.Internal || (string?)this.metricByKey.GetValueOrDefault(key)?["audience"] == "external";

	private bool CanUseDimension(string name)
		=> this.catalog["dimensions"]![name] is JsonNode d && (this.Internal || (string?)d["audience"] == "external");

	// ----- HTTP -----

	/// <summary>Answers a request as the Neuron's web server would.</summary>
	public HttpResponseMessage Answer(HttpRequestMessage request)
	{
		string path = request.RequestUri!.AbsolutePath;
		const string api = "/NeuroAppTelemetry/Api/v1";
		lock (this.Asked)
			this.Asked.Add(request.RequestUri.PathAndQuery);

		if (!path.StartsWith(api, StringComparison.Ordinal))
			return Json(HttpStatusCode.NotFound, "{}");
		if (request.Headers.Authorization?.Scheme != "Bearer")
			return new HttpResponseMessage(HttpStatusCode.Unauthorized);

		(HttpStatusCode code, string body) = path[api.Length..] switch
		{
			"/Catalog" => (HttpStatusCode.OK, this.Catalog()),
			"/Status" => (HttpStatusCode.OK, this.Status()),
			"/Query" => this.Query(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)),
			_ => (HttpStatusCode.NotFound, Error("unknownEndpoint", "Use /Catalog, /Query or /Status.")),
		};

		HttpResponseMessage response;
		if (code == HttpStatusCode.OK)
		{
			string etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16] + "\"";
			if (request.Headers.IfNoneMatch.Any(t => t.Tag == etag))
			{
				lock (this.Asked)
					this.NotModified++;
				response = new HttpResponseMessage(HttpStatusCode.NotModified);
			}
			else
				response = Json(code, body);
			response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
			return response;
		}

		return Json(code, body);
	}

	private static HttpResponseMessage Json(HttpStatusCode code, string body)
		=> new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static string Error(string code, string message) => new JsonObject { ["error"] = code, ["message"] = message }.ToJsonString();

	// ----- /Catalog -----

	public string Catalog()
	{
		JsonObject dims = [];
		foreach ((string name, JsonNode? d) in this.catalog["dimensions"]!.AsObject())
		{
			if (this.CanUseDimension(name))
				dims[name] = d!.DeepClone();
		}

		JsonArray metrics = [];
		foreach (JsonObject m in this.metricByKey.Values.Where(m => this.CanSeeMetric((string)m["key"]!)))
		{
			JsonObject c = m.DeepClone().AsObject();
			c["dimensions"] = new JsonArray([.. m["dimensions"]!.AsArray().Select(x => (string)x!).Where(this.CanUseDimension).Select(x => (JsonNode)x)]);
			metrics.Add(c);
		}

		return new JsonObject
		{
			["metricsVersion"] = (int)this.catalog["metricsVersion"]!,
			["access"] = this.Internal ? "internal" : "external",
			["minimumDenominator"] = MinimumDenominator,
			["dimensions"] = dims,
			["metrics"] = metrics,
		}.ToJsonString();
	}

	// ----- /Status -----

	public string Status()
	{
		DateTime yesterday = this.Today.AddDays(-1);
		JsonArray list =
		[
			new JsonObject
			{
				["neuron"] = this.Neurons[0], ["role"] = "self", ["metricsVersion"] = (int)this.catalog["metricsVersion"]!,
				["lastComputedDay"] = Day(yesterday), ["lastCompute"] = this.Today.AddHours(2).AddMinutes(10).ToString("o", CultureInfo.InvariantCulture),
				["lastComputeError"] = null, ["computing"] = false, ["lastCollect"] = null, ["lastCollectSuccess"] = null, ["lastCollectError"] = null, ["lastCollectFacts"] = null,
			},
		];

		if (this.Internal)
		{
			for (int i = 1; i < this.Neurons.Length; i++)
			{
				bool stopped = this.Stopped == i;
				list.Add(new JsonObject
				{
					["neuron"] = this.Neurons[i], ["role"] = "source", ["metricsVersion"] = (int)this.catalog["metricsVersion"]!,
					["lastComputedDay"] = Day(stopped ? this.Today.AddDays(-4) : yesterday), ["lastCompute"] = null, ["lastComputeError"] = null, ["computing"] = null,
					["lastCollect"] = this.Today.AddHours(2).ToString("o", CultureInfo.InvariantCulture),
					["lastCollectSuccess"] = (stopped ? this.Today.AddDays(-3).AddHours(2) : this.Today.AddHours(2)).ToString("o", CultureInfo.InvariantCulture),
					["lastCollectError"] = stopped ? "Timeout while contacting the Neuron." : null,
					["lastCollectFacts"] = 1200 + i * 300,
				});
			}
		}

		return new JsonObject { ["neurons"] = list }.ToJsonString();
	}

	// ----- The made-up facts -----

	private static double Noise(double seed)
	{
		double x = Math.Sin(seed * 12.9898) * 43758.5453;
		return x - Math.Floor(x);
	}

	/// <summary>Facts for one Neuron and day: metric to { "": total, "platform=Android": n, ... }.</summary>
	private Dictionary<string, Dictionary<string, double>> DayFacts(string neuron, DateTime t)
	{
		lock (this.dayCache)
		{
			if (this.dayCache.TryGetValue((neuron, t), out var cached))
				return cached;
		}

		int n = Array.IndexOf(this.Neurons, neuron);
		double d = (t - new DateTime(1970, 1, 1)).TotalDays;
		bool weekend = t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
		double scale = this.Scale * new[] { 0.15, 1, 0.7 }[Math.Min(n, 2)] * (weekend ? 0.6 : 1) * (1 + 0.15 * Math.Sin(d / 9));
		double R(double i) => Noise(d * 31 + n * 7 + i);
		Dictionary<string, Dictionary<string, double>> facts = [];

		void Add(string metric, string? dim, double value)
		{
			if (!facts.TryGetValue(metric, out var f))
				facts[metric] = f = new Dictionary<string, double> { [""] = 0 };
			if (dim is null)
				f[""] += value;
			else
				f[dim] = f.GetValueOrDefault(dim) + value;
		}

		List<(string, double)> Split(double value, (string, double)[] shares)
		{
			double left = value;
			List<(string, double)> list = [];
			for (int i = 0; i < shares.Length; i++)
			{
				double v = i == shares.Length - 1 ? left : Math.Min(left, Math.Round(value * shares[i].Item2));
				left -= v;
				list.Add((shares[i].Item1, v));
			}

			return list;
		}

		void Count(string metric, double value, Dictionary<string, (string, double)[]> splits)
		{
			Add(metric, null, value);
			foreach ((string dimension, (string, double)[] shares) in splits)
			{
				foreach ((string name, double v) in Split(value, shares))
					Add(metric, dimension + "=" + name, v);
			}
		}

		double age = (this.Today - t).TotalDays;
		double rollout = Math.Clamp((21 - age) / 14, 0, 1);
		double v241 = 0.12 * (1 - 0.4 * rollout), v251 = 0.75 * rollout;
		(string, double, double)[] versions = [("2.4.1", v241, -0.02), ("2.5.0", 1 - v241 - v251, 0), ("2.5.1", v251, 0)];
		(string, double)[] nationalities = [("SE", 0.55), ("NO", 0.15), ("FI", 0.12), ("DK", 0.1), ("Unknown", 0.08)];

		foreach ((string platform, double platformShare, double platformSuccess) in new[] { ("Android", 0.62, 0.87), ("iOS", 0.38, 0.92) })
		{
			foreach ((string version, double versionShare, double versionDelta) in versions)
			{
				double share = platformShare * versionShare;
				double success = platformSuccess + versionDelta + (version == "2.5.1" && platform == "Android" ? -0.08 : 0);
				if (share <= 0)
					continue;
				double attempts = Math.Round(240 * scale * share * (0.85 + 0.3 * R(1 + platformShare + 3 * versionShare)));
				double reads = Math.Round(attempts * 0.78);
				double ok = Math.Round(reads * (success + 0.05 * (R(3 + share) - 0.5)));
				double journeys = Math.Round(attempts * 0.8);
				double journeysOk = Math.Min(journeys, Math.Round(journeys * (success + 0.03)));
				Dictionary<string, double> outcomes = new()
				{
					["ReadSucceeded"] = ok, ["ReadFailed"] = reads - ok, ["ErrorBeforeRead"] = Math.Round(attempts * 0.06),
					["SwitchedMethod"] = Math.Round(attempts * 0.05), ["Abandoned"] = Math.Round(attempts * 0.08),
				};
				outcomes["NoReadResult"] = Math.Max(0, attempts - outcomes.Values.Sum());
				Dictionary<string, (string, double)[]> nfc() => new() { ["platform"] = [(platform, 1)], ["appVersion"] = [(version, 1)], ["nationality"] = nationalities };
				var withOutcome = nfc();
				withOutcome["outcome"] = [.. outcomes.Select(o => (o.Key, attempts > 0 ? o.Value / attempts : 0))];
				Count("Nfc.Attempts", attempts, withOutcome);
				Count("Nfc.Reads", reads, nfc());
				var succeeded = nfc();
				succeeded["readDuration"] = [("0-5s", 0.19), ("5-10s", 0.4), ("10-20s", 0.24), ("20-30s", 0.08), ("30-60s", 0.04), ("60s+", 0.01), ("Unknown", 0.04)];
				succeeded["applicationOutcome"] = [("Approved", 0.8), ("Rejected", 0.08), ("Undecided", 0.07), ("SubmissionFailed", 0.02), ("NotSubmitted", 0.03)];
				Count("Nfc.ReadsSucceeded", ok, succeeded);
				Count("Nfc.Journeys", journeys, nfc());
				Count("Nfc.JourneysSucceeded", journeysOk, nfc());

				double approved = Math.Round(ok * 0.8), rejected = Math.Round(ok * 0.08);
				var failed = nfc();
				failed["failureCode"] = [("TagLost", 0.45), ("AccessDenied", 0.25), ("Timeout", 0.2), ("Unknown", 0.1)];
				Count("Nfc.ReadsFailed", reads - ok, failed);
				var before = nfc();
				before["failureCode"] = [("CameraUnavailable", 0.4), ("MrzNotRead", 0.45), ("NfcDisabled", 0.15)];
				Count("Nfc.ErrorsBeforeRead", outcomes["ErrorBeforeRead"], before);
				Count("Nfc.AttemptsApproved", approved, nfc());
				Count("Nfc.AttemptsRejected", rejected, nfc());
				Count("Nfc.AttemptsDecided", approved + rejected, nfc());
				Count("Nfc.FailureDiagnostics", Math.Round((reads - ok) * 0.7), new()
				{
					["outcome"] = [("ReadFailed", 0.8), ("Abandoned", 0.2)], ["chipResult"] = [("BacFailed", 0.5), ("PaceFailed", 0.3), ("Unknown", 0.2)],
					["lastChipFile"] = [("DG1", 0.3), ("DG2", 0.5), ("SOD", 0.2)], ["platform"] = [(platform, 1)], ["appVersion"] = [(version, 1)],
				});

				(string, double)[] steps =
				[
					("NfcSelected", 1), ("MrzAvailable", 0.95), ("NfcInstructionsShown", 0.93), ("NfcScanStarted", 0.86), ("NfcConnected", 0.8),
					("ReadStarted", 0.79), ("ReadCompleted", attempts > 0 ? ok / attempts : 0), ("SubmissionRequested", attempts > 0 ? ok * 0.98 / attempts : 0),
					("SubmissionAcknowledged", attempts > 0 ? ok * 0.96 / attempts : 0), ("ResultReceived", attempts > 0 ? (approved + rejected) / attempts : 0),
					("Approved", attempts > 0 ? approved / attempts : 0),
				];
				double[] reached = [.. steps.Select(s => Math.Round(attempts * s.Item2))];
				(string, double)[] stopReasons = [("SwitchedMethod", 0.3), ("Abandoned", 0.35), ("ErrorBeforeRead", 0.2), ("NoFurtherActivity", 0.15)];
				for (int i = 0; i < steps.Length; i++)
				{
					string step = steps[i].Item1;
					Add("Nfc.StepsReached", "step=" + step, reached[i]);
					if (i + 1 < steps.Length)
					{
						Add("Nfc.StepsContinued", "step=" + step, reached[i + 1]);
						double stopped = reached[i] - reached[i + 1];
						(string, double)[] reasons = step == "ReadStarted" ? [("ReadFailed", 1)] : step == "SubmissionAcknowledged" ? [("Undecided", 1)]
							: step == "ResultReceived" ? [("Rejected", 1)] : i > 6 ? [("SubmissionFailed", 0.6), ("NoFurtherActivity", 0.4)] : stopReasons;
						Add("Nfc.StepsStopped", "step=" + step, stopped);
						foreach ((string reason, double v) in Split(stopped, reasons))
							Add("Nfc.StepsStopped", "stopReason=" + reason, v);
					}
				}
			}
		}

		double unreadable = Math.Round(6 * scale * R(11));
		Dictionary<string, (string, double)[]> unreadableSplits() => new() { ["failureCode"] = [("UnsupportedDocument", 0.7), ("Unknown", 0.3)], ["failureStage"] = [("Bac", 0.6), ("Pace", 0.4)] };
		Count("Nfc.UnreadableReports", unreadable, unreadableSplits());
		Count("Nfc.UnreadableReportsShared", Math.Round(unreadable * 0.6), unreadableSplits());
		Count("Nfc.UnreadableReportsSodUploaded", Math.Round(unreadable * 0.4), unreadableSplits());
		Count("Nfc.UnreadableReportsComUploaded", Math.Round(unreadable * 0.35), unreadableSplits());
		Add("DataQuality.InvalidEvents", null, R(12) > 0.8 ? Math.Ceiling(R(13) * 3) : 0);
		Add("DataQuality.EventsWithoutAccount", null, R(14) > 0.7 ? Math.Ceiling(R(15) * 4) : 0);
		Add("DataQuality.UnknownReadOutcomes", null, R(16) > 0.9 ? 1 : 0);

		foreach ((string method, double share) in new[] { ("NFC", 0.7), ("ManualReview", 0.2), ("PeerReview", 0.1) })
		{
			double submitted = Math.Round(180 * scale * share * (0.85 + 0.3 * R(5 + share)));
			double settled = Math.Min(1, age / 4);
			double approved = Math.Round(submitted * 0.74 * settled);
			double rejected = Math.Round(submitted * 0.14 * settled);
			Dictionary<string, (string, double)[]> app() => new() { ["method"] = [(method, 1)], ["nationality"] = nationalities };
			Count("Applications.Submitted", submitted, app());
			Count("Applications.Approved", approved, app());
			Count("Applications.Rejected", rejected, app());
			Count("Applications.Decided", approved + rejected, app());
			Count("Applications.Undecided", submitted - approved - rejected, app());

			(string, double)[] reasons = [("DocumentExpired", 0.3), ("PhotoMismatch", 0.25), ("ImageQuality", 0.2), ("Unknown", 0.25)];
			Count("Applications.RejectionReasons", rejected, new() { ["rejectionReason"] = reasons });
			if (method == "NFC")
				Count("Applications.NfcRejectionReasons", rejected, new() { ["rejectionReason"] = reasons });
			Count("Activity.Submissions", Math.Round(submitted * 1.04), new() { ["method"] = [(method, 1)] });
			Count("Activity.Approvals", Math.Round(submitted * 0.74), new() { ["method"] = [(method, 1)] });
			Count("Activity.Rejections", Math.Round(submitted * 0.14), new() { ["method"] = [(method, 1)] });
		}

		// Distinct accounts are kept per day, week to date and month to date, as the package does.
		double users = Math.Round(150 * scale);
		int dayOfWeek = ((int)t.DayOfWeek + 6) % 7, dayOfMonth = t.Day - 1;
		foreach ((string suffix, double f) in new[] { ("day", 1.0), ("week", 1 + 0.7 * dayOfWeek), ("month", 1 + 0.6 * dayOfMonth) })
		{
			Add("Applications.Applicants@" + suffix, null, Math.Round(users * f * 0.8));
			Add("Nfc.Users@" + suffix, null, Math.Round(users * f));
		}

		lock (this.dayCache)
			this.dayCache[(neuron, t)] = facts;
		return facts;
	}

	// ----- /Query -----

	private static DateTime BucketOf(DateTime t, string interval, DateTime from) => interval switch
	{
		"week" => t.AddDays(-(((int)t.DayOfWeek + 6) % 7)),
		"month" => new DateTime(t.Year, t.Month, 1),
		"total" => from,
		_ => t,
	};

	private static DateTime BucketEnd(DateTime b, string interval, DateTime to)
	{
		DateTime end = interval switch
		{
			"week" => b.AddDays(6),
			"month" => new DateTime(b.Year, b.Month, 1).AddMonths(1).AddDays(-1),
			"total" => to,
			_ => b,
		};
		return end > to ? to : end;
	}

	private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	private static DateTime? Parse(string? s) => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) ? d : null;

	private (HttpStatusCode, string) Query(System.Collections.Specialized.NameValueCollection q)
	{
		static (HttpStatusCode, string) Bad(string code, string message) => (HttpStatusCode.BadRequest, Error(code, message));

		string? metricsText = q["metrics"];
		if (string.IsNullOrEmpty(metricsText))
			return Bad("missingParameter", "metrics is missing.");

		DateTime to = Parse(q["to"]) ?? this.Today.AddDays(-1);
		string interval = q["interval"] ?? "day";
		if (interval is not ("day" or "week" or "month" or "total"))
			return Bad("invalidParameter", "interval must be day, week, month or total.");
		DateTime givenFrom = Parse(q["from"]) ?? to.AddDays(-29);
		DateTime from = BucketOf(givenFrom, interval, givenFrom);

		bool byNeuron = false, byDimension = false;
		string? dimension = null;
		HashSet<string>? neuronFilter = null, valueFilter = null;
		foreach (string g in (q["groupBy"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!this.CanUseDimension(g))
				return Bad("unknownDimension", "Unknown dimension: " + g + ".");
			if (g == "neuron")
				byNeuron = true;
			else if (dimension is null)
				(dimension, byDimension) = (g, true);
			else
				return Bad("invalidParameter", "groupBy takes neuron and at most one other dimension.");
		}

		foreach (string f in (q["filter"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			int i = f.IndexOf(':');
			string[] values = i > 0 ? f[(i + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
			if (values.Length == 0)
				return Bad("invalidParameter", "filter must be name:value.");
			string name = f[..i].Trim();
			if (!this.CanUseDimension(name))
				return Bad("unknownDimension", "Unknown dimension: " + name + ".");
			if (name == "neuron")
				neuronFilter = new HashSet<string>(values.Select(v => v.ToLowerInvariant()));
			else
			{
				if (dimension is not null && dimension != name)
					return Bad("invalidParameter", "groupBy and filter can use neuron and at most one other dimension, the same in both.");
				dimension = name;
				valueFilter = [.. values];
			}
		}

		string[] answerNeurons = [.. this.Neurons.Where(n => neuronFilter is null || neuronFilter.Contains(n))];
		DateTime finalBefore = this.Today.AddDays(-31);
		List<DateTime> buckets = [];
		for (DateTime t = from; t <= to; t = t.AddDays(1))
		{
			DateTime b = BucketOf(t, interval, from);
			if (buckets.Count == 0 || buckets[^1] != b)
				buckets.Add(b);
		}

		// The Neuron that stopped has no days after it stopped.
		DateTime stoppedAfter = this.Today.AddDays(-4);

		Dictionary<string, Dictionary<DateTime, (double Value, bool Final, Dictionary<string, double> Last)>> Aggregate(JsonObject metric, string storageKey, bool distinct)
		{
			Dictionary<string, Dictionary<DateTime, (double, bool, Dictionary<string, double>)>> output = [];
			for (int ni = 0; ni < this.Neurons.Length; ni++)
			{
				string neuron = this.Neurons[ni];
				if (neuronFilter is not null && !neuronFilter.Contains(neuron))
					continue;
				for (DateTime t = from; t <= to; t = t.AddDays(1))
				{
					if (this.Stopped == ni && t > stoppedAfter)
						continue;
					if (!this.DayFacts(neuron, t).TryGetValue(storageKey, out var facts))
						continue;
					foreach ((string dims, double value) in facts)
					{
						string dimValue = string.Empty;
						if (dimension is not null)
						{
							if (!dims.StartsWith(dimension + "=", StringComparison.Ordinal))
								continue;
							dimValue = dims[(dimension.Length + 1)..];
							if (valueFilter is not null && !valueFilter.Contains(dimValue))
								continue;
						}
						else if (dims.Length != 0)
							continue;

						string group = (byNeuron ? neuron : string.Empty) + "\u0001" + (byDimension ? dimValue : string.Empty);
						DateTime bucket = BucketOf(t, interval, from);
						if (!output.TryGetValue(group, out var points))
							output[group] = points = [];
						(double v, bool fin, Dictionary<string, double> last) p = points.TryGetValue(bucket, out var had) ? had : (0, true, []);
						if (distinct)
							p.last[neuron] = value;
						else
							p.v += value;
						p.fin &= t < finalBefore;
						points[bucket] = p;
					}
				}
			}

			if (distinct)
			{
				foreach (var points in output.Values)
				{
					foreach (DateTime b in points.Keys.ToList())
						points[b] = (points[b].Item3.Values.Sum(), points[b].Item2, points[b].Item3);
				}
			}

			return output;
		}

		JsonObject GroupObject(string group)
		{
			string[] parts = group.Split('\u0001');
			JsonObject g = [];
			if (byNeuron)
				g["neuron"] = parts[0];
			if (byDimension)
				g[dimension!] = parts[1];
			return g;
		}

		List<string> Expected()
		{
			List<string> values = !byDimension ? [string.Empty]
				: [.. valueFilter ?? (IEnumerable<string>?)this.catalog["dimensions"]![dimension!]?["values"]?.AsArray().Select(v => (string)v!) ?? []];
			return [.. (byNeuron ? answerNeurons : [string.Empty]).SelectMany(n => values.Select(v => n + "\u0001" + v))];
		}

		List<string> Ordered(IEnumerable<string> groups)
		{
			List<string> order = byDimension ? [.. this.catalog["dimensions"]![dimension!]?["values"]?.AsArray().Select(v => (string)v!) ?? []] : [];
			int Rank(string v) => order.Contains(v) ? order.IndexOf(v) : order.Count;
			return [.. groups.Distinct().Select(g => (g, g.Split('\u0001')))
				.OrderBy(x => x.Item2[0], StringComparer.Ordinal).ThenBy(x => Rank(x.Item2[1])).ThenBy(x => x.Item2[1], StringComparer.Ordinal).Select(x => x.g)];
		}

		JsonObject Point(DateTime b) => new() { ["start"] = Day(b), ["end"] = Day(BucketEnd(b, interval, to)) };

		JsonArray series = [];
		foreach (string key in metricsText.Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			if (!this.metricByKey.TryGetValue(key, out JsonObject? m) || !this.CanSeeMetric(key))
				return Bad("unknownMetric", "Unknown metric: " + key + ".");
			List<string> dims = [.. m["dimensions"]!.AsArray().Select(x => (string)x!)];
			if (dimension is not null && !dims.Contains(dimension))
				return Bad("unknownDimension", "Metric " + key + " has no dimension " + dimension + ".");
			if (dimension is null && m["hasTotal"] is JsonNode ht && !(bool)ht)
				return Bad("notAvailable", "Metric " + key + " has no total.");
			string kind = (string)m["kind"]!;
			if (kind == "distinct" && interval == "total")
				return Bad("notAvailable", "Metric " + key + " counts distinct accounts. Use interval day, week or month.");

			if (kind == "rate")
			{
				var num = Aggregate(m, (string)m["numerator"]!, false);
				var den = Aggregate(m, (string)m["denominator"]!, false);
				bool parts = this.CanSeeMetric((string)m["numerator"]!) && this.CanSeeMetric((string)m["denominator"]!);
				foreach (string group in Ordered(num.Keys.Concat(den.Keys).Concat(Expected())))
				{
					JsonArray points = [];
					foreach (DateTime b in buckets)
					{
						var nv = num.GetValueOrDefault(group)?.GetValueOrDefault(b) ?? (0, true, null!);
						var dv = den.GetValueOrDefault(group)?.GetValueOrDefault(b) ?? (0, true, null!);
						JsonObject p = Point(b);
						p["value"] = dv.Item1 >= MinimumDenominator ? Math.Round(1000 * nv.Item1 / dv.Item1) / 10 : null;
						if (parts)
						{
							p["numerator"] = nv.Item1;
							p["denominator"] = dv.Item1;
						}

						p["final"] = nv.Item2 && dv.Item2;
						points.Add(p);
					}

					series.Add(new JsonObject { ["metric"] = key, ["kind"] = kind, ["unit"] = (string?)m["unit"], ["group"] = GroupObject(group), ["points"] = points });
				}
			}
			else
			{
				var values = Aggregate(m, kind == "distinct" ? key + "@" + interval : key, kind == "distinct");
				foreach (string group in Ordered(values.Keys.Concat(Expected())))
				{
					JsonArray points = [];
					foreach (DateTime b in buckets)
					{
						var v = values.GetValueOrDefault(group)?.GetValueOrDefault(b);
						JsonObject p = Point(b);
						p["value"] = v?.Item1 ?? 0;
						p["final"] = v?.Item2 ?? true;
						points.Add(p);
					}

					series.Add(new JsonObject { ["metric"] = key, ["kind"] = kind, ["unit"] = (string?)m["unit"], ["group"] = GroupObject(group), ["points"] = points });
				}
			}
		}

		JsonObject answer = new()
		{
			["from"] = Day(from),
			["to"] = Day(to),
			["interval"] = interval,
		};
		if (this.CanUseDimension("neuron"))
			answer["neurons"] = new JsonArray([.. answerNeurons.Select(n => (JsonNode)n)]);
		answer["complete"] = this.Stopped is null || to <= stoppedAfter;
		answer["series"] = series;
		return (HttpStatusCode.OK, answer.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
	}
}
