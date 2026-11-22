using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FilmAffinity.Configuration;
using Jellyfin.Plugin.FilmAffinity.Models;
using Jellyfin.Plugin.FilmAffinity.ScheduledTasks;
using Jellyfin.Plugin.FilmAffinity.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.FilmAffinity.Tests;

public class FilmAffinitySmartRefreshTests
{
    private static readonly DateTime FixedNow = new(2026, 6, 15, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Policy_UnmatchedItem_ImmediateFirstTime_ThenWeeklyRetry()
    {
        var unmatched = new Dictionary<string, string> { ["Imdb"] = "tt1234567" };

        var first = FilmAffinityRefreshPolicy.Evaluate(
            unmatched,
            communityRating: null,
            premiereDate: FixedNow.AddDays(-10),
            productionYear: 2026,
            isContinuingSeries: false,
            state: null,
            SyncMode.SmartRefresh,
            FixedNow);

        Assert.True(first.ShouldProcess);
        Assert.Equal(FilmAffinityRefreshPolicy.PriorityUnmatched, first.Priority);

        var checkedRecently = new RefreshStateEntry(null, FixedNow.AddDays(-6), null, null);
        var skip = FilmAffinityRefreshPolicy.Evaluate(
            unmatched, null, FixedNow.AddDays(-10), 2026, false, checkedRecently, SyncMode.SmartRefresh, FixedNow);
        Assert.False(skip.ShouldProcess);

        var checkedWeekAgo = new RefreshStateEntry(null, FixedNow.AddDays(-7), null, null);
        var retry = FilmAffinityRefreshPolicy.Evaluate(
            unmatched, null, FixedNow.AddDays(-10), 2026, false, checkedWeekAgo, SyncMode.SmartRefresh, FixedNow);
        Assert.True(retry.ShouldProcess);
    }

    [Fact]
    public void Policy_MatchedButUnrated_RetriesEvery3DaysForRecent_Every14DaysForOlder()
    {
        var matched = new Dictionary<string, string> { [Plugin.ProviderKey] = "111111" };

        // Recent release (20 days old), checked 2 days ago -> skip
        var recentSkip = FilmAffinityRefreshPolicy.Evaluate(
            matched, null, FixedNow.AddDays(-20), 2026, false,
            new RefreshStateEntry("111111", FixedNow.AddDays(-2), null, 12),
            SyncMode.SmartRefresh, FixedNow);
        Assert.False(recentSkip.ShouldProcess);

        // Recent release (20 days old), checked 3 days ago -> process
        var recentDue = FilmAffinityRefreshPolicy.Evaluate(
            matched, null, FixedNow.AddDays(-20), 2026, false,
            new RefreshStateEntry("111111", FixedNow.AddDays(-3), null, 12),
            SyncMode.SmartRefresh, FixedNow);
        Assert.True(recentDue.ShouldProcess);
        Assert.Equal(FilmAffinityRefreshPolicy.PriorityMatchedUnrated, recentDue.Priority);

        // Older release (3 years old), checked 10 days ago -> skip
        var olderSkip = FilmAffinityRefreshPolicy.Evaluate(
            matched, null, FixedNow.AddYears(-3), 2023, false,
            new RefreshStateEntry("111111", FixedNow.AddDays(-10), null, 20),
            SyncMode.SmartRefresh, FixedNow);
        Assert.False(olderSkip.ShouldProcess);

        // Older release (3 years old), checked 14 days ago -> process
        var olderDue = FilmAffinityRefreshPolicy.Evaluate(
            matched, null, FixedNow.AddYears(-3), 2023, false,
            new RefreshStateEntry("111111", FixedNow.AddDays(-14), null, 20),
            SyncMode.SmartRefresh, FixedNow);
        Assert.True(olderDue.ShouldProcess);
    }

    [Fact]
    public void Policy_RecentRelease_Uses3DayCadenceWhenLowVotes_And7DayCadenceOtherwise()
    {
        var matched = new Dictionary<string, string> { [Plugin.ProviderKey] = "222222" };
        var premiere = FixedNow.AddDays(-30);

        // Low vote count (< 500): due after 3 days
        var lowVoteState = new RefreshStateEntry("222222", FixedNow.AddDays(-3), 7.2f, 240);
        var lowVoteDue = FilmAffinityRefreshPolicy.Evaluate(
            matched, 7.2f, premiere, 2026, false, lowVoteState, SyncMode.SmartRefresh, FixedNow);
        Assert.True(lowVoteDue.ShouldProcess);
        Assert.Equal(FilmAffinityRefreshPolicy.PriorityRecentRelease, lowVoteDue.Priority);

        // Higher vote count (>= 500): skipped at 3 days, due at 7 days
        var highVoteRecent = new RefreshStateEntry("222222", FixedNow.AddDays(-3), 7.2f, 1500);
        Assert.False(FilmAffinityRefreshPolicy.Evaluate(
            matched, 7.2f, premiere, 2026, false, highVoteRecent, SyncMode.SmartRefresh, FixedNow).ShouldProcess);

        var highVoteWeekAgo = new RefreshStateEntry("222222", FixedNow.AddDays(-7), 7.2f, 1500);
        Assert.True(FilmAffinityRefreshPolicy.Evaluate(
            matched, 7.2f, premiere, 2026, false, highVoteWeekAgo, SyncMode.SmartRefresh, FixedNow).ShouldProcess);
    }

    [Fact]
    public void Policy_MidTermRelease_RefreshesMonthly_AndCatalogItemIsSkipped()
    {
        var matched = new Dictionary<string, string> { [Plugin.ProviderKey] = "333333" };

        // 180 days old: skipped at 20 days, due at 30 days
        var midTermPremiere = FixedNow.AddDays(-180);
        Assert.False(FilmAffinityRefreshPolicy.Evaluate(
            matched, 7.5f, midTermPremiere, 2025, false,
            new RefreshStateEntry("333333", FixedNow.AddDays(-20), 7.5f, 5000),
            SyncMode.SmartRefresh, FixedNow).ShouldProcess);

        var midTermDue = FilmAffinityRefreshPolicy.Evaluate(
            matched, 7.5f, midTermPremiere, 2025, false,
            new RefreshStateEntry("333333", FixedNow.AddDays(-30), 7.5f, 5000),
            SyncMode.SmartRefresh, FixedNow);
        Assert.True(midTermDue.ShouldProcess);
        Assert.Equal(FilmAffinityRefreshPolicy.PriorityMidTermRelease, midTermDue.Priority);

        // 3 years old, rated, not continuing: skipped even with no prior state
        var catalog = FilmAffinityRefreshPolicy.Evaluate(
            matched, 8.1f, FixedNow.AddYears(-3), 2023, false,
            state: null, SyncMode.SmartRefresh, FixedNow);
        Assert.False(catalog.ShouldProcess);
    }

    [Fact]
    public void Policy_ContinuingSeries_RefreshesMonthlyRegardlessOfAge_EndedSeriesSkipped()
    {
        var matched = new Dictionary<string, string> { [Plugin.ProviderKey] = "444444" };
        var oldPremiere = FixedNow.AddYears(-5);

        // Continuing series: skipped at 15 days, due at 30 days
        Assert.False(FilmAffinityRefreshPolicy.Evaluate(
            matched, 8.0f, oldPremiere, 2021, isContinuingSeries: true,
            new RefreshStateEntry("444444", FixedNow.AddDays(-15), 8.0f, 9000),
            SyncMode.SmartRefresh, FixedNow).ShouldProcess);

        var continuingDue = FilmAffinityRefreshPolicy.Evaluate(
            matched, 8.0f, oldPremiere, 2021, isContinuingSeries: true,
            new RefreshStateEntry("444444", FixedNow.AddDays(-30), 8.0f, 9000),
            SyncMode.SmartRefresh, FixedNow);
        Assert.True(continuingDue.ShouldProcess);
        Assert.Equal(FilmAffinityRefreshPolicy.PriorityContinuingSeries, continuingDue.Priority);

        // Ended series of same age: skipped
        var ended = FilmAffinityRefreshPolicy.Evaluate(
            matched, 8.0f, oldPremiere, 2021, isContinuingSeries: false,
            state: null, SyncMode.SmartRefresh, FixedNow);
        Assert.False(ended.ShouldProcess);
    }

    [Fact]
    public void Policy_ProductionYearFallback_UsedWhenPremiereDateMissing()
    {
        var matched = new Dictionary<string, string> { [Plugin.ProviderKey] = "555555" };

        // ProductionYear = 2026 -> resolves to 2026-07-01 -> age <= 90 days -> recent tier
        var recentByYear = FilmAffinityRefreshPolicy.Evaluate(
            matched, 6.8f, premiereDate: null, productionYear: 2026, isContinuingSeries: false,
            state: null, SyncMode.SmartRefresh, FixedNow);
        Assert.True(recentByYear.ShouldProcess);
        Assert.Equal(FilmAffinityRefreshPolicy.PriorityRecentRelease, recentByYear.Priority);

        // ProductionYear = 2015 -> catalog -> skipped
        var oldByYear = FilmAffinityRefreshPolicy.Evaluate(
            matched, 8.4f, premiereDate: null, productionYear: 2015, isContinuingSeries: false,
            state: null, SyncMode.SmartRefresh, FixedNow);
        Assert.False(oldByYear.ShouldProcess);
    }

    [Fact]
    public async Task StateStore_RoundTrips_RecoversFromCorruptJson_AndPrunesDeleted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "fa-state-test-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(tempDir, "refresh-state.json");

        try
        {
            using var store = new FilmAffinityRefreshStateStore(statePath, NullLogger<FilmAffinityRefreshStateStore>.Instance);

            // Missing file returns empty
            var initial = await store.LoadAsync(CancellationToken.None);
            Assert.Empty(initial);

            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            initial[id1] = new RefreshStateEntry("100001", FixedNow.AddDays(-2), 7.4f, 1200);
            initial[id2] = new RefreshStateEntry("100002", FixedNow.AddDays(-5), null, 30);

            await store.SaveAsync(initial, CancellationToken.None);
            Assert.True(File.Exists(statePath));

            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("100001", loaded[id1].FilmAffinityId);
            Assert.Equal(7.4f, loaded[id1].LastRating);
            Assert.Equal(1200, loaded[id1].LastVoteCount);
            Assert.Equal(FixedNow.AddDays(-2), loaded[id1].LastCheckedUtc);

            // Prune deleted item id2
            var pruned = FilmAffinityRefreshStateStore.PruneDeletedItems(loaded, new HashSet<Guid> { id1 });
            Assert.Equal(1, pruned);
            Assert.Single(loaded);
            Assert.True(loaded.ContainsKey(id1));

            // Corrupt JSON gracefully returns empty dictionary
            await File.WriteAllTextAsync(statePath, "{ not valid json !!!");
            var recovered = await store.LoadAsync(CancellationToken.None);
            Assert.Empty(recovered);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SyncTask_SelectCandidates_OrdersByPriorityThenOldestCheckThenNewestRelease()
    {
        var unmatchedMovie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Unmatched",
            PremiereDate = FixedNow.AddYears(-5),
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0001" }
        };
        var unratedMovie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Matched Unrated",
            PremiereDate = FixedNow.AddDays(-15),
            ProviderIds = new Dictionary<string, string> { [Plugin.ProviderKey] = "1001" }
        };
        var recentOlderCheck = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Recent Older Check",
            CommunityRating = 7.0f,
            PremiereDate = FixedNow.AddDays(-40),
            ProviderIds = new Dictionary<string, string> { [Plugin.ProviderKey] = "1002" }
        };
        var recentNeverChecked = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Recent Never Checked",
            CommunityRating = 7.1f,
            PremiereDate = FixedNow.AddDays(-20),
            ProviderIds = new Dictionary<string, string> { [Plugin.ProviderKey] = "1003" }
        };
        var continuingSeries = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Continuing Show",
            Status = SeriesStatus.Continuing,
            CommunityRating = 8.2f,
            PremiereDate = FixedNow.AddYears(-4),
            ProviderIds = new Dictionary<string, string> { [Plugin.ProviderKey] = "1004" }
        };
        var catalogMovie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Old Catalog",
            CommunityRating = 8.5f,
            PremiereDate = FixedNow.AddYears(-10),
            ProviderIds = new Dictionary<string, string> { [Plugin.ProviderKey] = "1005" }
        };

        var state = new Dictionary<Guid, RefreshStateEntry>
        {
            [recentOlderCheck.Id] = new RefreshStateEntry("1002", FixedNow.AddDays(-10), 7.0f, 1000)
        };

        var items = new BaseItem[]
        {
            catalogMovie,
            continuingSeries,
            recentOlderCheck,
            recentNeverChecked,
            unratedMovie,
            unmatchedMovie
        };

        var selected = FilmAffinitySyncTask.SelectCandidates(items, state, SyncMode.SmartRefresh, FixedNow);

        Assert.Equal(5, selected.Count);
        Assert.Equal("Unmatched", selected[0].Name);
        Assert.Equal("Matched Unrated", selected[1].Name);
        Assert.Equal("Recent Never Checked", selected[2].Name);
        Assert.Equal("Recent Older Check", selected[3].Name);
        Assert.Equal("Continuing Show", selected[4].Name);
    }

    [Fact]
    public async Task SyncTask_ExecuteAsync_UpdatesDriftedRating_RespectsCap_AndFlushesOn429()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "fa-sync-test-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(tempDir, "refresh-state.json");

        try
        {
            var movie1 = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Movie One",
                CommunityRating = 7.8f,
                PremiereDate = FixedNow.AddDays(-25),
                ProductionYear = 2026,
                ProviderIds = new Dictionary<string, string> { [Plugin.ProviderKey] = "900001" }
            };
            var movie2 = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Movie Two",
                CommunityRating = 6.5f,
                CriticRating = 65.0f,
                PremiereDate = FixedNow.AddDays(-20),
                ProductionYear = 2026,
                ProviderIds = new Dictionary<string, string> { [Plugin.ProviderKey] = "900002" }
            };
            var movie3 = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Movie Three",
                CommunityRating = 7.0f,
                PremiereDate = FixedNow.AddDays(-10),
                ProductionYear = 2026,
                ProviderIds = new Dictionary<string, string> { [Plugin.ProviderKey] = "900003" }
            };

            var libraryOptions = new LibraryOptions
            {
                TypeOptions =
                [
                    new TypeOptions
                    {
                        Type = nameof(Movie),
                        MetadataFetchers = [Plugin.ProviderDisplayName]
                    }
                ]
            };

            var updatedItems = new List<BaseItem>();
            var libraryManager = StubLibraryManagerProxy.Create(
                [movie1, movie2, movie3],
                libraryOptions,
                updatedItems);

            var scoreHandler = new StubHttpMessageHandler(request =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.EndsWith("/900003", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"rating": 6.4, "votes": 1200}""", Encoding.UTF8, "application/json")
                    };
                }

                if (url.EndsWith("/900002", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"rating": 6.5, "votes": 800}""", Encoding.UTF8, "application/json")
                    };
                }

                var rateLimitedResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                rateLimitedResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return rateLimitedResponse;
            });

            var factory = new NamedStubHttpClientFactory(new Dictionary<string, HttpMessageHandler>
            {
                [WikidataFilmAffinityResolver.HttpClientName] = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
                [FilmAffinityScoreClient.HttpClientName] = scoreHandler
            });

            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var cacheService = new FilmAffinityCacheService(memoryCache);
            var config = new PluginConfiguration
            {
                ScoreEndpointUrl = "http://scores.local/score/{id}",
                SyncMode = SyncMode.SmartRefresh,
                MaxItemsPerSyncRun = 2,
                SetCriticRating = true,
                MinVotes = 50,
                ThrottleMs = 0,
                RateLimitCooldownMinutes = 15
            };

            var now = FixedNow;
            using var resolver = new WikidataFilmAffinityResolver(
                factory, cacheService, NullLogger<WikidataFilmAffinityResolver>.Instance, TimeSpan.Zero, TimeSpan.Zero);
            using var scoreClient = new FilmAffinityScoreClient(
                factory, cacheService, NullLogger<FilmAffinityScoreClient>.Instance, () => config, () => now);
            var ratingService = new FilmAffinityRatingService(resolver, scoreClient);
            using var stateStore = new FilmAffinityRefreshStateStore(statePath, NullLogger<FilmAffinityRefreshStateStore>.Instance);

            var task = new FilmAffinitySyncTask(
                libraryManager,
                ratingService,
                stateStore,
                NullLogger<FilmAffinitySyncTask>.Instance,
                () => config,
                () => now);

            // Run 1: capped at 2 items -> processes movie3 (newest, rating drifted 7.0 -> 6.4) and movie2 (unchanged 6.5)
            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Single(updatedItems);
            Assert.Equal(movie3.Id, updatedItems[0].Id);
            Assert.Equal(6.4f, movie3.CommunityRating);
            Assert.Equal(64.0f, movie3.CriticRating);

            var stateAfterRun1 = await stateStore.LoadAsync(CancellationToken.None);
            Assert.Equal(2, stateAfterRun1.Count);
            Assert.True(stateAfterRun1.ContainsKey(movie3.Id));
            Assert.True(stateAfterRun1.ContainsKey(movie2.Id));
            Assert.False(stateAfterRun1.ContainsKey(movie1.Id));
            Assert.Equal(1200, stateAfterRun1[movie3.Id].LastVoteCount);

            // Run 2 (1 hour later): movie2 and movie3 are skipped (recently checked); movie1 is due and hits 429
            now = FixedNow.AddHours(1);
            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            // movie1 must NOT be marked checked because it hit HTTP 429
            var stateAfterRun2 = await stateStore.LoadAsync(CancellationToken.None);
            Assert.Equal(2, stateAfterRun2.Count);
            Assert.False(stateAfterRun2.ContainsKey(movie1.Id));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    private sealed class NamedStubHttpClientFactory(IReadOnlyDictionary<string, HttpMessageHandler> handlers) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handlers[name], disposeHandler: false);
    }

    private class StubLibraryManagerProxy : DispatchProxy
    {
        private List<BaseItem> _items = [];
        private LibraryOptions _libraryOptions = new();
        private List<BaseItem> _updatedItems = [];

        public static ILibraryManager Create(
            List<BaseItem> items,
            LibraryOptions libraryOptions,
            List<BaseItem> updatedItems)
        {
            var proxy = Create<ILibraryManager, StubLibraryManagerProxy>();
            var typed = (StubLibraryManagerProxy)(object)proxy;
            typed._items = items;
            typed._libraryOptions = libraryOptions;
            typed._updatedItems = updatedItems;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (string.Equals(targetMethod.Name, nameof(ILibraryManager.GetItemList), StringComparison.Ordinal))
            {
                return _items;
            }

            if (string.Equals(targetMethod.Name, nameof(ILibraryManager.GetLibraryOptions), StringComparison.Ordinal))
            {
                return _libraryOptions;
            }

            if (string.Equals(targetMethod.Name, nameof(ILibraryManager.UpdateItemAsync), StringComparison.Ordinal))
            {
                if (args is { Length: > 0 } && args[0] is BaseItem item)
                {
                    _updatedItems.Add(item);
                }

                return Task.CompletedTask;
            }

            throw new NotSupportedException($"Unexpected ILibraryManager call: {targetMethod.Name}");
        }
    }
}
