namespace Jellyfin.Plugin.FilmAffinity.Models;

/// <summary>
/// Represents the outcome of a Wikidata P480 FilmAffinity ID resolution attempt.
/// </summary>
/// <param name="FilmAffinityId">The resolved FilmAffinity ID, if found.</param>
/// <param name="HadTransientError"><c>true</c> if the Wikidata request failed due to an HTTP or network error rather than a confirmed missing P480 claim.</param>
public sealed record WikidataResolutionResult(string? FilmAffinityId, bool HadTransientError);
