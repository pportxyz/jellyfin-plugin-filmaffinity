using System;
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

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FilmAffinityCacheService _cache;
    private readonly ILogger<FilmAffinityScoreClient> _logger;
    private readonly Func<PluginConfiguration> _configurationProvider;
    private readonly SemaphoreSlim _throttleLock = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

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
        : this(httpClientFactory, cache, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
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
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
        _configurationProvider = configurationProvider;
    }

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
        if (string.IsNullOrWhiteSpace(filmAffinityId))
        {
            return null;
        }

        var config = _configurationProvider();

        if (_cache.TryGetScore(filmAffinityId, out var cachedScore) && cachedScore is not null)
        {
            return MeetsMinVotes(cachedScore, config.MinVotes) ? cachedScore : null;
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
            return null;
        }

        await WaitForThrottleAsync(config.ThrottleMs, cancellationToken).ConfigureAwait(false);

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

                return null;
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

                return null;
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
                return null;
            }

            return score;
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
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _throttleLock.Dispose();
    }

    private static bool MeetsMinVotes(FilmAffinityScore score, int minVotes)
    {
        return minVotes <= 0 || !score.VoteCount.HasValue || score.VoteCount.Value >= minVotes;
    }

    private async Task WaitForThrottleAsync(int throttleMs, CancellationToken cancellationToken)
    {
        if (throttleMs <= 0)
        {
            return;
        }

        await _throttleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequestUtc;
            var minDelay = TimeSpan.FromMilliseconds(throttleMs);
            if (elapsed < minDelay)
            {
                await Task.Delay(minDelay - elapsed, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _throttleLock.Release();
        }
    }
}
