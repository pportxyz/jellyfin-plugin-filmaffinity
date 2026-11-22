using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FilmAffinity.Configuration;
using Jellyfin.Plugin.FilmAffinity.Providers;
using Jellyfin.Plugin.FilmAffinity.ScheduledTasks;
using Jellyfin.Plugin.FilmAffinity.Services;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.FilmAffinity.Tests;

/// <summary>
/// Unit tests for <see cref="WikidataFilmAffinityResolver"/> and <see cref="FilmAffinityScoreClient"/>.
/// </summary>
public class FilmAffinityServicesTests
{
    [Fact]
    public void ParseSparqlResponse_ValidResult_ReturnsFilmAffinityId()
    {
        var sparql = """
            {
              "results": {
                "bindings": [
                  { "faId": { "type": "literal", "value": "809297" } }
                ]
              }
            }
            """;

        var id = WikidataFilmAffinityResolver.ParseSparqlResponse(sparql);
        Assert.Equal("809297", id);
    }

    [Fact]
    public async Task Resolver_PrefersExistingFilmAffinityProviderId()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var callCount = 0;
        using var handler = new StubHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var factory = new StubHttpClientFactory(handler);

        using var resolver = new WikidataFilmAffinityResolver(
            factory,
            cacheService,
            NullLogger<WikidataFilmAffinityResolver>.Instance);

        var providerIds = new Dictionary<string, string>
        {
            [Plugin.ProviderKey] = "809297",
            ["Imdb"] = "tt0068646"
        };

        var resolved = await resolver.ResolveAsync(providerIds, isSeries: false, CancellationToken.None);

        Assert.Equal("809297", resolved);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task Resolver_CombinesImdbAndTmdbIntoSingleSparqlQueryAndCachesBoth()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var callCount = 0;
        string? seenQuery = null;

        using var handler = new StubHttpMessageHandler(req =>
        {
            callCount++;
            seenQuery = Uri.UnescapeDataString(req.RequestUri?.Query ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"results":{"bindings":[{"faId":{"type":"literal","value":"824461"}}]}}""",
                    Encoding.UTF8,
                    "application/sparql-results+json")
            };
        });

        using var resolver = new WikidataFilmAffinityResolver(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<WikidataFilmAffinityResolver>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);

        var providerIds = new Dictionary<string, string>
        {
            ["Imdb"] = "tt7984734",
            ["Tmdb"] = "503919"
        };

        var first = await resolver.ResolveAsync(providerIds, isSeries: false, CancellationToken.None);
        var second = await resolver.ResolveAsync(
            new Dictionary<string, string> { ["Tmdb"] = "503919" },
            isSeries: false,
            CancellationToken.None);

        Assert.Equal("824461", first);
        Assert.Equal("824461", second);
        Assert.Equal(1, callCount);
        Assert.NotNull(seenQuery);
        Assert.Contains("P345", seenQuery, StringComparison.Ordinal);
        Assert.Contains("P4947", seenQuery, StringComparison.Ordinal);
        Assert.Contains("VALUES", seenQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_RetriesOnTransientTimeoutAndSucceeds()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var attempts = 0;

        using var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new TaskCanceledException("Simulated HttpClient timeout");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"results":{"bindings":[{"faId":{"type":"literal","value":"824461"}}]}}""",
                    Encoding.UTF8,
                    "application/sparql-results+json")
            };
        });

        using var resolver = new WikidataFilmAffinityResolver(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<WikidataFilmAffinityResolver>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);

        var resolved = await resolver.ResolveAsync(
            new Dictionary<string, string> { ["Imdb"] = "tt7984734" },
            isSeries: false,
            CancellationToken.None);

        Assert.Equal("824461", resolved);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Resolver_RetriesOnHttp429AndSucceeds()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var attempts = 0;

        using var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return rateLimited;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"results":{"bindings":[{"faId":{"type":"literal","value":"824461"}}]}}""",
                    Encoding.UTF8,
                    "application/sparql-results+json")
            };
        });

        using var resolver = new WikidataFilmAffinityResolver(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<WikidataFilmAffinityResolver>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);

        var resolved = await resolver.ResolveAsync(
            new Dictionary<string, string> { ["Imdb"] = "tt7984734" },
            isSeries: false,
            CancellationToken.None);

        Assert.Equal("824461", resolved);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Resolver_LimitsConcurrentInFlightRequests()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var inFlight = 0;
        var maxInFlight = 0;

        using var handler = new AsyncStubHttpMessageHandler(async (_, ct) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            int observed;
            do
            {
                observed = maxInFlight;
                if (current <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref maxInFlight, current, observed) != observed);

            try
            {
                await Task.Delay(50, ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"results":{"bindings":[{"faId":{"type":"literal","value":"824461"}}]}}""",
                        Encoding.UTF8,
                        "application/sparql-results+json")
                };
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        });

        using var resolver = new WikidataFilmAffinityResolver(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<WikidataFilmAffinityResolver>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);

        var tasks = new Task<string?>[6];
        for (var i = 0; i < tasks.Length; i++)
        {
            var imdbId = $"tt000000{i}";
            tasks[i] = resolver.ResolveAsync(
                new Dictionary<string, string> { ["Imdb"] = imdbId },
                isSeries: false,
                CancellationToken.None);
        }

        var results = await Task.WhenAll(tasks);

        Assert.All(results, id => Assert.Equal("824461", id));
        Assert.InRange(maxInFlight, 1, 2);
    }

    [Fact]
    public async Task ScoreClient_RespectsMinVotesAndCachesResult()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var callCount = 0;
        string? seenAuthHeader = null;

        using var handler = new StubHttpMessageHandler(req =>
        {
            callCount++;
            if (req.Headers.TryGetValues("X-API-Key", out var values))
            {
                seenAuthHeader = string.Join(",", values);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"rating": 8.5, "votes": 120}""", Encoding.UTF8, "application/json")
            };
        });

        var config = new PluginConfiguration
        {
            ScoreEndpointUrl = "http://scores.local/score/{id}",
            AuthHeaderName = "X-API-Key",
            AuthHeaderValue = "secret-token",
            MinVotes = 50,
            ThrottleMs = 0
        };

        using var client = new FilmAffinityScoreClient(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => config);

        var first = await client.GetScoreAsync("809297", 1972, CancellationToken.None);
        var second = await client.GetScoreAsync("809297", 1972, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(8.5f, first.Rating);
        Assert.Equal(120, first.VoteCount);
        Assert.Equal("secret-token", seenAuthHeader);
        Assert.NotNull(second);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ScoreClient_IgnoresScoreBelowMinVotes()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);

        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"rating": 9.1, "votes": 12}""", Encoding.UTF8, "application/json")
        });

        var config = new PluginConfiguration
        {
            ScoreEndpointUrl = "http://scores.local/score/{id}",
            MinVotes = 50,
            ThrottleMs = 0
        };

        using var client = new FilmAffinityScoreClient(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => config);

        var result = await client.GetScoreAsync("999999", 2024, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ScoreClient_Http200WithChallengePlatformScriptAndValidRating_ParsesRating()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);

        var html = """
            <html>
              <head><script src="/cdn-cgi/challenge-platform/scripts/jsd/main.js"></script></head>
              <body>
                <div id="movie-rat-avg" itemprop="ratingValue" content="8.4">8,4</div>
                <span itemprop="ratingCount" content="45000">45.000</span>
              </body>
            </html>
            """;

        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });

        var config = new PluginConfiguration
        {
            ScoreEndpointUrl = string.Empty,
            MinVotes = 50,
            ThrottleMs = 0
        };

        using var client = new FilmAffinityScoreClient(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => config);

        var result = await client.GetScoreAsync("230129", 2020, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(8.4f, result.Rating);
        Assert.Equal(45000, result.VoteCount);
    }

    [Fact]
    public void SyncTask_IsEnabledForLibrary_RespectsLibraryMetadataFetchers()
    {
        var enabledLibrary = new LibraryOptions
        {
            TypeOptions =
            [
                new TypeOptions
                {
                    Type = "Movie",
                    MetadataFetchers = ["TheMovieDb", Plugin.ProviderDisplayName]
                }
            ]
        };

        var disabledLibrary = new LibraryOptions
        {
            TypeOptions =
            [
                new TypeOptions
                {
                    Type = "Movie",
                    MetadataFetchers = ["TheMovieDb"]
                }
            ]
        };

        Assert.True(FilmAffinitySyncTask.IsEnabledForLibrary(enabledLibrary, "Movie"));
        Assert.False(FilmAffinitySyncTask.IsEnabledForLibrary(enabledLibrary, "Series"));
        Assert.False(FilmAffinitySyncTask.IsEnabledForLibrary(disabledLibrary, "Movie"));
        Assert.False(FilmAffinitySyncTask.IsEnabledForLibrary(null, "Movie"));
    }

    [Fact]
    public async Task ScoreClient_Http429WithRetryAfter_RetriesAndSucceeds()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var attempts = 0;

        using var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                return rateLimited;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"rating": 7.9, "votes": 1000}""", Encoding.UTF8, "application/json")
            };
        });

        var config = new PluginConfiguration
        {
            ScoreEndpointUrl = "http://scores.local/score/{id}",
            MinVotes = 50,
            ThrottleMs = 0
        };

        using var client = new FilmAffinityScoreClient(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => config);

        var result = await client.GetScoreAsync("144113", 2015, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(7.9f, result.Rating);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ScoreClient_Http429Exhausted_ReturnsRateLimitedTrue()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var attempts = 0;

        using var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
            return rateLimited;
        });

        var config = new PluginConfiguration
        {
            ScoreEndpointUrl = "http://scores.local/score/{id}",
            MinVotes = 50,
            ThrottleMs = 0
        };

        using var client = new FilmAffinityScoreClient(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => config);

        var result = await client.FetchScoreAsync("144113", 2015, CancellationToken.None);

        Assert.Null(result.Score);
        Assert.True(result.RateLimited);
        Assert.True(client.IsInRateLimitCooldown);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ScoreClient_Http429Exhausted_PausesRequestsDuringCooldownAndResumesAfter()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        var attempts = 0;

        using var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts <= 3)
            {
                var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return rateLimited;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"rating": 8.1, "votes": 500}""", Encoding.UTF8, "application/json")
            };
        });

        var config = new PluginConfiguration
        {
            ScoreEndpointUrl = "http://scores.local/score/{id}",
            MinVotes = 50,
            ThrottleMs = 0,
            RateLimitCooldownMinutes = 30
        };

        using var client = new FilmAffinityScoreClient(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => config,
            () => now);

        var first = await client.FetchScoreAsync("144113", 2015, CancellationToken.None);
        Assert.True(first.RateLimited);
        Assert.True(client.IsInRateLimitCooldown);
        Assert.Equal(3, attempts);

        now = now.AddMinutes(20);
        var skipped = await client.FetchScoreAsync("230129", 2020, CancellationToken.None);
        Assert.Null(skipped.Score);
        Assert.True(skipped.RateLimited);
        Assert.Equal(3, attempts);

        now = now.AddMinutes(10);
        Assert.False(client.IsInRateLimitCooldown);

        var resumed = await client.FetchScoreAsync("230129", 2020, CancellationToken.None);
        Assert.False(resumed.RateLimited);
        Assert.NotNull(resumed.Score);
        Assert.Equal(8.1f, resumed.Score.Rating);
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task ScoreClient_ZeroCooldownMinutes_DisablesCooldownWindow()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var attempts = 0;

        using var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts <= 3)
            {
                var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return rateLimited;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"rating": 7.5, "votes": 200}""", Encoding.UTF8, "application/json")
            };
        });

        var config = new PluginConfiguration
        {
            ScoreEndpointUrl = "http://scores.local/score/{id}",
            MinVotes = 50,
            ThrottleMs = 0,
            RateLimitCooldownMinutes = 0
        };

        using var client = new FilmAffinityScoreClient(
            new StubHttpClientFactory(handler),
            cacheService,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => config);

        var first = await client.FetchScoreAsync("144113", 2015, CancellationToken.None);
        Assert.True(first.RateLimited);
        Assert.False(client.IsInRateLimitCooldown);

        var second = await client.FetchScoreAsync("230129", 2020, CancellationToken.None);
        Assert.False(second.RateLimited);
        Assert.NotNull(second.Score);
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task MovieProvider_StopsAndSkipsSubsequentItemsDuringRateLimitCooldown()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheService = new FilmAffinityCacheService(cache);
        var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        var wikidataCalls = 0;
        var scoreCalls = 0;

        using var wikidataHandler = new StubHttpMessageHandler(_ =>
        {
            wikidataCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"results":{"bindings":[{"faId":{"type":"literal","value":"824461"}}]}}""",
                    Encoding.UTF8,
                    "application/sparql-results+json")
            };
        });

        using var scoreHandler = new StubHttpMessageHandler(_ =>
        {
            scoreCalls++;
            var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
            return rateLimited;
        });

        var factory = new NamedStubHttpClientFactory(new Dictionary<string, HttpMessageHandler>
        {
            [WikidataFilmAffinityResolver.HttpClientName] = wikidataHandler,
            [FilmAffinityScoreClient.HttpClientName] = scoreHandler
        });

        var config = new PluginConfiguration
        {
            ScoreEndpointUrl = "http://scores.local/score/{id}",
            MinVotes = 50,
            ThrottleMs = 0
        };

        using var resolver = new WikidataFilmAffinityResolver(
            factory,
            cacheService,
            NullLogger<WikidataFilmAffinityResolver>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);

        using var scoreClient = new FilmAffinityScoreClient(
            factory,
            cacheService,
            NullLogger<FilmAffinityScoreClient>.Instance,
            () => config,
            () => now);

        var ratingService = new FilmAffinityRatingService(resolver, scoreClient);
        var provider = new FilmAffinityMovieProvider(
            ratingService,
            factory,
            NullLogger<FilmAffinityMovieProvider>.Instance);

        var firstMovie = new MovieInfo
        {
            Name = "First New Movie",
            Year = 2024,
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1111111" }
        };

        var secondMovie = new MovieInfo
        {
            Name = "Second New Movie",
            Year = 2024,
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt2222222" }
        };

        var firstResult = await provider.GetMetadata(firstMovie, CancellationToken.None);
        var secondResult = await provider.GetMetadata(secondMovie, CancellationToken.None);

        Assert.False(firstResult.HasMetadata);
        Assert.False(secondResult.HasMetadata);
        Assert.Equal(1, wikidataCalls);
        Assert.Equal(3, scoreCalls);
    }

    [Fact]
    public void SyncTask_ShouldProcessItem_RespectsSyncMode()
    {
        var matched = new Dictionary<string, string>
        {
            [Plugin.ProviderKey] = "809297",
            ["Imdb"] = "tt0068646"
        };
        var unmatched = new Dictionary<string, string>
        {
            ["Imdb"] = "tt0068646"
        };

        Assert.False(FilmAffinitySyncTask.ShouldProcessItem(matched, SyncMode.OnlyNewAndUnmatched));
        Assert.True(FilmAffinitySyncTask.ShouldProcessItem(unmatched, SyncMode.OnlyNewAndUnmatched));
        Assert.True(FilmAffinitySyncTask.ShouldProcessItem(matched, SyncMode.RefreshAll));
        Assert.True(FilmAffinitySyncTask.ShouldProcessItem(unmatched, SyncMode.RefreshAll));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    private sealed class AsyncStubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return responder(request, cancellationToken);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NamedStubHttpClientFactory(IReadOnlyDictionary<string, HttpMessageHandler> handlers) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handlers[name], disposeHandler: false);
    }
}
