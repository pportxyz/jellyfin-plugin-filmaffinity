namespace Jellyfin.Plugin.FilmAffinity.Models;

/// <summary>
/// Represents a FilmAffinity rating fetched for a movie or TV series.
/// </summary>
/// <param name="FilmAffinityId">The numeric FilmAffinity identifier (Wikidata P480).</param>
/// <param name="Rating">The community rating on a 0.0 to 10.0 scale.</param>
/// <param name="VoteCount">The number of user votes, if available.</param>
public sealed record FilmAffinityScore(string FilmAffinityId, float Rating, int? VoteCount);
