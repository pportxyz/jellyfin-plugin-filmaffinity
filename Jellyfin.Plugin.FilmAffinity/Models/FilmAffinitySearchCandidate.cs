namespace Jellyfin.Plugin.FilmAffinity.Models;

/// <summary>
/// Represents a single candidate entry parsed from FilmAffinity search HTML.
/// </summary>
/// <param name="Id">The numeric FilmAffinity ID.</param>
/// <param name="Title">The parsed display title.</param>
/// <param name="Year">The parsed release year, if available.</param>
/// <param name="IsSeries"><c>true</c> if the candidate is tagged as a TV series or miniseries.</param>
/// <param name="IsShortFilm"><c>true</c> if the candidate is tagged as a short film.</param>
public sealed record FilmAffinitySearchCandidate(
    string Id,
    string Title,
    int? Year,
    bool IsSeries,
    bool IsShortFilm);
