using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FilmAffinity.Configuration;
using Jellyfin.Plugin.FilmAffinity.Services;
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

        var resolver = new WikidataFilmAffinityResolver(
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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
