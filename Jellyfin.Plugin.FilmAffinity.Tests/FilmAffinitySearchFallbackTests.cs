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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.FilmAffinity.Tests;

public class FilmAffinitySearchFallbackTests
{
    private const string InvincibleSearchHtml = """
        <div class="row movie-card movie-card-1" data-movie-id="148228">
            <img alt="Invincible (TV Series)" />
            <div class="fs-6 mc-title">
                <a class="d-none d-md-inline-block" href="https://www.filmaffinity.com/en/film148228.html">Invincible</a>
            </div>
            <div class="types-wrapper">
                <span class="type">tv series</span><span class="type">animation</span>
            </div>
            <span class="mc-year ms-1">2021</span>
        </div>
        <div class="row movie-card movie-card-1" data-movie-id="836337">
            <img alt="Invincible " />
            <div class="fs-6 mc-title">
                <a class="d-none d-md-inline-block" href="https://www.filmaffinity.com/en/film836337.html">Invincible</a>
            </div>
            <span class="mc-year ms-1">2006</span>
        </div>
        """;

    private const string SampleScoreHtml = """
        <html>
          <div id="movie-rat-avg" content="7.6">7,6</div>
          <span itemprop="ratingCount" content="9196">9.196</span>
        </html>
        """;

    [Fact]
    public void ParseCandidates_AndSelectBestMatch_DistinguishesSeriesFromMovieAndYear()
    {
        var candidates = FilmAffinitySearchParser.ParseCandidates(InvincibleSearchHtml);

        Assert.Equal(2, candidates.Count);
        Assert.True(candidates[0].IsSeries);
        Assert.Equal("148228", candidates[0].Id);
        Assert.Equal(2021, candidates[0].Year);
        Assert.False(candidates[1].IsSeries);
        Assert.Equal("836337", candidates[1].Id);
        Assert.Equal(2006, candidates[1].Year);

        Assert.Equal("148228", FilmAffinitySearchParser.SelectBestMatch(candidates, "Invincible", null, isSeries: true, productionYear: 2021));
        Assert.Equal("836337", FilmAffinitySearchParser.SelectBestMatch(candidates, "Invincible", null, isSeries: false, productionYear: 2006));
        Assert.Null(FilmAffinitySearchParser.SelectBestMatch(candidates, "Invincible", null, isSeries: true, productionYear: 2006));
        Assert.Null(FilmAffinitySearchParser.SelectBestMatch(candidates, "Invincible", null, isSeries: false, productionYear: 2021));
    }

    [Fact]
    public void SelectBestMatch_MatchesMiniseries_Diacritics_OriginalTitle_AndAdjacentYear()
    {
        const string html = """
            <div class="row movie-card" data-movie-id="624827">
                <div class="mc-title"><a href="/en/film624827.html">Chernobyl</a></div>
                <span class="type">miniseries</span>
                <span class="mc-year">2019</span>
            </div>
            <div class="row movie-card" data-movie-id="879405">
                <div class="mc-title"><a href="/es/film879405.html">La casa de papel (Serie de TV)</a></div>
                <span class="type">Serie de TV</span>
                <span class="mc-year">2017</span>
            </div>
            """;

        var candidates = FilmAffinitySearchParser.ParseCandidates(html);

        Assert.Equal("624827", FilmAffinitySearchParser.SelectBestMatch(candidates, "Chernobyl", null, isSeries: true, productionYear: 2019));
        Assert.Equal("624827", FilmAffinitySearchParser.SelectBestMatch(candidates, "Chernobyl", null, isSeries: true, productionYear: 2020));
        Assert.Equal("879405", FilmAffinitySearchParser.SelectBestMatch(candidates, "Money Heist", "La Casa de Papel", isSeries: true, productionYear: 2017));
    }

    [Fact]
    public void ParseCandidates_HandlesDirectRedirectToFilmPage()
    {
        const string filmHtml = """
            <html>
              <h1 id="main-title"><span itemprop="name">Rick and Morty (TV Series)</span></h1>
              <dd itemprop="datePublished">2013</dd>
            </html>
            """;

        var finalUri = new Uri("https://www.filmaffinity.com/en/film551351.html");
        var candidates = FilmAffinitySearchParser.ParseCandidates(filmHtml, finalUri);

        Assert.Single(candidates);
        Assert.Equal("551351", candidates[0].Id);
        Assert.True(candidates[0].IsSeries);
        Assert.Equal(2013, candidates[0].Year);
        Assert.Equal("551351", FilmAffinitySearchParser.SelectBestMatch(candidates, "Rick and Morty", null, isSeries: true, productionYear: 2013));
    }

    [Fact]
    public void CanUseDirectSearch_RespectsCustomProxyEndpoints()
    {
        Assert.True(FilmAffinitySearchParser.CanUseDirectSearch(null));
        Assert.True(FilmAffinitySearchParser.CanUseDirectSearch(string.Empty));
        Assert.True(FilmAffinitySearchParser.CanUseDirectSearch("https://www.filmaffinity.com/{lang}/film{id}.html"));
        Assert.False(FilmAffinitySearchParser.CanUseDirectSearch("http://localhost:8080/score/{id}"));
    }

    [Fact]
    public async Task RatingService_FallsBackToFilmAffinitySearch_WhenWikidataHasNoP480()
    {
        var requests = new List<Uri>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req.RequestUri!);
            if (req.RequestUri!.Host.Contains("wikidata.org", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"results":{"bindings":[]}}""", Encoding.UTF8, "application/sparql-results+json")
                };
            }

            if (req.RequestUri.AbsolutePath.Contains("search.php", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(InvincibleSearchHtml, Encoding.UTF8, "text/html")
                };
            }

            if (req.RequestUri.AbsolutePath.Contains("film148228.html", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleScoreHtml, Encoding.UTF8, "text/html")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new FilmAffinityCacheService(memoryCache);
        var factory = new SingleHandlerHttpClientFactory(handler);
        using var resolver = new WikidataFilmAffinityResolver(
            factory,
            cache,
            NullLogger<WikidataFilmAffinityResolver>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        using var scoreClient = new FilmAffinityScoreClient(
            factory,
            cache,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => new PluginConfiguration { ThrottleMs = 0, LinkLanguage = "en" });

        var service = new FilmAffinityRatingService(resolver, scoreClient);

        var result = await service.LookupDetailedAsync(
            new Dictionary<string, string> { ["Imdb"] = "tt6741278", ["Tmdb"] = "95557" },
            isSeries: true,
            productionYear: 2021,
            title: "Invincible",
            originalTitle: null,
            CancellationToken.None);

        Assert.Equal("148228", result.FilmAffinityId);
        Assert.NotNull(result.Score);
        Assert.Equal(7.6f, result.Score!.Rating);
        Assert.False(result.RateLimited);
        Assert.False(result.HadTransientError);
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task SyncTask_DoesNotPoisonRefreshState_WhenWikidataFailsWithTransientError()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new FilmAffinityCacheService(memoryCache);
        var factory = new SingleHandlerHttpClientFactory(handler);
        using var resolver = new WikidataFilmAffinityResolver(
            factory,
            cache,
            NullLogger<WikidataFilmAffinityResolver>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        using var scoreClient = new FilmAffinityScoreClient(
            factory,
            cache,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => new PluginConfiguration
            {
                ThrottleMs = 0,
                ScoreEndpointUrl = "http://localhost:8080/score/{id}"
            });

        var tempFile = Path.Combine(Path.GetTempPath(), $"fa-state-transient-{Guid.NewGuid():N}.json");
        try
        {
            using var stateStore = new FilmAffinityRefreshStateStore(tempFile, NullLogger<FilmAffinityRefreshStateStore>.Instance);
            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = "Invincible",
                ProductionYear = 2021,
                ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt6741278" }
            };

            var libraryManager = FakeLibraryManagerProxy.Create([series], enabled: true);
            var task = new FilmAffinitySyncTask(
                libraryManager,
                new FilmAffinityRatingService(resolver, scoreClient),
                stateStore,
                NullLogger<FilmAffinitySyncTask>.Instance,
                () => new PluginConfiguration { SyncMode = SyncMode.SmartRefresh, ThrottleMs = 0 },
                () => new DateTime(2026, 11, 24, 21, 0, 0, DateTimeKind.Utc));

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            var savedState = await stateStore.LoadAsync(CancellationToken.None);
            Assert.Empty(savedState);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task SyncTask_PreservesResolvedFilmAffinityId_WhenScoreFetchHitsRateLimit()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host.Contains("wikidata.org", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"results":{"bindings":[{"faId":{"value":"551351"}}]}}""", Encoding.UTF8, "application/sparql-results+json")
                };
            }

            var rateLimitResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return rateLimitResponse;
        });

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new FilmAffinityCacheService(memoryCache);
        var factory = new SingleHandlerHttpClientFactory(handler);
        using var resolver = new WikidataFilmAffinityResolver(
            factory,
            cache,
            NullLogger<WikidataFilmAffinityResolver>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        using var scoreClient = new FilmAffinityScoreClient(
            factory,
            cache,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => new PluginConfiguration { ThrottleMs = 0, RateLimitCooldownMinutes = 15 });

        var tempFile = Path.Combine(Path.GetTempPath(), $"fa-state-429-{Guid.NewGuid():N}.json");
        try
        {
            using var stateStore = new FilmAffinityRefreshStateStore(tempFile, NullLogger<FilmAffinityRefreshStateStore>.Instance);
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Test Movie",
                ProductionYear = 2020,
                ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1111111" }
            };

            var libraryManager = FakeLibraryManagerProxy.Create([movie], enabled: true);
            var task = new FilmAffinitySyncTask(
                libraryManager,
                new FilmAffinityRatingService(resolver, scoreClient),
                stateStore,
                NullLogger<FilmAffinitySyncTask>.Instance,
                () => new PluginConfiguration { SyncMode = SyncMode.SmartRefresh, ThrottleMs = 0 },
                () => new DateTime(2026, 11, 24, 21, 0, 0, DateTimeKind.Utc));

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.True(movie.ProviderIds.TryGetValue(Plugin.ProviderKey, out var savedId));
            Assert.Equal("551351", savedId);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task SearchFilmAffinityId_Http200WithPassiveCloudflareJsdScript_ParsesMatchAndEmptyResultsCleanly()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            var html = req.RequestUri!.Query.Contains("Mayday", StringComparison.OrdinalIgnoreCase)
                ? """
                  <html><body>
                    <div class="row movie-card movie-card-1" data-movie-id="123456">
                      <div class="fs-6 mc-title">
                        <a class="d-none d-md-inline-block" href="https://www.filmaffinity.com/es/film123456.html">Mayday</a>
                      </div>
                      <span class="mc-year ms-1">2021</span>
                    </div>
                    <script src="/cdn-cgi/challenge-platform/scripts/jsd/main.js"></script>
                  </body></html>
                  """
                : """
                  <html><body>
                    <div class="no-results">No results</div>
                    <script src="/cdn-cgi/challenge-platform/scripts/jsd/main.js"></script>
                  </body></html>
                  """;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
            response.Headers.Add("cf-ray", "a6035ee40d500bf3-MAD");
            return response;
        });

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new FilmAffinityCacheService(memoryCache);
        using var scoreClient = new FilmAffinityScoreClient(
            new SingleHandlerHttpClientFactory(handler),
            cache,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => new PluginConfiguration { ThrottleMs = 0 });

        var matched = await scoreClient.SearchFilmAffinityIdAsync("Mayday", null, isSeries: false, 2021, CancellationToken.None);
        Assert.Equal("123456", matched.FilmAffinityId);
        Assert.False(matched.HadTransientError);

        var unmatched = await scoreClient.SearchFilmAffinityIdAsync("Nonexistent Title", null, isSeries: false, 2021, CancellationToken.None);
        Assert.Null(unmatched.FilmAffinityId);
        Assert.False(unmatched.HadTransientError);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private class FakeLibraryManagerProxy : DispatchProxy
    {
        private List<BaseItem> _items = [];
        private LibraryOptions _libraryOptions = new();

        public static ILibraryManager Create(List<BaseItem> items, bool enabled)
        {
            var proxy = Create<ILibraryManager, FakeLibraryManagerProxy>();
            var impl = (FakeLibraryManagerProxy)(object)proxy;
            impl._items = items;
            impl._libraryOptions = new LibraryOptions
            {
                TypeOptions =
                [
                    new TypeOptions
                    {
                        Type = nameof(Movie),
                        MetadataFetchers = enabled ? [Plugin.ProviderDisplayName] : []
                    },
                    new TypeOptions
                    {
                        Type = nameof(Series),
                        MetadataFetchers = enabled ? [Plugin.ProviderDisplayName] : []
                    }
                ]
            };
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(ILibraryManager.GetItemList) => _items,
                nameof(ILibraryManager.GetLibraryOptions) => _libraryOptions,
                nameof(ILibraryManager.UpdateItemAsync) => Task.CompletedTask,
                _ => throw new NotImplementedException(targetMethod?.Name)
            };
        }
    }
}
