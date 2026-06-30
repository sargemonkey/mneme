using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// Client-side throttle + retry for an OpenAI-compatible endpoint. A long
/// LoCoMo run makes thousands of calls; GitHub Models' free tier rate-limits
/// aggressively (HTTP 429 with a Retry-After header). This:
/// <list type="bullet">
///   <item>spaces requests to at most <c>RPM</c> per minute (a shared token
///         gate, so chat + embeddings draw from one budget);</item>
///   <item>retries 429 and 5xx, honoring the server's <c>Retry-After</c> when
///         present, otherwise exponential backoff with jitter;</item>
///   <item>surfaces the response body on a terminal failure for diagnosis.</item>
/// </list>
/// Combined with the harness's per-question checkpointing, a run that exhausts
/// the daily quota can simply be resumed later.
/// </summary>
public sealed class ThrottledHttp : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minInterval;
    private readonly int _maxRetries;
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public ThrottledHttp(string baseUrl, string apiKey, double requestsPerMinute, int maxRetries,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(180),
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        }
        if (extraHeaders is not null)
        {
            foreach (var (k, v) in extraHeaders) _http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
        }
        _minInterval = requestsPerMinute > 0 ? TimeSpan.FromSeconds(60.0 / requestsPerMinute) : TimeSpan.Zero;
        _maxRetries = Math.Max(0, maxRetries);
    }

    public async Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct).ConfigureAwait(false);
            HttpResponseMessage resp;
            try
            {
                resp = await _http.PostAsJsonAsync(path, body, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < _maxRetries)
            {
                await BackoffAsync(attempt, null, ct).ConfigureAwait(false);
                continue;
            }

            if (resp.IsSuccessStatusCode)
            {
                var ok = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                resp.Dispose();
                return JsonDocument.Parse(ok);
            }

            var retriable = resp.StatusCode == HttpStatusCode.TooManyRequests
                || (int)resp.StatusCode >= 500;
            var bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var retryAfter = resp.Headers.RetryAfter?.Delta
                ?? (resp.Headers.TryGetValues("retry-after", out var vals)
                    && int.TryParse(vals.FirstOrDefault(), out var secs)
                        ? TimeSpan.FromSeconds(secs) : (TimeSpan?)null);
            resp.Dispose();

            if (!retriable || attempt >= _maxRetries)
            {
                throw new HttpRequestException(
                    $"Request to {path} failed ({(int)resp.StatusCode}) after {attempt + 1} attempt(s): " +
                    Truncate(bodyText, 300));
            }
            Console.Error.WriteLine($"  rate/5xx ({(int)resp.StatusCode}); retry {attempt + 1}/{_maxRetries}" +
                                    (retryAfter is { } ra ? $" after {ra.TotalSeconds:F0}s" : ""));
            await BackoffAsync(attempt, retryAfter, ct).ConfigureAwait(false);
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        if (_minInterval <= TimeSpan.Zero) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var wait = _nextAllowed - now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _nextAllowed = DateTimeOffset.UtcNow + _minInterval;
        }
        finally { _gate.Release(); }
    }

    private static async Task BackoffAsync(int attempt, TimeSpan? retryAfter, CancellationToken ct)
    {
        if (retryAfter is { } ra)
        {
            await Task.Delay(ra + TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            return;
        }
        var seconds = Math.Min(15, Math.Pow(2, attempt)); // 1,2,4,8,15,15… (concurrency 429s clear fast)
        var jitter = Random.Shared.NextDouble() * 0.5;
        await Task.Delay(TimeSpan.FromSeconds(seconds + jitter), ct).ConfigureAwait(false);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
