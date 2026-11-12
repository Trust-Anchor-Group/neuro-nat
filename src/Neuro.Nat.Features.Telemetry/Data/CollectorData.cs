using Neuro.Nat.Features.Telemetry.Api;

namespace Neuro.Nat.Features.Telemetry.Data;

/// <summary>What one collector's dashboard has to show: its model, and how fresh and whole it is.</summary>
public sealed record Snapshot(DashboardModel? Model, string? Problem, DateTime? AskedAt)
{
	public bool HasNumbers => this.Model is not null;
}

/// <summary>
/// One collector's numbers: asks its API through the fetcher (which keeps the hourly rule and the
/// ETags), and makes the model the pages are drawn from. Nothing here knows any other collector.
/// </summary>
public sealed class CollectorData(TelemetryCollector collector, TelemetryFetcher fetcher, Func<string, string> nameOf, Func<DateTime> utcNow)
{
	private readonly object sync = new();
	private readonly Dictionary<string, QueryAnswer> parsed = [];
	private Snapshot? overview;

	public TelemetryCollector Collector => collector;

	/// <summary>The latest overview made, for the Inbox and the big screen. Null before the first.</summary>
	public Snapshot? Overview
	{
		get
		{
			lock (this.sync)
				return this.overview;
		}
	}

	/// <summary>Raised when a new overview was made (what the Inbox and the big screen read).</summary>
	public event EventHandler? Changed;

	/// <summary>
	/// The numbers for some pages and choices on the All metrics page. With ask false, only what was
	/// kept is used (nothing goes to the Neuron); else each question goes to the Neuron when its
	/// answer is an hour old.
	/// </summary>
	public async Task<Snapshot> GetAsync(IReadOnlyList<string> pages, IReadOnlyList<TelemetryQuery>? more, bool ask, CancellationToken cancel = default)
	{
		await fetcher.LoadAsync();
		Task<Fetched> Get(string path) => ask ? fetcher.GetAsync(collector, path, cancel) : Task.FromResult(fetcher.Kept(collector, path));

		Fetched catalogFetched = await Get("/Catalog");
		Catalog? catalog = catalogFetched.Body is string cb ? TelemetryJson.Catalog(cb) : null;
		if (catalog is null)
			return new Snapshot(null, catalogFetched.Problem ?? (ask ? "No numbers" : null), catalogFetched.AskedAt);

		Fetched statusFetched = await Get("/Status");
		StatusAnswer? status = statusFetched.Body is string sb ? TelemetryJson.Status(sb) : null;

		DateTime now = utcNow();
		Questions q = new(catalog, Period.Before(now));
		List<TelemetryQuery> all = [.. pages.SelectMany(q.For), .. more ?? []];
		all = [.. all.DistinctBy(x => x.ToPath())];

		// A few at a time, so the Neuron is not flooded.
		using SemaphoreSlim gate = new(4, 4);
		List<string> problems = [];
		DateTime? oldest = catalogFetched.AskedAt;
		Dictionary<string, QueryAnswer> answers = [];
		await Task.WhenAll(all.Select(async query =>
		{
			await gate.WaitAsync(cancel);
			try
			{
				string path = query.ToPath();
				Fetched f = await Get(path);
				QueryAnswer? a = null;
				if (f.Body is string body)
				{
					lock (this.sync)
					{
						if (!this.parsed.TryGetValue(path + "|" + body.Length + "|" + f.AskedAt?.Ticks, out a))
						{
							a = TelemetryJson.Query(body);
							if (a is not null)
								this.parsed[path + "|" + body.Length + "|" + f.AskedAt?.Ticks] = a;
						}
					}
				}

				lock (answers)
				{
					if (a is not null)
						answers[path] = a;
					if (f.Problem is string p)
						problems.Add(p);
					if (f.AskedAt is DateTime t && (oldest is null || t < oldest))
						oldest = t;
				}
			}
			finally
			{
				gate.Release();
			}
		}));

		DashboardModel model = new(q, query => query is not null && answers.TryGetValue(query.ToPath(), out QueryAnswer? a) ? a : QueryAnswer.Empty, status, nameOf, now);
		string? problem = catalogFetched.Problem ?? statusFetched.Problem ?? problems.FirstOrDefault();
		Snapshot snapshot = new(model, problem, oldest);
		if (pages.Contains(Pages.Overview))
		{
			lock (this.sync)
				this.overview = snapshot;
			this.Changed?.Invoke(this, EventArgs.Empty);
		}

		return snapshot;
	}
}
