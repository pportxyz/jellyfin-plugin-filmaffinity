using System;

namespace Jellyfin.Plugin.FilmAffinity.Models;

/// <summary>
/// Persisted refresh metadata for a single Jellyfin library item.
/// </summary>
/// <param name="FilmAffinityId">The resolved FilmAffinity ID at the time of the last check, if any.</param>
/// <param name="LastCheckedUtc">The UTC timestamp when this item was last checked.</param>
/// <param name="LastRating">The last fetched FilmAffinity rating (0.0 to 10.0), if available.</param>
/// <param name="LastVoteCount">The last observed vote count, if reported.</param>
public sealed record RefreshStateEntry(
    string? FilmAffinityId,
    DateTime LastCheckedUtc,
    float? LastRating,
    int? LastVoteCount);
