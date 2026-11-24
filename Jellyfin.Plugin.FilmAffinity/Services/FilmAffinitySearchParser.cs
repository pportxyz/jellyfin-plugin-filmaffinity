using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.FilmAffinity.Models;

namespace Jellyfin.Plugin.FilmAffinity.Services;

/// <summary>
/// Pure parsing and matching helpers for FilmAffinity title search results.
/// </summary>
public static partial class FilmAffinitySearchParser
{
    private static readonly string[] SeriesTypeMarkers =
    [
        "tv series",
        "miniseries",
        "tv miniseries",
        "serie de tv",
        "miniserie",
        "miniserie de tv"
    ];

    private static readonly string[] ShortFilmTypeMarkers =
    [
        "short film",
        "cortometraje"
    ];

    /// <summary>
    /// Builds the FilmAffinity title search URI for the given title and language.
    /// </summary>
    /// <param name="title">The title to search for.</param>
    /// <param name="language">The FilmAffinity site language (<c>en</c> or <c>es</c>).</param>
    /// <returns>The resolved search <see cref="Uri"/>.</returns>
    public static Uri BuildSearchUri(string title, string? language)
    {
        var lang = FilmAffinityScoreParser.NormalizeLanguage(language);
        var url = $"https://www.filmaffinity.com/{Uri.EscapeDataString(lang)}/search.php?stext={Uri.EscapeDataString(title.Trim())}&stype=title";
        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>
    /// Determines whether direct FilmAffinity title search is appropriate for the configured score endpoint.
    /// Returns <c>false</c> when a user has explicitly configured a non-FilmAffinity proxy endpoint.
    /// </summary>
    /// <param name="scoreEndpointUrl">The configured <c>ScoreEndpointUrl</c>.</param>
    /// <returns><c>true</c> if direct FilmAffinity search should be used; otherwise <c>false</c>.</returns>
    public static bool CanUseDirectSearch(string? scoreEndpointUrl)
    {
        if (string.IsNullOrWhiteSpace(scoreEndpointUrl))
        {
            return true;
        }

        var probe = scoreEndpointUrl.Trim()
            .Replace("{lang}", "en", StringComparison.OrdinalIgnoreCase)
            .Replace("{id}", "123456", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("filmaffinity.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".filmaffinity.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes a title for strict comparison by removing trailing type markers, diacritics, and punctuation.
    /// </summary>
    /// <param name="title">The raw title.</param>
    /// <returns>The normalized title, or an empty string if input is blank.</returns>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var cleaned = TrailingTypeSuffixRegex().Replace(title.Trim(), string.Empty);
        var decomposed = cleaned.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = true;

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Parses candidate titles from a FilmAffinity search response (or direct redirected film page).
    /// </summary>
    /// <param name="html">The raw HTML body.</param>
    /// <param name="finalUri">Optional final request URI after redirects.</param>
    /// <returns>A list of parsed candidates in page order.</returns>
    public static IReadOnlyList<FilmAffinitySearchCandidate> ParseCandidates(string html, Uri? finalUri = null)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        if (finalUri is not null)
        {
            var directMatch = DirectFilmUriRegex().Match(finalUri.AbsolutePath);
            if (directMatch.Success)
            {
                var directCandidate = ParseDirectFilmPage(directMatch.Groups[1].Value, html);
                return directCandidate is not null ? [directCandidate] : [];
            }
        }

        var cardMatches = MovieCardIdRegex().Matches(html);
        if (cardMatches.Count == 0)
        {
            return [];
        }

        var candidates = new List<FilmAffinitySearchCandidate>(cardMatches.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < cardMatches.Count; i++)
        {
            var match = cardMatches[i];
            var id = match.Groups[1].Value;
            if (!seenIds.Add(id))
            {
                continue;
            }

            var start = match.Index;
            var end = i + 1 < cardMatches.Count ? cardMatches[i + 1].Index : html.Length;
            var slice = html.AsSpan(start, end - start).ToString();

            var titleMatch = McTitleRegex().Match(slice);
            if (!titleMatch.Success)
            {
                continue;
            }

            var rawTitle = StripHtmlTags(titleMatch.Groups[1].Value);
            var title = WebUtility.HtmlDecode(rawTitle).Trim();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            int? year = null;
            var yearMatch = McYearRegex().Match(slice);
            if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear))
            {
                year = parsedYear;
            }

            var typeMatches = TypeBadgeRegex().Matches(slice);
            var isSeries = false;
            var isShortFilm = false;
            foreach (Match typeMatch in typeMatches)
            {
                var badge = WebUtility.HtmlDecode(typeMatch.Groups[1].Value).Trim();
                if (IsSeriesMarker(badge))
                {
                    isSeries = true;
                }

                if (IsShortFilmMarker(badge))
                {
                    isShortFilm = true;
                }
            }

            if (!isSeries)
            {
                var altMatch = PosterAltRegex().Match(slice);
                if (altMatch.Success && ContainsSeriesParenSuffix(WebUtility.HtmlDecode(altMatch.Groups[1].Value)))
                {
                    isSeries = true;
                }
            }

            if (!isSeries && ContainsSeriesParenSuffix(title))
            {
                isSeries = true;
            }

            candidates.Add(new FilmAffinitySearchCandidate(id, title, year, isSeries, isShortFilm));
        }

        return candidates;
    }

    /// <summary>
    /// Selects the best unambiguous FilmAffinity ID from parsed search candidates.
    /// </summary>
    /// <param name="candidates">The parsed search candidates.</param>
    /// <param name="title">The item's primary title.</param>
    /// <param name="originalTitle">Optional original title.</param>
    /// <param name="isSeries"><c>true</c> for TV series; <c>false</c> for movies.</param>
    /// <param name="productionYear">Optional production year.</param>
    /// <returns>The matched FilmAffinity ID, or <c>null</c> if no unambiguous match exists.</returns>
    public static string? SelectBestMatch(
        IReadOnlyList<FilmAffinitySearchCandidate> candidates,
        string title,
        string? originalTitle,
        bool isSeries,
        int? productionYear)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var normalizedTargets = new HashSet<string>(StringComparer.Ordinal);
        var normalizedTitle = NormalizeTitle(title);
        if (!string.IsNullOrEmpty(normalizedTitle))
        {
            normalizedTargets.Add(normalizedTitle);
        }

        var normalizedOriginal = NormalizeTitle(originalTitle);
        if (!string.IsNullOrEmpty(normalizedOriginal))
        {
            normalizedTargets.Add(normalizedOriginal);
        }

        if (normalizedTargets.Count == 0)
        {
            return null;
        }

        var exactTitleMatches = candidates
            .Where(c => c.IsSeries == isSeries && normalizedTargets.Contains(NormalizeTitle(c.Title)))
            .ToList();

        if (exactTitleMatches.Count == 0)
        {
            return null;
        }

        if (productionYear.HasValue)
        {
            var exactYear = exactTitleMatches
                .Where(c => c.Year == productionYear.Value)
                .OrderBy(c => !isSeries && c.IsShortFilm ? 1 : 0)
                .ToList();

            if (exactYear.Count > 0)
            {
                return exactYear[0].Id;
            }

            var adjacentYear = exactTitleMatches
                .Where(c => c.Year.HasValue && Math.Abs(c.Year.Value - productionYear.Value) <= 1)
                .OrderBy(c => !isSeries && c.IsShortFilm ? 1 : 0)
                .ToList();

            if (adjacentYear.Count == 1)
            {
                return adjacentYear[0].Id;
            }

            return null;
        }

        var preferred = isSeries
            ? exactTitleMatches
            : exactTitleMatches.Where(c => !c.IsShortFilm).ToList();

        if (preferred.Count == 1)
        {
            return preferred[0].Id;
        }

        return exactTitleMatches.Count == 1 ? exactTitleMatches[0].Id : null;
    }

    private static FilmAffinitySearchCandidate? ParseDirectFilmPage(string id, string html)
    {
        var titleMatch = DirectMainTitleRegex().Match(html);
        if (!titleMatch.Success)
        {
            return null;
        }

        var rawTitle = StripHtmlTags(titleMatch.Groups[1].Value);
        var title = WebUtility.HtmlDecode(rawTitle).Trim();
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        int? year = null;
        var yearMatch = DirectYearRegex().Match(html);
        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear))
        {
            year = parsedYear;
        }

        var isSeries = ContainsSeriesParenSuffix(title);
        var isShortFilm = title.Contains("(S)", StringComparison.OrdinalIgnoreCase)
            || title.Contains("(C)", StringComparison.OrdinalIgnoreCase);

        return new FilmAffinitySearchCandidate(id, title, year, isSeries, isShortFilm);
    }

    private static bool IsSeriesMarker(string value)
    {
        foreach (var marker in SeriesTypeMarkers)
        {
            if (value.Equals(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsShortFilmMarker(string value)
    {
        foreach (var marker in ShortFilmTypeMarkers)
        {
            if (value.Equals(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSeriesParenSuffix(string value)
    {
        return value.Contains("(TV Series)", StringComparison.OrdinalIgnoreCase)
            || value.Contains("(TV Miniseries)", StringComparison.OrdinalIgnoreCase)
            || value.Contains("(Serie de TV)", StringComparison.OrdinalIgnoreCase)
            || value.Contains("(Miniserie de TV)", StringComparison.OrdinalIgnoreCase)
            || value.Contains("(Miniserie)", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripHtmlTags(string html)
    {
        return HtmlTagRegex().Replace(html, string.Empty);
    }

    [GeneratedRegex(@"/film(\d+)\.html$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectFilmUriRegex();

    [GeneratedRegex(@"<h1[^>]*id=[""']main-title[""'][^>]*>.*?<span[^>]*>(.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DirectMainTitleRegex();

    [GeneratedRegex(@"itemprop=[""']datePublished[""'][^>]*>\s*(\d{4})\s*<", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectYearRegex();

    [GeneratedRegex(@"data-movie-id=[""'](\d+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MovieCardIdRegex();

    [GeneratedRegex(@"class=[""'][^""']*\bmc-title\b[^""']*[""'][^>]*>.*?<a\b[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex McTitleRegex();

    [GeneratedRegex(@"class=[""'][^""']*\bmc-year\b[^""']*[""'][^>]*>\s*(\d{4})\s*<", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex McYearRegex();

    [GeneratedRegex(@"<span[^>]*class=[""'][^""']*\btype\b[^""']*[""'][^>]*>(.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TypeBadgeRegex();

    [GeneratedRegex(@"\balt=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PosterAltRegex();

    [GeneratedRegex(@"\s*\((?:TV\s+Series|TV\s+Miniseries|TV|Serie\s+de\s+TV|Miniserie(?:\s+de\s+TV)?|S|C)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingTypeSuffixRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();
}
