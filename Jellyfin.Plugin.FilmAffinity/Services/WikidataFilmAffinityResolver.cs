using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FilmAffinity.Services;

/// <summary>
/// Resolves FilmAffinity IDs (Wikidata property P480) from IMDb or TMDb identifiers using the Wikidata SPARQL endpoint.
/// </summary>
public sealed class WikidataFilmAffinityResolver : IDisposable
{
    /// <summary>
    /// The named <see cref="HttpClient"/> registered for Wikidata requests.
    /// </summary>
    public const string HttpClientName = "FilmAffinityWikidata";

    private const string SparqlEndpoint = "https://query.wikidata.org/sparql";
    private const int MaxRetries = 2;

    private static readonly TimeSpan DefaultThrottleDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FilmAffinityCacheService _cache;
    private readonly ILogger<WikidataFilmAffinityResolver> _logger;
    private readonly TimeSpan _throttleDelay;
    private readonly TimeSpan _retryBaseDelay;
    private readonly SemaphoreSlim _throttleLock = new(1, 1);
    private DateTime _nextAllowedRequestUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="WikidataFilmAffinityResolver"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="cache">The cache service.</param>
    /// <param name="logger">The logger.</param>
    public WikidataFilmAffinityResolver(
        IHttpClientFactory httpClientFactory,
        FilmAffinityCacheService cache,
        ILogger<WikidataFilmAffinityResolver> logger)
        : this(httpClientFactory, cache, logger, DefaultThrottleDelay, DefaultRetryBaseDelay)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WikidataFilmAffinityResolver"/> class with custom throttle and retry delays (for testing).
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="cache">The cache service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="throttleDelay">Minimum delay between sequential Wikidata requests.</param>
    /// <param name="retryBaseDelay">Base delay used for exponential backoff on transient failures.</param>
    internal WikidataFilmAffinityResolver(
        IHttpClientFactory httpClientFactory,
        FilmAffinityCacheService cache,
        ILogger<WikidataFilmAffinityResolver> logger,
        TimeSpan throttleDelay,
        TimeSpan retryBaseDelay)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
        _throttleDelay = throttleDelay;
        _retryBaseDelay = retryBaseDelay;
    }

    /// <summary>
    /// Resolves a FilmAffinity ID from an item's provider IDs.
    /// Priority: existing FilmAffinity ID -> IMDb (P345) -> TMDb Movie (P4947) or TMDb TV (P4983).
    /// </summary>
    /// <param name="providerIds">The Jellyfin item's provider IDs dictionary.</param>
    /// <param name="isSeries"><c>true</c> for TV series (uses TMDb TV P4983); <c>false</c> for movies (uses TMDb Movie P4947).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The numeric FilmAffinity ID if resolved; otherwise <c>null</c>.</returns>
    public async Task<string?> ResolveAsync(
        IReadOnlyDictionary<string, string>? providerIds,
        bool isSeries,
        CancellationToken cancellationToken)
    {
        if (providerIds is null || providerIds.Count == 0)
        {
            return null;
        }

        if (providerIds.TryGetValue(Plugin.ProviderKey, out var existingId)
            && !string.IsNullOrWhiteSpace(existingId))
        {
            return existingId.Trim();
        }

        var lookups = new List<(string Property, string Value, string CacheKey)>(2);

        if (providerIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdbId)
            && !string.IsNullOrWhiteSpace(imdbId))
        {
            var trimmed = imdbId.Trim();
            lookups.Add(("P345", trimmed, "P345:" + trimmed));
        }

        if (providerIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId)
            && !string.IsNullOrWhiteSpace(tmdbId))
        {
            var property = isSeries ? "P4983" : "P4947";
            var trimmed = tmdbId.Trim();
            lookups.Add((property, trimmed, property + ":" + trimmed));
        }

        if (lookups.Count == 0)
        {
            return null;
        }

        var uncachedLookups = new List<(string Property, string Value, string CacheKey)>(lookups.Count);
        foreach (var lookup in lookups)
        {
            if (_cache.TryGetFilmAffinityId(lookup.CacheKey, out var cachedId))
            {
                if (!string.IsNullOrEmpty(cachedId))
                {
                    return cachedId;
                }
            }
            else
            {
                uncachedLookups.Add(lookup);
            }
        }

        if (uncachedLookups.Count == 0)
        {
            return null;
        }

        return await QueryLookupsAsync(uncachedLookups, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the first FilmAffinity ID (<c>faId</c>) from a Wikidata SPARQL JSON response.
    /// </summary>
    /// <param name="sparqlJson">The raw SPARQL JSON string.</param>
    /// <returns>The FilmAffinity ID if found; otherwise <c>null</c>.</returns>
    public static string? ParseSparqlResponse(string sparqlJson)
    {
        if (string.IsNullOrWhiteSpace(sparqlJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(sparqlJson);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || !results.TryGetProperty("bindings", out var bindings)
                || bindings.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var binding in bindings.EnumerateArray())
            {
                if (binding.TryGetProperty("faId", out var faIdElement)
                    && faIdElement.TryGetProperty("value", out var valueElement)
                    && valueElement.ValueKind == JsonValueKind.String)
                {
                    var value = valueElement.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _throttleLock.Dispose();
    }

    internal static string BuildSparqlQuery(IReadOnlyList<(string Property, string Value, string CacheKey)> lookups)
    {
        if (lookups.Count == 1)
        {
            var (property, value, _) = lookups[0];
            return $"SELECT ?faId WHERE {{ ?item wdt:{property} \"{EscapeSparqlLiteral(value)}\" ; wdt:P480 ?faId . }} LIMIT 1";
        }

        var branches = new string[lookups.Count];
        for (var i = 0; i < lookups.Count; i++)
        {
            var (property, value, _) = lookups[i];
            branches[i] = $"{{ ?item wdt:{property} \"{EscapeSparqlLiteral(value)}\" ; wdt:P480 ?faId . BIND({i + 1} AS ?priority) }}";
        }

        return $"SELECT ?faId WHERE {{ {string.Join(" UNION ", branches)} }} ORDER BY ?priority LIMIT 1";
    }

    private static string EscapeSparqlLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == HttpStatusCode.TooManyRequests
            || statusCode == HttpStatusCode.RequestTimeout
            || (code >= 500 && code <= 599);
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        if (response?.Headers.RetryAfter is { } retryAfter)
        {
            TimeSpan? headerDelay = retryAfter.Delta;
            if (!headerDelay.HasValue && retryAfter.Date.HasValue)
            {
                headerDelay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            }

            if (headerDelay.HasValue && headerDelay.Value > TimeSpan.Zero)
            {
                return headerDelay.Value > MaxRetryDelay ? MaxRetryDelay : headerDelay.Value;
            }
        }

        if (_retryBaseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = 1 << attempt;
        var computed = TimeSpan.FromMilliseconds(_retryBaseDelay.TotalMilliseconds * multiplier);
        return computed > MaxRetryDelay ? MaxRetryDelay : computed;
    }

    private async Task<string?> QueryLookupsAsync(
        IReadOnlyList<(string Property, string Value, string CacheKey)> lookups,
        CancellationToken cancellationToken)
    {
        var query = BuildSparqlQuery(lookups);
        var requestUri = new Uri($"{SparqlEndpoint}?format=json&query={Uri.EscapeDataString(query)}");
        var lookupDescription = string.Join(", ", lookups.Select(l => $"{l.Property}={l.Value}"));

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await WaitForThrottleAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Accept.ParseAdd("application/sparql-results+json");

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransientStatusCode(response.StatusCode) && attempt < MaxRetries)
                    {
                        var delay = GetRetryDelay(response, attempt);
                        await ApplyBackoffAsync(delay, cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning(
                            "Wikidata SPARQL lookup failed for {Lookups} with HTTP {StatusCode}. Backing off for {DelaySeconds:F1}s before retry {Attempt}/{MaxRetries}.",
                            lookupDescription,
                            (int)response.StatusCode,
                            delay.TotalSeconds,
                            attempt + 1,
                            MaxRetries);
                        continue;
                    }

                    _logger.LogWarning(
                        "Wikidata SPARQL lookup failed for {Lookups} with HTTP {StatusCode}",
                        lookupDescription,
                        (int)response.StatusCode);
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var filmAffinityId = ParseSparqlResponse(content);
                foreach (var lookup in lookups)
                {
                    _cache.SetFilmAffinityId(lookup.CacheKey, filmAffinityId);
                }

                return filmAffinityId;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt < MaxRetries)
                {
                    var delay = GetRetryDelay(null, attempt);
                    await ApplyBackoffAsync(delay, cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning(
                        ex,
                        "Wikidata SPARQL request error for {Lookups}. Backing off for {DelaySeconds:F1}s before retry {Attempt}/{MaxRetries}.",
                        lookupDescription,
                        delay.TotalSeconds,
                        attempt + 1,
                        MaxRetries);
                    continue;
                }

                _logger.LogWarning(
                    ex,
                    "Wikidata SPARQL request error for {Lookups} after {MaxRetries} retries",
                    lookupDescription,
                    MaxRetries);
                return null;
            }
        }

        return null;
    }

    private async Task ApplyBackoffAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await _throttleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = DateTime.UtcNow + delay;
            if (target > _nextAllowedRequestUtc)
            {
                _nextAllowedRequestUtc = target;
            }
        }
        finally
        {
            _throttleLock.Release();
        }
    }

    private async Task WaitForThrottleAsync(CancellationToken cancellationToken)
    {
        await _throttleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            if (_nextAllowedRequestUtc > now)
            {
                await Task.Delay(_nextAllowedRequestUtc - now, cancellationToken).ConfigureAwait(false);
            }

            _nextAllowedRequestUtc = _throttleDelay > TimeSpan.Zero
                ? DateTime.UtcNow + _throttleDelay
                : DateTime.UtcNow;
        }
        finally
        {
            _throttleLock.Release();
        }
    }
}
