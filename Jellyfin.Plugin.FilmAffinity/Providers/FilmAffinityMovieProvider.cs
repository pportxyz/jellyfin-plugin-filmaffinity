using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FilmAffinity.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FilmAffinity.Providers;

/// <summary>
/// Remote metadata provider that populates FilmAffinity <c>CommunityRating</c> (and optional <c>CriticRating</c>) for Movies.
/// </summary>
public class FilmAffinityMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
{
    private readonly FilmAffinityRatingService _ratingService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FilmAffinityMovieProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmAffinityMovieProvider"/> class.
    /// </summary>
    /// <param name="ratingService">The FilmAffinity rating service.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public FilmAffinityMovieProvider(
        FilmAffinityRatingService ratingService,
        IHttpClientFactory httpClientFactory,
        ILogger<FilmAffinityMovieProvider> logger)
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
    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var result = new MetadataResult<Movie>();
        var lookup = await _ratingService
            .LookupDetailedAsync(info.ProviderIds, isSeries: false, info.Year, info.Name, info.OriginalTitle, cancellationToken)
            .ConfigureAwait(false);

        if (lookup.RateLimited || string.IsNullOrEmpty(lookup.FilmAffinityId))
        {
            return result;
        }

        var filmAffinityId = lookup.FilmAffinityId;
        var score = lookup.Score;

        var movie = new Movie();
        movie.SetProviderId(Plugin.ProviderKey, filmAffinityId);

        if (score is not null)
        {
            movie.CommunityRating = score.Rating;
            if (Plugin.Instance?.Configuration.SetCriticRating == true)
            {
                movie.CriticRating = FilmAffinityRatingService.ToCriticRating(score.Rating);
            }

            _logger.LogDebug(
                "Applied FilmAffinity rating {Rating} (ID {FilmAffinityId}) to movie {MovieName}",
                score.Rating,
                filmAffinityId,
                info.Name);
        }

        result.Item = movie;
        result.HasMetadata = true;
        return result;
    }

    /// <inheritdoc />
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
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
