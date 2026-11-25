using System;
using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.FilmAffinity.Configuration;
using Jellyfin.Plugin.FilmAffinity.Services;
using Xunit;

namespace Jellyfin.Plugin.FilmAffinity.Tests;

/// <summary>
/// Unit tests for <see cref="FilmAffinityScoreParser"/>.
/// </summary>
public class FilmAffinityScoreParserTests
{
    [Fact]
    public void BuildRequestUri_EmptyEndpoint_UsesDefaultFilmAffinityHtmlUrl()
    {
        var uri = FilmAffinityScoreParser.BuildRequestUri(string.Empty, "809297", "en");
        Assert.Equal(new Uri("https://www.filmaffinity.com/en/film809297.html"), uri);
    }

    [Fact]
    public void BuildRequestUri_BaseUrlWithoutPlaceholder_AppendsScorePath()
    {
        var uri = FilmAffinityScoreParser.BuildRequestUri("http://localhost:8080/", "809297", "es");
        Assert.Equal(new Uri("http://localhost:8080/score/809297"), uri);
    }

    [Fact]
    public void BuildRequestUri_TemplateWithPlaceholders_ReplacesIdAndLang()
    {
        var uri = FilmAffinityScoreParser.BuildRequestUri("https://proxy.local/{lang}/film{id}.html", "809297", "es");
        Assert.Equal(new Uri("https://proxy.local/es/film809297.html"), uri);
    }

    [Fact]
    public void ParseJson_StandardPayload_ParsesRatingAndVotes()
    {
        var json = """{"rating": 9.0, "votes": 175432}""";
        var score = FilmAffinityScoreParser.Parse("809297", json, "application/json", ResponseParseMode.Auto);

        Assert.NotNull(score);
        Assert.Equal("809297", score.FilmAffinityId);
        Assert.Equal(9.0f, score.Rating);
        Assert.Equal(175432, score.VoteCount);
    }

    [Fact]
    public void ParseJson_SpanishDecimalCommaString_ParsesCorrectly()
    {
        var json = """{"score": "8,7", "vote_count": "120.450"}""";
        var score = FilmAffinityScoreParser.Parse("809297", json, null, ResponseParseMode.Auto);

        Assert.NotNull(score);
        Assert.Equal(8.7f, score.Rating);
        Assert.Equal(120450, score.VoteCount);
    }

    [Fact]
    public void ParseHtml_FilmAffinityMarkup_ParsesRatingAndVotes()
    {
        var html = """
            <div id="movie-rat-avg" itemprop="ratingValue" content="9.0">
                9,0
            </div>
            <div id="movie-count-rat">
                <span itemprop="ratingCount" content="182345">182.345</span> votos
            </div>
            """;

        var score = FilmAffinityScoreParser.Parse("809297", html, "text/html", ResponseParseMode.Auto);

        Assert.NotNull(score);
        Assert.Equal(9.0f, score.Rating);
        Assert.Equal(182345, score.VoteCount);
    }

    [Fact]
    public void ParseHtml_InnerTextFallback_ParsesCommaRating()
    {
        var html = """<div id="movie-rat-avg"> 7,8 </div>""";
        var score = FilmAffinityScoreParser.Parse("123456", html, "text/html", ResponseParseMode.Html);

        Assert.NotNull(score);
        Assert.Equal(7.8f, score.Rating);
        Assert.Null(score.VoteCount);
    }

    [Fact]
    public void IsCloudflareBlocked_ForbiddenWithCfRay_ReturnsTrue()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("cf-ray", "a5d9971a2c2ed943-MAD");

        Assert.True(FilmAffinityScoreParser.IsCloudflareBlocked(response, "<html>Forbidden</html>"));
    }

    [Fact]
    public void IsCloudflareBlocked_NormalOkResponse_ReturnsFalse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.False(FilmAffinityScoreParser.IsCloudflareBlocked(response, """{"rating": 8.2}"""));
    }

    [Fact]
    public void IsCloudflareBlocked_OkResponseWithPassiveJsdScript_ReturnsFalse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("cf-ray", "a6035ee40d500bf3-MAD");
        var html = """<html><body><p>No results</p><script src="/cdn-cgi/challenge-platform/scripts/jsd/main.js"></script></body></html>""";

        Assert.False(FilmAffinityScoreParser.IsCloudflareBlocked(response, html));
    }

    [Fact]
    public void IsCloudflareBlocked_ActiveChallengePlatformOrchestrator_ReturnsTrue()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var html = """<html><head><script src="/cdn-cgi/challenge-platform/h/b/orchestrate/chl_page/v1"></script></head></html>""";

        Assert.True(FilmAffinityScoreParser.IsCloudflareBlocked(response, html));
    }
}
