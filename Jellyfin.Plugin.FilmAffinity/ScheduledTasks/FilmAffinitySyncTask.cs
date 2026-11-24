using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FilmAffinity.Configuration;
using Jellyfin.Plugin.FilmAffinity.Models;
using Jellyfin.Plugin.FilmAffinity.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FilmAffinity.ScheduledTasks;

/// <summary>
/// Scheduled task that updates FilmAffinity ratings across Movies and TV Series in the Jellyfin library.
/// </summary>
public class FilmAffinitySyncTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly FilmAffinityRatingService _ratingService;
    private readonly FilmAffinityRefreshStateStore _stateStore;
    private readonly ILogger<FilmAffinitySyncTask> _logger;
    private readonly Func<PluginConfiguration> _configurationProvider;
    private readonly Func<DateTime> _utcNowProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmAffinitySyncTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="ratingService">The FilmAffinity rating service.</param>
    /// <param name="stateStore">The persistent refresh state store.</param>
    /// <param name="logger">The logger.</param>
    public FilmAffinitySyncTask(
        ILibraryManager libraryManager,
        FilmAffinityRatingService ratingService,
        FilmAffinityRefreshStateStore stateStore,
        ILogger<FilmAffinitySyncTask> logger)
        : this(
            libraryManager,
            ratingService,
            stateStore,
            logger,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            () => DateTime.UtcNow)
    {
    }

    internal FilmAffinitySyncTask(
        ILibraryManager libraryManager,
        FilmAffinityRatingService ratingService,
        FilmAffinityRefreshStateStore stateStore,
        ILogger<FilmAffinitySyncTask> logger,
        Func<PluginConfiguration> configurationProvider,
        Func<DateTime>? utcNowProvider = null)
    {
        _libraryManager = libraryManager;
        _ratingService = ratingService;
        _stateStore = stateStore;
        _logger = logger;
        _configurationProvider = configurationProvider;
        _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc />
    public string Name => "Update FilmAffinity Ratings";

    /// <inheritdoc />
    public string Key => "FilmAffinityRatingsSync";

    /// <inheritdoc />
    public string Description => "Resolves FilmAffinity IDs via Wikidata (P480) and updates CommunityRating for movies and TV series.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var allItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            Recursive = true
        });

        var config = _configurationProvider();
        var setCriticRating = config.SetCriticRating;
        var syncMode = config.SyncMode;
        var maxItemsPerRun = Math.Max(0, config.MaxItemsPerSyncRun);
        var utcNow = _utcNowProvider();

        var enabledItems = allItems
            .Where(item => IsEnabledForLibrary(_libraryManager.GetLibraryOptions(item), item.GetType().Name))
            .ToList();

        var stateEntries = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var activeItemIds = enabledItems.Select(item => item.Id).ToHashSet();
        var prunedCount = FilmAffinityRefreshStateStore.PruneDeletedItems(stateEntries, activeItemIds);
        var stateDirty = prunedCount > 0;

        var candidates = SelectCandidates(enabledItems, stateEntries, syncMode, utcNow);
        var totalDueCount = candidates.Count;

        if (maxItemsPerRun > 0 && candidates.Count > maxItemsPerRun)
        {
            candidates = candidates.Take(maxItemsPerRun).ToList();
            _logger.LogInformation(
                "Capping FilmAffinity sync run to {MaxItems} of {TotalDue} due items (SyncMode={SyncMode}); remaining {DeferredCount} will be processed in subsequent runs.",
                maxItemsPerRun,
                totalDueCount,
                syncMode,
                totalDueCount - maxItemsPerRun);
        }

        if (candidates.Count == 0)
        {
            if (stateDirty)
            {
                await _stateStore.SaveAsync(stateEntries, cancellationToken).ConfigureAwait(false);
            }

            if (enabledItems.Count == 0)
            {
                _logger.LogInformation(
                    "No movies or series found in libraries with {ProviderName} metadata fetcher enabled (TotalItems={TotalItems}).",
                    Plugin.ProviderDisplayName,
                    allItems.Count);
            }
            else
            {
                var unmatchedInCooldown = enabledItems.Count(item =>
                    !item.ProviderIds.TryGetValue(Plugin.ProviderKey, out var id) || string.IsNullOrWhiteSpace(id));
                var matchedNotDue = enabledItems.Count - unmatchedInCooldown;

                _logger.LogInformation(
                    "No movies or series due for sync (SyncMode={SyncMode}, EnabledItems={EnabledItems}, MatchedNotDue={MatchedNotDue}, UnmatchedInCooldown={UnmatchedInCooldown}) in libraries with {ProviderName} metadata fetcher enabled.",
                    syncMode,
                    enabledItems.Count,
                    matchedNotDue,
                    unmatchedInCooldown,
                    Plugin.ProviderDisplayName);
            }

            progress.Report(100);
            return;
        }

        var updatedCount = 0;
        var processedCount = 0;

        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = candidates[i];
                var isSeries = item is Series;

                var lookup = await _ratingService
                    .LookupDetailedAsync(item.ProviderIds, isSeries, item.ProductionYear, item.Name, item.OriginalTitle, cancellationToken)
                    .ConfigureAwait(false);
                var filmAffinityId = lookup.FilmAffinityId;
                var score = lookup.Score;

                if (lookup.RateLimited)
                {
                    if (!string.IsNullOrEmpty(filmAffinityId)
                        && (!item.ProviderIds.TryGetValue(Plugin.ProviderKey, out var currentRateLimitedId)
                            || !string.Equals(currentRateLimitedId, filmAffinityId, StringComparison.Ordinal)))
                    {
                        item.SetProviderId(Plugin.ProviderKey, filmAffinityId);
                        await _libraryManager
                            .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                            .ConfigureAwait(false);
                        updatedCount++;
                    }

                    _logger.LogWarning(
                        "Stopping FilmAffinity sync early after HTTP 429 rate-limit retries were exhausted ({ProcessedCount}/{TotalCount} processed). Re-run later once the cooldown window resets.",
                        processedCount,
                        candidates.Count);
                    break;
                }

                processedCount++;
                var checkTimeUtc = _utcNowProvider();
                var effectiveId = !string.IsNullOrEmpty(filmAffinityId)
                    ? filmAffinityId
                    : item.ProviderIds.TryGetValue(Plugin.ProviderKey, out var existingId) ? existingId : null;

                if (string.IsNullOrEmpty(effectiveId) && lookup.HadTransientError)
                {
                    _logger.LogWarning(
                        "Skipping refresh-state cooldown update for {ItemName} because ID resolution encountered a transient error; it will be retried on the next sync run.",
                        item.Name);
                }
                else
                {
                    stateEntries[item.Id] = new RefreshStateEntry(
                        effectiveId,
                        checkTimeUtc,
                        score?.Rating,
                        score?.VoteCount);
                    stateDirty = true;
                }

                var changed = false;
                var previousRating = item.CommunityRating;

                if (!string.IsNullOrEmpty(filmAffinityId)
                    && (!item.ProviderIds.TryGetValue(Plugin.ProviderKey, out var currentId)
                        || !string.Equals(currentId, filmAffinityId, StringComparison.Ordinal)))
                {
                    item.SetProviderId(Plugin.ProviderKey, filmAffinityId);
                    changed = true;
                }

                if (score is not null)
                {
                    if (!item.CommunityRating.HasValue || MathF.Abs(item.CommunityRating.Value - score.Rating) > 0.001f)
                    {
                        item.CommunityRating = score.Rating;
                        changed = true;
                    }

                    if (setCriticRating)
                    {
                        var criticRating = FilmAffinityRatingService.ToCriticRating(score.Rating);
                        if (!item.CriticRating.HasValue || MathF.Abs(item.CriticRating.Value - criticRating) > 0.01f)
                        {
                            item.CriticRating = criticRating;
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    await _libraryManager
                        .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                        .ConfigureAwait(false);
                    updatedCount++;

                    if (score is not null)
                    {
                        if (previousRating.HasValue && MathF.Abs(previousRating.Value - score.Rating) > 0.001f)
                        {
                            _logger.LogInformation(
                                "Updated {ItemName} FilmAffinity rating: {OldRating} -> {NewRating} ({VoteCount} votes, ID {FilmAffinityId})",
                                item.Name,
                                previousRating.Value,
                                score.Rating,
                                score.VoteCount,
                                filmAffinityId);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Updated {ItemName} with FilmAffinity rating {Rating} ({VoteCount} votes, ID {FilmAffinityId})",
                                item.Name,
                                score.Rating,
                                score.VoteCount,
                                filmAffinityId);
                        }
                    }
                }

                progress.Report((double)(i + 1) / candidates.Count * 100.0);
            }
        }
        finally
        {
            if (stateDirty)
            {
                await _stateStore.SaveAsync(stateEntries, CancellationToken.None).ConfigureAwait(false);
            }
        }

        _logger.LogInformation(
            "FilmAffinity sync completed: updated {UpdatedCount} of {ProcessedCount} processed items ({TotalDue} due, SyncMode={SyncMode})",
            updatedCount,
            processedCount,
            totalDueCount,
            syncMode);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        ];
    }

    /// <summary>
    /// Determines whether the FilmAffinity metadata fetcher is enabled in the given library for the specified item type.
    /// </summary>
    /// <param name="libraryOptions">The library options.</param>
    /// <param name="itemTypeName">The item type name (e.g. <c>Movie</c> or <c>Series</c>).</param>
    /// <returns><c>true</c> if enabled; otherwise <c>false</c>.</returns>
    internal static bool IsEnabledForLibrary(LibraryOptions? libraryOptions, string itemTypeName)
    {
        var typeOptions = libraryOptions?.GetTypeOptions(itemTypeName);
        return typeOptions?.MetadataFetchers is { Length: > 0 } fetchers
            && Array.Exists(fetchers, f => string.Equals(f, Plugin.ProviderDisplayName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Determines whether an item should be processed under the configured <see cref="SyncMode"/>.
    /// </summary>
    /// <param name="providerIds">The item's provider IDs.</param>
    /// <param name="syncMode">The configured sync mode.</param>
    /// <returns><c>true</c> if the item should be synced; otherwise <c>false</c>.</returns>
    internal static bool ShouldProcessItem(IReadOnlyDictionary<string, string>? providerIds, SyncMode syncMode)
    {
        return FilmAffinityRefreshPolicy.Evaluate(
            providerIds,
            communityRating: null,
            premiereDate: null,
            productionYear: null,
            isContinuingSeries: false,
            state: null,
            syncMode,
            DateTime.UtcNow).ShouldProcess;
    }

    /// <summary>
    /// Filters and prioritizes items due for processing in the current sync run.
    /// </summary>
    /// <param name="items">Items in libraries with the FilmAffinity fetcher enabled.</param>
    /// <param name="stateEntries">Persisted per-item refresh state.</param>
    /// <param name="syncMode">The configured sync mode.</param>
    /// <param name="utcNow">The current UTC timestamp.</param>
    /// <returns>Prioritized list of items to process.</returns>
    internal static List<BaseItem> SelectCandidates(
        IEnumerable<BaseItem> items,
        IReadOnlyDictionary<Guid, RefreshStateEntry> stateEntries,
        SyncMode syncMode,
        DateTime utcNow)
    {
        return items
            .Select(item =>
            {
                stateEntries.TryGetValue(item.Id, out var state);
                var isContinuingSeries = item is Series { Status: SeriesStatus.Continuing };
                var decision = FilmAffinityRefreshPolicy.Evaluate(
                    item.ProviderIds,
                    item.CommunityRating,
                    item.PremiereDate,
                    item.ProductionYear,
                    isContinuingSeries,
                    state,
                    syncMode,
                    utcNow);
                var releaseDate = FilmAffinityRefreshPolicy.ResolveReleaseDateUtc(item.PremiereDate, item.ProductionYear);

                return (Item: item, Decision: decision, LastCheckedUtc: state?.LastCheckedUtc, ReleaseDate: releaseDate);
            })
            .Where(x => x.Decision.ShouldProcess)
            .OrderBy(x => x.Decision.Priority)
            .ThenBy(x => x.LastCheckedUtc ?? DateTime.MinValue)
            .ThenByDescending(x => x.ReleaseDate ?? DateTime.MinValue)
            .Select(x => x.Item)
            .ToList();
    }
}
