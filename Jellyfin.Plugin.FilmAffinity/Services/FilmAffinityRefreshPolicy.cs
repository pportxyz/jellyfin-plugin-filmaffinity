using System;
using System.Collections.Generic;
using Jellyfin.Plugin.FilmAffinity.Configuration;
using Jellyfin.Plugin.FilmAffinity.Models;

namespace Jellyfin.Plugin.FilmAffinity.Services;

/// <summary>
/// Evaluates whether a library item is due for a FilmAffinity rating check and assigns its sync priority.
/// </summary>
public static class FilmAffinityRefreshPolicy
{
    internal const int PriorityUnmatched = 0;
    internal const int PriorityMatchedUnrated = 1;
    internal const int PriorityRecentRelease = 2;
    internal const int PriorityMidTermRelease = 3;
    internal const int PriorityContinuingSeries = 4;
    internal const int PriorityRefreshAll = 5;

    internal const int LowVoteThreshold = 500;

    internal static readonly TimeSpan UnmatchedRetryInterval = TimeSpan.FromDays(7);
    internal static readonly TimeSpan UnratedRecentInterval = TimeSpan.FromDays(3);
    internal static readonly TimeSpan UnratedOlderInterval = TimeSpan.FromDays(14);
    internal static readonly TimeSpan RecentLowVoteInterval = TimeSpan.FromDays(3);
    internal static readonly TimeSpan RecentReleaseInterval = TimeSpan.FromDays(7);
    internal static readonly TimeSpan MidTermReleaseInterval = TimeSpan.FromDays(30);
    internal static readonly TimeSpan ContinuingSeriesInterval = TimeSpan.FromDays(30);

    internal static readonly TimeSpan RecentReleaseWindow = TimeSpan.FromDays(90);
    internal static readonly TimeSpan MidTermReleaseWindow = TimeSpan.FromDays(365);

    /// <summary>
    /// Evaluates whether an item should be processed in the current sync run.
    /// </summary>
    /// <param name="providerIds">The item's provider IDs.</param>
    /// <param name="communityRating">The item's current Jellyfin <c>CommunityRating</c>.</param>
    /// <param name="premiereDate">The item's premiere date, if available.</param>
    /// <param name="productionYear">The item's production year, if available.</param>
    /// <param name="isContinuingSeries"><c>true</c> if the item is an ongoing TV series.</param>
    /// <param name="state">The item's persisted refresh state, if any.</param>
    /// <param name="syncMode">The configured <see cref="SyncMode"/>.</param>
    /// <param name="utcNow">The current UTC timestamp.</param>
    /// <returns>A <see cref="RefreshDecision"/> describing eligibility, priority, and reason.</returns>
    public static RefreshDecision Evaluate(
        IReadOnlyDictionary<string, string>? providerIds,
        float? communityRating,
        DateTime? premiereDate,
        int? productionYear,
        bool isContinuingSeries,
        RefreshStateEntry? state,
        SyncMode syncMode,
        DateTime utcNow)
    {
        var hasFilmAffinityId = providerIds is not null
            && providerIds.TryGetValue(Plugin.ProviderKey, out var existingId)
            && !string.IsNullOrWhiteSpace(existingId);

        if (syncMode == SyncMode.OnlyNewAndUnmatched)
        {
            return hasFilmAffinityId
                ? RefreshDecision.Skip("Already matched (OnlyNewAndUnmatched)")
                : RefreshDecision.Process(PriorityUnmatched, "Unmatched item");
        }

        if (syncMode == SyncMode.RefreshAll)
        {
            var priority = !hasFilmAffinityId
                ? PriorityUnmatched
                : !communityRating.HasValue
                    ? PriorityMatchedUnrated
                    : PriorityRefreshAll;
            return RefreshDecision.Process(priority, "RefreshAll");
        }

        // SyncMode.SmartRefresh
        if (!hasFilmAffinityId)
        {
            return IsDue(state, UnmatchedRetryInterval, utcNow)
                ? RefreshDecision.Process(PriorityUnmatched, "Unmatched item")
                : RefreshDecision.Skip("Unmatched item checked recently");
        }

        var releaseDateUtc = ResolveReleaseDateUtc(premiereDate, productionYear);
        var releaseAge = releaseDateUtc.HasValue ? utcNow - releaseDateUtc.Value : (TimeSpan?)null;

        if (!communityRating.HasValue)
        {
            var unratedInterval = releaseAge.HasValue && releaseAge.Value <= MidTermReleaseWindow
                ? UnratedRecentInterval
                : UnratedOlderInterval;

            return IsDue(state, unratedInterval, utcNow)
                ? RefreshDecision.Process(PriorityMatchedUnrated, "Matched item without rating")
                : RefreshDecision.Skip("Unrated item checked recently");
        }

        if (releaseAge.HasValue && releaseAge.Value <= RecentReleaseWindow)
        {
            var interval = state?.LastVoteCount is int votes && votes < LowVoteThreshold
                ? RecentLowVoteInterval
                : RecentReleaseInterval;

            return IsDue(state, interval, utcNow)
                ? RefreshDecision.Process(PriorityRecentRelease, "Recent release (0-90 days)")
                : RefreshDecision.Skip("Recent release checked recently");
        }

        if (releaseAge.HasValue && releaseAge.Value <= MidTermReleaseWindow)
        {
            return IsDue(state, MidTermReleaseInterval, utcNow)
                ? RefreshDecision.Process(PriorityMidTermRelease, "Mid-term release (91-365 days)")
                : RefreshDecision.Skip("Mid-term release checked recently");
        }

        if (isContinuingSeries)
        {
            return IsDue(state, ContinuingSeriesInterval, utcNow)
                ? RefreshDecision.Process(PriorityContinuingSeries, "Continuing TV series")
                : RefreshDecision.Skip("Continuing TV series checked recently");
        }

        return RefreshDecision.Skip("Stabilized catalog item");
    }

    /// <summary>
    /// Resolves an approximate UTC release date from <paramref name="premiereDate"/> or <paramref name="productionYear"/>.
    /// </summary>
    /// <param name="premiereDate">The item's premiere date, if available.</param>
    /// <param name="productionYear">The item's production year, if available.</param>
    /// <returns>A UTC <see cref="DateTime"/> if either value is present; otherwise <c>null</c>.</returns>
    public static DateTime? ResolveReleaseDateUtc(DateTime? premiereDate, int? productionYear)
    {
        if (premiereDate.HasValue)
        {
            return premiereDate.Value.Kind == DateTimeKind.Utc
                ? premiereDate.Value
                : premiereDate.Value.ToUniversalTime();
        }

        if (productionYear.HasValue && productionYear.Value is >= 1888 and <= 9999)
        {
            return new DateTime(productionYear.Value, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return null;
    }

    private static bool IsDue(RefreshStateEntry? state, TimeSpan interval, DateTime utcNow)
    {
        if (state is null)
        {
            return true;
        }

        return (utcNow - state.LastCheckedUtc) >= interval;
    }
}
