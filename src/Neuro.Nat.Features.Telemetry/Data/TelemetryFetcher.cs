using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Neuro.Nat.App;
using Neuro.Nat.Engine.Storage;
using Neuro.Nat.Features.Telemetry.Api;

namespace Neuro.Nat.Features.Telemetry.Data;

/// <summary>A Neuron whose telemetry API answers this account: its numbers make one dashboard.</summary>
public sealed record TelemetryCollector(Guid AccountId, string Domain, Uri WebAddress)
{
	/// <summary>The key its answers are kept under. Two collectors never share one.</summary>
	public string Key => this.AccountId.ToString("N") + "|" + this.Domain.ToLowerInvariant();
}

/// <summary>What asking for one path came to.</summary>
public sealed record Fetched(string? Body, DateTime? AskedAt, string? Problem, bool Asked)
{
	public bool HasBody => this.Body is not null;
}

/// <summary>
/// Asks a collector's web API and keeps what it answers, kindly: each path at most once an hour
/// (a path that failed is tried again after five minutes), with the last ETag in If-None-Match so
/// "nothing changed" costs a short 304. What was fetched is kept in the store (the encrypted
/// database), so the numbers are there at the next start.
/// </summary>
public sealed class TelemetryFetcher : IDisposable
{
	/// <summary>How long an answer is used before the Neuron is asked again.</summary>
	public static readonly TimeSpan Fresh = TimeSpan.FromHours(1);

	/// <summary>How long after a failed question it is asked again.</summary>
	public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

	/// <summary>How long a token is used: it lives 60 seconds.</summary>
	private static readonly TimeSpan TokenLife = TimeSpan.FromSeconds(40);

	private readonly HttpClient http;
	private readonly Func<Guid, CancellationToken, Task<string?>> tokens;
	private readonly IFeatureAnswerStore store;
	private readonly Func<DateTime> utcNow;
	private readonly string feature;
	private readonly object sync = new();
	private readonly Dictionary<string, FeatureAnswer> answers = [];
	private readonly Dictionary<string, DateTime> failed = [];
	private readonly Dictionary<string, Task<Fetched>> asking = [];
	private readonly Dictionary<Guid, (string Token, DateTime At)> tokenCache = [];
	private readonly SemaphoreSlim tokenGate = new(1, 1);
	private Task? loaded;

	public TelemetryFetcher(HttpClient http, Func<Guid, CancellationToken, Task<string?>> tokens, IFeatureAnswerStore store, Func<DateTime> utcNow, string feature = TelemetryFeature.FeatureKey)
	{
		this.http = http;
		this.tokens = tokens;
		this.store = store;
		this.utcNow = utcNow;
		this.feature = feature;
	}

	/// <summary>How many requests went to a Neuron. For tests.</summary>
	public int Requests { get; private set; }

	/// <summary>How many of them were answered "not modified". For tests.</summary>
	public int NotModified { get; private set; }

	private static string KeyOf(TelemetryCollector c, string path) => c.Key + "|" + path;

	/// <summary>Reads what was kept at the last run, once.</summary>
	public Task LoadAsync()
	{
		lock (this.sync)
			return this.loaded ??= this.LoadKeptAsync();
	}

	private async Task LoadKeptAsync()
	{
		try
		{
			IReadOnlyDictionary<string, FeatureAnswer> kept = await this.store.LoadAsync(this.feature);
			lock (this.sync)
			{
				foreach ((string key, FeatureAnswer a) in kept)
					this.answers.TryAdd(key, a);
			}
		}
		catch (Exception ex)
		{
			AppLog.Error("The kept app telemetry numbers could not be read.", ex);
		}
	}

	/// <summary>The kept answer for a path, without asking.</summary>
	public Fetched Kept(TelemetryCollector c, string path)
	{
		lock (this.sync)
			return this.answers.TryGetValue(KeyOf(c, path), out FeatureAnswer? a) ? new Fetched(a.Body, a.AskedAt, null, false) : new Fetched(null, null, null, false);
	}

	/// <summary>
	/// The answer for a path: the kept one while it is less than an hour old, else the Neuron is
	/// asked (with If-None-Match). When asking fails, the kept answer is given with the problem.
	/// </summary>
	public async Task<Fetched> GetAsync(TelemetryCollector c, string path, CancellationToken cancel = default)
	{
		await this.LoadAsync();
		string key = KeyOf(c, path);
		Task<Fetched> task;
		lock (this.sync)
		{
			DateTime now = this.utcNow();
			this.answers.TryGetValue(key, out FeatureAnswer? kept);
			if (kept is not null && now - kept.AskedAt < Fresh)
				return new Fetched(kept.Body, kept.AskedAt, null, false);
			if (this.failed.TryGetValue(key, out DateTime failedAt) && now - failedAt < RetryAfter)
				return new Fetched(kept?.Body, kept?.AskedAt, "Asked a moment ago", false);

			// The same question asked twice at once goes to the Neuron once.
			if (!this.asking.TryGetValue(key, out task!))
			{
				task = this.AskAsync(c, path, key, kept, cancel);
				this.asking[key] = task;
			}
		}

		try
		{
			return await task;
		}
		finally
		{
			lock (this.sync)
			{
				if (this.asking.TryGetValue(key, out Task<Fetched>? t) && t == task)
					this.asking.Remove(key);
			}
		}
	}

	private async Task<Fetched> AskAsync(TelemetryCollector c, string path, string key, FeatureAnswer? kept, CancellationToken cancel)
	{
		await Task.Yield();
		string? problem;
		try
		{
			string? token = await this.TokenAsync(c.AccountId, cancel);
			if (string.IsNullOrEmpty(token))
				problem = "Not signed in";
			else
			{
				using HttpRequestMessage request = new(HttpMethod.Get, new Uri(c.WebAddress, TelemetryFeature.ApiPath + path));
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
				request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
				if (kept?.ETag is string etag && EntityTagHeaderValue.TryParse(etag, out EntityTagHeaderValue? tag))
					request.Headers.IfNoneMatch.Add(tag);

				lock (this.sync)
					this.Requests++;
				using HttpResponseMessage response = await this.http.SendAsync(request, cancel);
				DateTime now = this.utcNow();

				if (response.StatusCode == HttpStatusCode.NotModified && kept is not null)
				{
					lock (this.sync)
						this.NotModified++;
					return await this.KeepAsync(key, kept with { AskedAt = now });
				}

				string body = await response.Content.ReadAsStringAsync(cancel);
				if (response.StatusCode == HttpStatusCode.OK)
					return await this.KeepAsync(key, new FeatureAnswer(body, response.Headers.ETag?.ToString(), now));

				if (response.StatusCode == HttpStatusCode.Unauthorized)
				{
					lock (this.sync)
						this.tokenCache.Remove(c.AccountId);
				}

				problem = TelemetryJson.ErrorMessage(body) ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase;
			}
		}
		catch (HttpRequestException ex)
		{
			problem = "No answer (" + ex.HttpRequestError + ")";
		}
		catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
		{
			problem = "No answer in time";
		}

		lock (this.sync)
			this.failed[key] = this.utcNow();
		AppLog.Info($"App telemetry on {c.Domain}: {path.Split('?')[0]} failed ({problem}).");
		return new Fetched(kept?.Body, kept?.AskedAt, problem, true);
	}

	private async Task<Fetched> KeepAsync(string key, FeatureAnswer answer)
	{
		lock (this.sync)
		{
			this.answers[key] = answer;
			this.failed.Remove(key);
		}

		try
		{
			await this.store.SaveAsync(this.feature, key, answer);
		}
		catch (Exception ex)
		{
			AppLog.Error("App telemetry numbers could not be kept.", ex);
		}

		return new Fetched(answer.Body, answer.AskedAt, null, true);
	}

	/// <summary>A token for the account's Neuron, reused for a few seconds so a round asks for one.</summary>
	private async Task<string?> TokenAsync(Guid accountId, CancellationToken cancel)
	{
		await this.tokenGate.WaitAsync(cancel);
		try
		{
			lock (this.sync)
			{
				if (this.tokenCache.TryGetValue(accountId, out (string Token, DateTime At) t) && this.utcNow() - t.At < TokenLife)
					return t.Token;
			}

			string? token = await this.tokens(accountId, cancel);
			if (!string.IsNullOrEmpty(token))
			{
				lock (this.sync)
					this.tokenCache[accountId] = (token, this.utcNow());
			}

			return token;
		}
		finally
		{
			this.tokenGate.Release();
		}
	}

	public void Dispose() => this.tokenGate.Dispose();
}
