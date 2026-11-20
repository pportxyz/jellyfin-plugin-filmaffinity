using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FilmAffinity.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FilmAffinity.Providers;

/// <summary>
/// Remote metadata provider that populates FilmAffinity <c>CommunityRating</c> (and optional <c>CriticRating</c>) for TV Series.
/// </summary>
public class FilmAffinitySeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    private readonly FilmAffinityRatingService _ratingService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FilmAffinitySeriesProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmAffinitySeriesProvider"/> class.
    /// </summary>
    /// <param name="ratingService">The FilmAffinity rating service.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public FilmAffinitySeriesProvider(
        FilmAffinityRatingService ratingService,
        IHttpClientFactory httpClientFactory,
        ILogger<FilmAffinitySeriesProvider> logger)
    {
        _ratingService = ratingService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Plugin.ProviderDisplayName;

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var result = new MetadataResult<Series>();
        var (filmAffinityId, score) = await _ratingService
            .LookupAsync(info.ProviderIds, isSeries: true, info.Year, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(filmAffinityId))
        {
            return result;
        }

        var series = new Series();
        series.SetProviderId(Plugin.ProviderKey, filmAffinityId);

        if (score is not null)
        {
            series.CommunityRating = score.Rating;
            if (Plugin.Instance?.Configuration.SetCriticRating == true)
            {
                series.CriticRating = FilmAffinityRatingService.ToCriticRating(score.Rating);
            }

            _logger.LogDebug(
                "Applied FilmAffinity rating {Rating} (ID {FilmAffinityId}) to series {SeriesName}",
                score.Rating,
                filmAffinityId,
                info.Name);
        }

        result.Item = series;
        result.HasMetadata = true;
        return result;
    }

    /// <inheritdoc />
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<RemoteSearchResult>>([]);
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _httpClientFactory.CreateClient(FilmAffinityScoreClient.HttpClientName)
            .GetAsync(new Uri(url), cancellationToken);
    }
}
