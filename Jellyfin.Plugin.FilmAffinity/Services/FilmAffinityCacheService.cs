using System;
using Jellyfin.Plugin.FilmAffinity.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.FilmAffinity.Services;

/// <summary>
/// In-memory TTL cache for Wikidata FilmAffinity ID mappings and FilmAffinity scores.
/// </summary>
public class FilmAffinityCacheService
{
    private static readonly TimeSpan RecentItemTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan StandardItemTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeLookupTtl = TimeSpan.FromHours(6);

    private readonly IMemoryCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmAffinityCacheService"/> class.
    /// </summary>
    /// <param name="cache">The Jellyfin memory cache instance.</param>
    public FilmAffinityCacheService(IMemoryCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Attempts to retrieve a cached FilmAffinity ID for an external lookup key.
    /// </summary>
    /// <param name="lookupKey">The lookup key (e.g. <c>imdb:tt0068646</c>).</param>
    /// <param name="filmAffinityId">The cached FilmAffinity ID, or <c>null</c> if cached as not found.</param>
    /// <returns><c>true</c> if the lookup key was present in the cache; otherwise <c>false</c>.</returns>
    public bool TryGetFilmAffinityId(string lookupKey, out string? filmAffinityId)
    {
        return _cache.TryGetValue(BuildIdKey(lookupKey), out filmAffinityId);
    }

    /// <summary>
    /// Caches a FilmAffinity ID lookup result (including negative results).
    /// </summary>
    /// <param name="lookupKey">The lookup key.</param>
    /// <param name="filmAffinityId">The resolved FilmAffinity ID, or <c>null</c> if not found.</param>
    public void SetFilmAffinityId(string lookupKey, string? filmAffinityId)
    {
        var ttl = string.IsNullOrEmpty(filmAffinityId) ? NegativeLookupTtl : StandardItemTtl;
        _cache.Set(BuildIdKey(lookupKey), filmAffinityId, ttl);
    }

    /// <summary>
    /// Attempts to retrieve a cached score for a FilmAffinity ID.
    /// </summary>
    /// <param name="filmAffinityId">The FilmAffinity ID.</param>
    /// <param name="score">The cached score, if present.</param>
    /// <returns><c>true</c> if a cached entry exists; otherwise <c>false</c>.</returns>
    public bool TryGetScore(string filmAffinityId, out FilmAffinityScore? score)
    {
        return _cache.TryGetValue(BuildScoreKey(filmAffinityId), out score);
    }

    /// <summary>
    /// Caches a FilmAffinity score using a shorter TTL (6h) for recent releases (&lt; 2 years old) and 24h for older items.
    /// </summary>
    /// <param name="filmAffinityId">The FilmAffinity ID.</param>
    /// <param name="score">The score to cache.</param>
    /// <param name="productionYear">Optional production year of the movie or series.</param>
    public void SetScore(string filmAffinityId, FilmAffinityScore score, int? productionYear)
    {
        var currentYear = DateTime.UtcNow.Year;
        var isRecent = productionYear.HasValue && (currentYear - productionYear.Value) < 2;
        var ttl = isRecent ? RecentItemTtl : StandardItemTtl;
        _cache.Set(BuildScoreKey(filmAffinityId), score, ttl);
    }

    private static string BuildIdKey(string lookupKey) => "fa:id:" + lookupKey;

    private static string BuildScoreKey(string filmAffinityId) => "fa:score:" + filmAffinityId;
}
