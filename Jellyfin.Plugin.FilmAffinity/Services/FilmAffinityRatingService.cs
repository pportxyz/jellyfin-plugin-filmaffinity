using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FilmAffinity.Models;

namespace Jellyfin.Plugin.FilmAffinity.Services;

/// <summary>
/// High-level facade combining Wikidata P480 ID resolution and FilmAffinity score retrieval.
/// </summary>
public class FilmAffinityRatingService
{
    private readonly WikidataFilmAffinityResolver _resolver;
    private readonly FilmAffinityScoreClient _scoreClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmAffinityRatingService"/> class.
    /// </summary>
    /// <param name="resolver">The Wikidata FilmAffinity ID resolver.</param>
    /// <param name="scoreClient">The FilmAffinity score HTTP client.</param>
    public FilmAffinityRatingService(
        WikidataFilmAffinityResolver resolver,
        FilmAffinityScoreClient scoreClient)
    {
        _resolver = resolver;
        _scoreClient = scoreClient;
    }

    /// <summary>
    /// Resolves the FilmAffinity ID and fetches the corresponding score for a movie or series.
    /// </summary>
    /// <param name="providerIds">Existing provider IDs on the item (IMDb, TMDb, FilmAffinity).</param>
    /// <param name="isSeries"><c>true</c> for TV series; <c>false</c> for movies.</param>
    /// <param name="productionYear">Optional production year.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple containing the resolved FilmAffinity ID (if any) and the fetched score (if any).</returns>
    public async Task<(string? FilmAffinityId, FilmAffinityScore? Score)> LookupAsync(
        IReadOnlyDictionary<string, string>? providerIds,
        bool isSeries,
        int? productionYear,
        CancellationToken cancellationToken)
    {
        var (filmAffinityId, score, _) = await LookupWithStatusAsync(providerIds, isSeries, productionYear, cancellationToken)
            .ConfigureAwait(false);
        return (filmAffinityId, score);
    }

    /// <summary>
    /// Resolves the FilmAffinity ID and fetches the corresponding score along with rate-limit status.
    /// </summary>
    /// <param name="providerIds">Existing provider IDs on the item (IMDb, TMDb, FilmAffinity).</param>
    /// <param name="isSeries"><c>true</c> for TV series; <c>false</c> for movies.</param>
    /// <param name="productionYear">Optional production year.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple containing the resolved FilmAffinity ID, fetched score, and whether HTTP 429 exhausted retries.</returns>
    public async Task<(string? FilmAffinityId, FilmAffinityScore? Score, bool RateLimited)> LookupWithStatusAsync(
        IReadOnlyDictionary<string, string>? providerIds,
        bool isSeries,
        int? productionYear,
        CancellationToken cancellationToken)
    {
        if (_scoreClient.IsInRateLimitCooldown)
        {
            return (null, null, true);
        }

        var filmAffinityId = await _resolver.ResolveAsync(providerIds, isSeries, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(filmAffinityId))
        {
            return (null, null, false);
        }

        var fetchResult = await _scoreClient.FetchScoreAsync(filmAffinityId, productionYear, cancellationToken)
            .ConfigureAwait(false);

        return (filmAffinityId, fetchResult.Score, fetchResult.RateLimited);
    }

    /// <summary>
    /// Converts a FilmAffinity 0-10 score to Jellyfin's 0-100 <c>CriticRating</c> scale.
    /// </summary>
    /// <param name="rating">The 0-10 rating.</param>
    /// <returns>The scaled 0-100 critic rating.</returns>
    public static float ToCriticRating(float rating)
    {
        return MathF.Round(rating * 10f, 1, MidpointRounding.AwayFromZero);
    }
}
