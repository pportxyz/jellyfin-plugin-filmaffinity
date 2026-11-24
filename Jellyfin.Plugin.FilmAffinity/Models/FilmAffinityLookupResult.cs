namespace Jellyfin.Plugin.FilmAffinity.Models;

/// <summary>
/// Represents the full outcome of resolving a FilmAffinity ID and fetching its rating score.
/// </summary>
/// <param name="FilmAffinityId">The resolved FilmAffinity ID, if any.</param>
/// <param name="Score">The fetched FilmAffinity score, if available and meeting vote thresholds.</param>
/// <param name="RateLimited"><c>true</c> if FilmAffinity rate-limiting (HTTP 429) or active cooldown prevented completion.</param>
/// <param name="HadTransientError"><c>true</c> if resolution failed due to a transient HTTP, Cloudflare, or network error.</param>
public sealed record FilmAffinityLookupResult(
    string? FilmAffinityId,
    FilmAffinityScore? Score,
    bool RateLimited,
    bool HadTransientError);
