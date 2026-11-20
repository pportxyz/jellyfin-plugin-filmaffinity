using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FilmAffinity.Configuration;
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
/// Scheduled task that updates FilmAffinity ratings across all Movies and TV Series in the Jellyfin library.
/// </summary>
public class FilmAffinitySyncTask : IScheduledTask
{
    private const int MaxConsecutiveRateLimits = 3;

    private readonly ILibraryManager _libraryManager;
    private readonly FilmAffinityRatingService _ratingService;
    private readonly ILogger<FilmAffinitySyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmAffinitySyncTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="ratingService">The FilmAffinity rating service.</param>
    /// <param name="logger">The logger.</param>
    public FilmAffinitySyncTask(
        ILibraryManager libraryManager,
        FilmAffinityRatingService ratingService,
        ILogger<FilmAffinitySyncTask> logger)
    {
        _libraryManager = libraryManager;
        _ratingService = ratingService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Update FilmAffinity Ratings";

    /// <inheritdoc />
    public string Key => "FilmAffinityRatingsSync";

    /// <inheritdoc />
    public string Description => "Resolves FilmAffinity IDs via Wikidata (P480) and updates CommunityRating for all movies and TV series.";

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

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var setCriticRating = config.SetCriticRating;
        var syncMode = config.SyncMode;

        var items = allItems
            .Where(item => IsEnabledForLibrary(_libraryManager.GetLibraryOptions(item), item.GetType().Name))
            .Where(item => ShouldProcessItem(item.ProviderIds, syncMode))
            .ToList();

        if (items.Count == 0)
        {
            _logger.LogInformation(
                "No movies or series to sync (SyncMode={SyncMode}) in libraries with '{ProviderName}' metadata fetcher enabled.",
                syncMode,
                Plugin.ProviderDisplayName);
            progress.Report(100);
            return;
        }

        var updatedCount = 0;
        var consecutiveRateLimits = 0;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            var isSeries = item is Series;

            var (filmAffinityId, score, rateLimited) = await _ratingService
                .LookupWithStatusAsync(item.ProviderIds, isSeries, item.ProductionYear, cancellationToken)
                .ConfigureAwait(false);

            if (rateLimited)
            {
                consecutiveRateLimits++;
                if (consecutiveRateLimits >= MaxConsecutiveRateLimits)
                {
                    _logger.LogWarning(
                        "Stopping FilmAffinity sync early after {Count} consecutive HTTP 429 rate-limit responses so the cooldown window can reset. Re-run later to continue.",
                        consecutiveRateLimits);
                    break;
                }

                progress.Report((double)(i + 1) / items.Count * 100.0);
                continue;
            }

            consecutiveRateLimits = 0;
            var changed = false;

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
                    _logger.LogInformation(
                        "Updated {ItemName} with FilmAffinity rating {Rating} (ID {FilmAffinityId})",
                        item.Name,
                        score.Rating,
                        filmAffinityId);
                }
            }

            progress.Report((double)(i + 1) / items.Count * 100.0);
        }

        _logger.LogInformation(
            "FilmAffinity sync completed: updated {UpdatedCount} of {TotalCount} items",
            updatedCount,
            items.Count);
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
        if (syncMode == SyncMode.RefreshAll)
        {
            return true;
        }

        return providerIds is null
            || !providerIds.TryGetValue(Plugin.ProviderKey, out var existingId)
            || string.IsNullOrWhiteSpace(existingId);
    }
}
