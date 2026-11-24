namespace Jellyfin.Plugin.FilmAffinity.Models;

/// <summary>
/// Represents the outcome of a FilmAffinity title search attempt.
/// </summary>
/// <param name="FilmAffinityId">The matched FilmAffinity ID, if an unambiguous match was found.</param>
/// <param name="RateLimited"><c>true</c> if the request hit HTTP 429 after exhausting retries or while in rate-limit cooldown.</param>
/// <param name="HadTransientError"><c>true</c> if the search failed due to an HTTP, Cloudflare, or network error.</param>
public sealed record FilmAffinitySearchResult(string? FilmAffinityId, bool RateLimited, bool HadTransientError);
