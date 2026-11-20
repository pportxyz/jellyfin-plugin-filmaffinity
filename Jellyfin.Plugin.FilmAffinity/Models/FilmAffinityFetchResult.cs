namespace Jellyfin.Plugin.FilmAffinity.Models;

/// <summary>
/// Represents the outcome of a FilmAffinity score fetch attempt.
/// </summary>
/// <param name="Score">The fetched score, if available and meeting vote thresholds.</param>
/// <param name="RateLimited"><c>true</c> if the request failed with HTTP 429 after exhausting retries.</param>
public sealed record FilmAffinityFetchResult(FilmAffinityScore? Score, bool RateLimited);
