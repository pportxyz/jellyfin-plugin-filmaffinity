namespace Jellyfin.Plugin.FilmAffinity.Configuration;

/// <summary>
/// Controls which library items the scheduled sync task processes.
/// </summary>
public enum SyncMode
{
    /// <summary>
    /// Only process newly added or previously unmatched items (skips items that already have a FilmAffinity ID).
    /// </summary>
    OnlyNewAndUnmatched = 0,

    /// <summary>
    /// Refresh FilmAffinity ratings for all items in enabled libraries.
    /// </summary>
    RefreshAll = 1
}
