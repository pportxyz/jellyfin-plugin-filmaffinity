using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FilmAffinity.Configuration;
using Jellyfin.Plugin.FilmAffinity.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FilmAffinity.Services;

/// <summary>
/// Fetches FilmAffinity scores from the configured external JSON API or HTML proxy/mirror endpoint
/// with throttling, caching, minimum-vote filtering, and Cloudflare block detection.
/// </summary>
public sealed class FilmAffinityScoreClient : IDisposable
{
    /// <summary>
    /// The named <see cref="HttpClient"/> registered for FilmAffinity score requests.
    /// </summary>
    public const string HttpClientName = "FilmAffinityScore";

    private const int MaxRateLimitRetries = 2;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FilmAffinityCacheService _cache;
    private readonly ILogger<FilmAffinityScoreClient> _logger;
    private readonly Func<PluginConfiguration> _configurationProvider;
    private readonly Func<DateTime> _utcNowProvider;
    private readonly SemaphoreSlim _throttleLock = new(1, 1);
    private DateTime _nextAllowedRequestUtc = DateTime.MinValue;
    private long _rateLimitCooldownUntilTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmAffinityScoreClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="cache">The cache service.</param>
    /// <param name="logger">The logger.</param>
    public FilmAffinityScoreClient(
        IHttpClientFactory httpClientFactory,
        FilmAffinityCacheService cache,
        ILogger<FilmAffinityScoreClient> logger)
        : this(
            httpClientFactory,
            cache,
            logger,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmAffinityScoreClient"/> class with a custom configuration provider (for testing).
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="cache">The cache service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configurationProvider">Delegate returning the active plugin configuration.</param>
    internal FilmAffinityScoreClient(
        IHttpClientFactory httpClientFactory,
        FilmAffinityCacheService cache,
        ILogger<FilmAffinityScoreClient> logger,
        Func<PluginConfiguration> configurationProvider)
        : this(httpClientFactory, cache, logger, configurationProvider, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmAffinityScoreClient"/> class with custom configuration and clock (for testing).
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="cache">The cache service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configurationProvider">Delegate returning the active plugin configuration.</param>
    /// <param name="utcNowProvider">Optional UTC clock delegate.</param>
    internal FilmAffinityScoreClient(
        IHttpClientFactory httpClientFactory,
        FilmAffinityCacheService cache,
        ILogger<FilmAffinityScoreClient> logger,
        Func<PluginConfiguration> configurationProvider,
        Func<DateTime>? utcNowProvider)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
        _configurationProvider = configurationProvider;
        _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Gets a value indicating whether score requests are currently paused due to an active HTTP 429 cooldown window.
    /// </summary>
    public bool IsInRateLimitCooldown => TryGetRemainingCooldown(out _);

    /// <summary>
    /// Fetches a FilmAffinity score for the specified FilmAffinity ID.
    /// </summary>
    /// <param name="filmAffinityId">The numeric FilmAffinity ID.</param>
    /// <param name="productionYear">Optional production year (used to tune cache TTL).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <see cref="FilmAffinityScore"/> if available and meeting <c>MinVotes</c>; otherwise <c>null</c>.</returns>
    public async Task<FilmAffinityScore?> GetScoreAsync(
        string filmAffinityId,
        int? productionYear,
        CancellationToken cancellationToken)
    {
        var result = await FetchScoreAsync(filmAffinityId, productionYear, cancellationToken)
            .ConfigureAwait(false);
        return result.Score;
    }

    /// <summary>
    /// Fetches a FilmAffinity score along with rate-limit status for the specified FilmAffinity ID.
    /// </summary>
    /// <param name="filmAffinityId">The numeric FilmAffinity ID.</param>
    /// <param name="productionYear">Optional production year (used to tune cache TTL).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="FilmAffinityFetchResult"/> indicating the parsed score and whether HTTP 429 exhausted retries.</returns>
    public async Task<FilmAffinityFetchResult> FetchScoreAsync(
        string filmAffinityId,
        int? productionYear,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filmAffinityId))
        {
            return new FilmAffinityFetchResult(null, RateLimited: false);
        }

        var config = _configurationProvider();

        if (_cache.TryGetScore(filmAffinityId, out var cachedScore) && cachedScore is not null)
        {
            var validCached = MeetsMinVotes(cachedScore, config.MinVotes) ? cachedScore : null;
            return new FilmAffinityFetchResult(validCached, RateLimited: false);
        }

        if (TryGetRemainingCooldown(out var remainingCooldown))
        {
            _logger.LogDebug(
                "Skipping FilmAffinity score request for ID {FilmAffinityId}; HTTP 429 cooldown active for another {RemainingSeconds:F0}s.",
                filmAffinityId,
                remainingCooldown.TotalSeconds);
            return new FilmAffinityFetchResult(null, RateLimited: true);
        }

        Uri requestUri;
        try
        {
            requestUri = FilmAffinityScoreParser.BuildRequestUri(
                config.ScoreEndpointUrl,
                filmAffinityId,
                config.LinkLanguage);
        }
        catch (UriFormatException ex)
        {
            _logger.LogError(
                ex,
                "Invalid FilmAffinity ScoreEndpointUrl configured: {EndpointUrl}",
                config.ScoreEndpointUrl);
            return new FilmAffinityFetchResult(null, RateLimited: false);
        }

        for (var attempt = 0; attempt <= MaxRateLimitRetries; attempt++)
        {
            if (TryGetRemainingCooldown(out _))
            {
                return new FilmAffinityFetchResult(null, RateLimited: true);
            }

            await WaitForThrottleAsync(config.ThrottleMs, cancellationToken).ConfigureAwait(false);

            if (TryGetRemainingCooldown(out _))
            {
                return new FilmAffinityFetchResult(null, RateLimited: true);
            }

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

                if (!string.IsNullOrWhiteSpace(config.UserAgent))
                {
                    request.Headers.UserAgent.TryParseAdd(config.UserAgent);
                }

                if (!string.IsNullOrWhiteSpace(config.AuthHeaderName)
                    && !string.IsNullOrWhiteSpace(config.AuthHeaderValue))
                {
                    request.Headers.TryAddWithoutValidation(config.AuthHeaderName, config.AuthHeaderValue);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timeoutSeconds = Math.Clamp(config.RequestTimeoutSeconds, 1, 120);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt < MaxRateLimitRetries)
                    {
                        var retryDelay = GetRateLimitDelay(response, config.ThrottleMs, attempt);
                        await ApplyBackoffAsync(retryDelay, cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning(
                            "Rate limited (HTTP 429) for {RequestUri}. Backing off for {DelaySeconds:F1}s before retry {Attempt}/{MaxRetries}.",
                            requestUri,
                            retryDelay.TotalSeconds,
                            attempt + 1,
                            MaxRateLimitRetries);
                        continue;
                    }

                    var cooldownMinutes = Math.Clamp(config.RateLimitCooldownMinutes, 0, 1440);
                    if (cooldownMinutes > 0)
                    {
                        ActivateRateLimitCooldown(TimeSpan.FromMinutes(cooldownMinutes));
                        _logger.LogWarning(
                            "Rate limited (HTTP 429) for {RequestUri} after {MaxRetries} consecutive retries. Pausing FilmAffinity score requests for {CooldownMinutes} minutes so the cooldown window can reset.",
                            requestUri,
                            MaxRateLimitRetries,
                            cooldownMinutes);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Rate limited (HTTP 429) for {RequestUri} after {MaxRetries} consecutive retries.",
                            requestUri,
                            MaxRateLimitRetries);
                    }

                    return new FilmAffinityFetchResult(null, RateLimited: true);
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (FilmAffinityScoreParser.IsCloudflareBlocked(response, body))
                    {
                        _logger.LogWarning(
                            "Cloudflare protection blocked request to {RequestUri} (HTTP {StatusCode}). Configure an external ScoreEndpointUrl in FilmAffinity Ratings plugin settings.",
                            requestUri,
                            (int)response.StatusCode);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "FilmAffinity score endpoint returned HTTP {StatusCode} for ID {FilmAffinityId} ({RequestUri})",
                            (int)response.StatusCode,
                            filmAffinityId,
                            requestUri);
                    }

                    return new FilmAffinityFetchResult(null, RateLimited: false);
                }

                var contentType = response.Content.Headers.ContentType?.MediaType;
                var score = FilmAffinityScoreParser.Parse(filmAffinityId, body, contentType, config.ResponseMode);
                if (score is null)
                {
                    if (FilmAffinityScoreParser.IsCloudflareBlocked(response, body))
                    {
                        _logger.LogWarning(
                            "Cloudflare protection blocked request to {RequestUri} (HTTP {StatusCode}). Configure an external ScoreEndpointUrl in FilmAffinity Ratings plugin settings.",
                            requestUri,
                            (int)response.StatusCode);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Could not parse FilmAffinity rating for ID {FilmAffinityId} from {RequestUri}",
                            filmAffinityId,
                            requestUri);
                    }

                    return new FilmAffinityFetchResult(null, RateLimited: false);
                }

                _cache.SetScore(filmAffinityId, score, productionYear);

                if (!MeetsMinVotes(score, config.MinVotes))
                {
                    _logger.LogDebug(
                        "Ignoring FilmAffinity score {Rating} for ID {FilmAffinityId} because vote count {Votes} is below MinVotes ({MinVotes})",
                        score.Rating,
                        filmAffinityId,
                        score.VoteCount,
                        config.MinVotes);
                    return new FilmAffinityFetchResult(null, RateLimited: false);
                }

                return new FilmAffinityFetchResult(score, RateLimited: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to fetch FilmAffinity score for ID {FilmAffinityId} from {RequestUri}",
                    filmAffinityId,
                    requestUri);
                return new FilmAffinityFetchResult(null, RateLimited: false);
            }
        }

        return new FilmAffinityFetchResult(null, RateLimited: true);
    }

    /// <summary>
    /// Searches FilmAffinity directly by title, media type, and optional year as a fallback when Wikidata lacks a P480 claim.
    /// Shares the same throttle, rate-limit cooldown, and Cloudflare detection as score fetches.
    /// </summary>
    /// <param name="title">The primary item title.</param>
    /// <param name="originalTitle">Optional original item title.</param>
    /// <param name="isSeries"><c>true</c> for TV series; <c>false</c> for movies.</param>
    /// <param name="productionYear">Optional production year.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="FilmAffinitySearchResult"/> containing any unambiguous match and error status.</returns>
    public async Task<FilmAffinitySearchResult> SearchFilmAffinityIdAsync(
        string? title,
        string? originalTitle,
        bool isSeries,
        int? productionYear,
        CancellationToken cancellationToken)
    {
        var primaryTitle = !string.IsNullOrWhiteSpace(title) ? title.Trim() : originalTitle?.Trim();
        if (string.IsNullOrWhiteSpace(primaryTitle))
        {
            return new FilmAffinitySearchResult(null, RateLimited: false, HadTransientError: false);
        }

        var config = _configurationProvider();
        if (!FilmAffinitySearchParser.CanUseDirectSearch(config.ScoreEndpointUrl))
        {
            return new FilmAffinitySearchResult(null, RateLimited: false, HadTransientError: false);
        }

        var lang = FilmAffinityScoreParser.NormalizeLanguage(config.LinkLanguage);
        var normalizedPrimary = FilmAffinitySearchParser.NormalizeTitle(primaryTitle);
        var normalizedOriginal = FilmAffinitySearchParser.NormalizeTitle(originalTitle);
        var cacheKey = $"search:{lang}:{(isSeries ? "tv" : "movie")}:{normalizedPrimary}:{normalizedOriginal}:{productionYear}";

        if (_cache.TryGetFilmAffinityId(cacheKey, out var cachedId))
        {
            return new FilmAffinitySearchResult(cachedId, RateLimited: false, HadTransientError: false);
        }

        if (TryGetRemainingCooldown(out var remainingCooldown))
        {
            _logger.LogDebug(
                "Skipping FilmAffinity title search for {Title}; HTTP 429 cooldown active for another {RemainingSeconds:F0}s.",
                primaryTitle,
                remainingCooldown.TotalSeconds);
            return new FilmAffinitySearchResult(null, RateLimited: true, HadTransientError: true);
        }

        var queries = new System.Collections.Generic.List<(string QueryTitle, string QueryLanguage)>(2)
        {
            (primaryTitle, lang)
        };

        if (!string.IsNullOrWhiteSpace(originalTitle)
            && !string.IsNullOrEmpty(normalizedOriginal)
            && !string.Equals(normalizedOriginal, normalizedPrimary, StringComparison.Ordinal))
        {
            queries.Add((originalTitle.Trim(), "en"));
        }

        var hadTransientError = false;
        foreach (var (queryTitle, queryLang) in queries)
        {
            var result = await ExecuteSearchQueryAsync(
                queryTitle,
                queryLang,
                primaryTitle,
                originalTitle,
                isSeries,
                productionYear,
                config,
                cancellationToken).ConfigureAwait(false);

            if (result.RateLimited)
            {
                return result;
            }

            if (!string.IsNullOrEmpty(result.FilmAffinityId))
            {
                _cache.SetFilmAffinityId(cacheKey, result.FilmAffinityId);
                _logger.LogInformation(
                    "Resolved FilmAffinity ID {FilmAffinityId} via title search fallback for {Title} ({Year})",
                    result.FilmAffinityId,
                    primaryTitle,
                    productionYear);
                return result;
            }

            if (result.HadTransientError)
            {
                hadTransientError = true;
            }
        }

        if (!hadTransientError)
        {
            _cache.SetFilmAffinityId(cacheKey, null);
        }

        return new FilmAffinitySearchResult(null, RateLimited: false, HadTransientError: hadTransientError);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _throttleLock.Dispose();
    }

    private async Task<FilmAffinitySearchResult> ExecuteSearchQueryAsync(
        string queryTitle,
        string queryLanguage,
        string primaryTitle,
        string? originalTitle,
        bool isSeries,
        int? productionYear,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var requestUri = FilmAffinitySearchParser.BuildSearchUri(queryTitle, queryLanguage);

        for (var attempt = 0; attempt <= MaxRateLimitRetries; attempt++)
        {
            if (TryGetRemainingCooldown(out _))
            {
                return new FilmAffinitySearchResult(null, RateLimited: true, HadTransientError: true);
            }

            await WaitForThrottleAsync(config.ThrottleMs, cancellationToken).ConfigureAwait(false);

            if (TryGetRemainingCooldown(out _))
            {
                return new FilmAffinitySearchResult(null, RateLimited: true, HadTransientError: true);
            }

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

                if (!string.IsNullOrWhiteSpace(config.UserAgent))
                {
                    request.Headers.UserAgent.TryParseAdd(config.UserAgent);
                }

                if (!string.IsNullOrWhiteSpace(config.AuthHeaderName)
                    && !string.IsNullOrWhiteSpace(config.AuthHeaderValue))
                {
                    request.Headers.TryAddWithoutValidation(config.AuthHeaderName, config.AuthHeaderValue);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timeoutSeconds = Math.Clamp(config.RequestTimeoutSeconds, 1, 120);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt < MaxRateLimitRetries)
                    {
                        var retryDelay = GetRateLimitDelay(response, config.ThrottleMs, attempt);
                        await ApplyBackoffAsync(retryDelay, cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning(
                            "Rate limited (HTTP 429) during FilmAffinity search {RequestUri}. Backing off for {DelaySeconds:F1}s before retry {Attempt}/{MaxRetries}.",
                            requestUri,
                            retryDelay.TotalSeconds,
                            attempt + 1,
                            MaxRateLimitRetries);
                        continue;
                    }

                    var cooldownMinutes = Math.Clamp(config.RateLimitCooldownMinutes, 0, 1440);
                    if (cooldownMinutes > 0)
                    {
                        ActivateRateLimitCooldown(TimeSpan.FromMinutes(cooldownMinutes));
                        _logger.LogWarning(
                            "Rate limited (HTTP 429) during FilmAffinity search {RequestUri} after {MaxRetries} consecutive retries. Pausing FilmAffinity requests for {CooldownMinutes} minutes.",
                            requestUri,
                            MaxRateLimitRetries,
                            cooldownMinutes);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Rate limited (HTTP 429) during FilmAffinity search {RequestUri} after {MaxRetries} consecutive retries.",
                            requestUri,
                            MaxRateLimitRetries);
                    }

                    return new FilmAffinitySearchResult(null, RateLimited: true, HadTransientError: true);
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (FilmAffinityScoreParser.IsCloudflareBlocked(response, body))
                    {
                        _logger.LogWarning(
                            "Cloudflare protection blocked FilmAffinity search request {RequestUri} (HTTP {StatusCode}).",
                            requestUri,
                            (int)response.StatusCode);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "FilmAffinity search endpoint returned HTTP {StatusCode} for {QueryTitle} ({RequestUri})",
                            (int)response.StatusCode,
                            queryTitle,
                            requestUri);
                    }

                    return new FilmAffinitySearchResult(null, RateLimited: false, HadTransientError: true);
                }

                var candidates = FilmAffinitySearchParser.ParseCandidates(body, response.RequestMessage?.RequestUri);
                if (candidates.Count == 0 && FilmAffinityScoreParser.IsCloudflareBlocked(response, body))
                {
                    _logger.LogWarning(
                        "Cloudflare protection blocked FilmAffinity search request {RequestUri} (HTTP {StatusCode}).",
                        requestUri,
                        (int)response.StatusCode);
                    return new FilmAffinitySearchResult(null, RateLimited: false, HadTransientError: true);
                }

                var matchedId = FilmAffinitySearchParser.SelectBestMatch(
                    candidates,
                    primaryTitle,
                    originalTitle,
                    isSeries,
                    productionYear);

                return new FilmAffinitySearchResult(matchedId, RateLimited: false, HadTransientError: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to search FilmAffinity for {QueryTitle} ({RequestUri})",
                    queryTitle,
                    requestUri);
                return new FilmAffinitySearchResult(null, RateLimited: false, HadTransientError: true);
            }
        }

        return new FilmAffinitySearchResult(null, RateLimited: true, HadTransientError: true);
    }

    private static bool MeetsMinVotes(FilmAffinityScore score, int minVotes)
    {
        return minVotes <= 0 || !score.VoteCount.HasValue || score.VoteCount.Value >= minVotes;
    }

    private static TimeSpan GetRateLimitDelay(HttpResponseMessage response, int throttleMs, int attempt)
    {
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            TimeSpan? headerDelay = retryAfter.Delta;
            if (!headerDelay.HasValue && retryAfter.Date.HasValue)
            {
                headerDelay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            }

            if (headerDelay.HasValue && headerDelay.Value > TimeSpan.Zero)
            {
                return headerDelay.Value > TimeSpan.FromSeconds(120)
                    ? TimeSpan.FromSeconds(120)
                    : headerDelay.Value;
            }
        }

        var baseMs = Math.Max(throttleMs * 4, 15000);
        var backoffMs = Math.Min(baseMs * (attempt + 1), 60000);
        return TimeSpan.FromMilliseconds(backoffMs);
    }

    private bool TryGetRemainingCooldown(out TimeSpan remaining)
    {
        var cooldownUntilTicks = Interlocked.Read(ref _rateLimitCooldownUntilTicks);
        if (cooldownUntilTicks <= 0)
        {
            remaining = TimeSpan.Zero;
            return false;
        }

        var now = _utcNowProvider();
        var cooldownUntilUtc = new DateTime(cooldownUntilTicks, DateTimeKind.Utc);
        if (cooldownUntilUtc > now)
        {
            remaining = cooldownUntilUtc - now;
            return true;
        }

        Interlocked.CompareExchange(ref _rateLimitCooldownUntilTicks, 0, cooldownUntilTicks);
        remaining = TimeSpan.Zero;
        return false;
    }

    private void ActivateRateLimitCooldown(TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero)
        {
            return;
        }

        var targetTicks = (_utcNowProvider() + cooldown).Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _rateLimitCooldownUntilTicks);
            if (current >= targetTicks)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _rateLimitCooldownUntilTicks, targetTicks, current) != current);
    }

    private async Task ApplyBackoffAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
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

    private async Task WaitForThrottleAsync(int throttleMs, CancellationToken cancellationToken)
    {
        await _throttleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            if (_nextAllowedRequestUtc > now)
            {
                await Task.Delay(_nextAllowedRequestUtc - now, cancellationToken).ConfigureAwait(false);
            }

            _nextAllowedRequestUtc = throttleMs > 0
                ? DateTime.UtcNow + TimeSpan.FromMilliseconds(throttleMs)
                : DateTime.UtcNow;
        }
        finally
        {
            _throttleLock.Release();
        }
    }
}
