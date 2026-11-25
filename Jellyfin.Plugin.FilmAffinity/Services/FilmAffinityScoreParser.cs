using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.FilmAffinity.Configuration;
using Jellyfin.Plugin.FilmAffinity.Models;

namespace Jellyfin.Plugin.FilmAffinity.Services;

/// <summary>
/// Pure parsing helpers for FilmAffinity score URLs, JSON API responses, FilmAffinity HTML pages, and Cloudflare blocks.
/// </summary>
public static partial class FilmAffinityScoreParser
{
    private const string DefaultFilmAffinityTemplate = "https://www.filmaffinity.com/{lang}/film{id}.html";

    /// <summary>
    /// Builds the concrete request URI for a FilmAffinity ID given the configured endpoint template and language.
    /// </summary>
    /// <param name="endpointTemplate">The configured endpoint URL or template.</param>
    /// <param name="filmAffinityId">The numeric FilmAffinity ID.</param>
    /// <param name="language">The language code (<c>es</c> or <c>en</c>).</param>
    /// <returns>The resolved <see cref="Uri"/>.</returns>
    public static Uri BuildRequestUri(string? endpointTemplate, string filmAffinityId, string? language)
    {
        var lang = NormalizeLanguage(language);
        var template = string.IsNullOrWhiteSpace(endpointTemplate)
            ? DefaultFilmAffinityTemplate
            : endpointTemplate.Trim();

        string resolved;
        if (template.Contains("{id}", StringComparison.OrdinalIgnoreCase))
        {
            resolved = template
                .Replace("{id}", Uri.EscapeDataString(filmAffinityId), StringComparison.OrdinalIgnoreCase)
                .Replace("{lang}", Uri.EscapeDataString(lang), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            resolved = template.TrimEnd('/') + "/score/" + Uri.EscapeDataString(filmAffinityId);
        }

        return new Uri(resolved, UriKind.Absolute);
    }

    /// <summary>
    /// Normalizes a FilmAffinity language code to <c>es</c> or <c>en</c>.
    /// </summary>
    /// <param name="language">The configured language string.</param>
    /// <returns><c>en</c> if English is requested; otherwise <c>es</c>.</returns>
    public static string NormalizeLanguage(string? language)
    {
        return string.Equals(language?.Trim(), "en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "es";
    }

    /// <summary>
    /// Detects whether an HTTP response appears to be a Cloudflare challenge or block.
    /// </summary>
    /// <param name="response">The HTTP response message.</param>
    /// <param name="body">The response body text.</param>
    /// <returns><c>true</c> if Cloudflare blocking is detected; otherwise <c>false</c>.</returns>
    public static bool IsCloudflareBlocked(HttpResponseMessage response, string body)
    {
        ArgumentNullException.ThrowIfNull(response);

        var hasCloudflareHeader =
            response.Headers.Contains("cf-ray")
            || response.Headers.Contains("cf-mitigated")
            || (response.Headers.Server?.ToString().Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ?? false);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
        {
            if (hasCloudflareHeader)
            {
                return true;
            }
        }

        if (response.Headers.Contains("cf-mitigated"))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(body)
            && (body.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase)
                || body.Contains("Attention Required! | Cloudflare", StringComparison.OrdinalIgnoreCase)
                || body.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
                || body.Contains("/cdn-cgi/challenge-platform/h/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a FilmAffinity score from a response body according to the configured parse mode and content type.
    /// </summary>
    /// <param name="filmAffinityId">The FilmAffinity ID.</param>
    /// <param name="body">The response body string.</param>
    /// <param name="contentType">Optional response MIME type.</param>
    /// <param name="mode">The configured parse mode.</param>
    /// <returns>A parsed <see cref="FilmAffinityScore"/>, or <c>null</c> if parsing fails.</returns>
    public static FilmAffinityScore? Parse(
        string filmAffinityId,
        string body,
        string? contentType,
        ResponseParseMode mode)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return mode switch
        {
            ResponseParseMode.Json => ParseJson(filmAffinityId, body),
            ResponseParseMode.Html => ParseHtml(filmAffinityId, body),
            _ => ShouldParseAsJson(body, contentType)
                ? ParseJson(filmAffinityId, body)
                : ParseHtml(filmAffinityId, body)
        };
    }

    /// <summary>
    /// Parses a JSON payload containing a FilmAffinity rating and optional vote count.
    /// </summary>
    /// <param name="filmAffinityId">The FilmAffinity ID.</param>
    /// <param name="json">The raw JSON string.</param>
    /// <returns>A parsed <see cref="FilmAffinityScore"/>, or <c>null</c> if no valid rating was found.</returns>
    public static FilmAffinityScore? ParseJson(string filmAffinityId, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryExtractFloat(root, out var rating, "rating", "score", "average", "ratingValue"))
            {
                return null;
            }

            if (rating is < 0f or > 10f)
            {
                return null;
            }

            int? votes = TryExtractInt(root, out var parsedVotes, "votes", "voteCount", "vote_count", "ratingCount", "count")
                ? parsedVotes
                : null;

            return new FilmAffinityScore(filmAffinityId, rating, votes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a FilmAffinity HTML page for its community rating and optional vote count.
    /// </summary>
    /// <param name="filmAffinityId">The FilmAffinity ID.</param>
    /// <param name="html">The raw HTML string.</param>
    /// <returns>A parsed <see cref="FilmAffinityScore"/>, or <c>null</c> if no valid rating was found.</returns>
    public static FilmAffinityScore? ParseHtml(string filmAffinityId, string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        string? rawRating = null;

        var movieRatAvgMatch = MovieRatAvgContentRegex().Match(html);
        if (movieRatAvgMatch.Success)
        {
            rawRating = movieRatAvgMatch.Groups["val"].Value;
        }
        else
        {
            var ratingValueMatch = RatingValueRegex().Match(html);
            if (ratingValueMatch.Success)
            {
                rawRating = ratingValueMatch.Groups["val"].Value;
            }
            else
            {
                var movieRatInnerMatch = MovieRatAvgInnerTextRegex().Match(html);
                if (movieRatInnerMatch.Success)
                {
                    rawRating = movieRatInnerMatch.Groups["val"].Value;
                }
            }
        }

        if (!TryParseRatingFloat(rawRating, out var rating) || rating is < 0f or > 10f)
        {
            return null;
        }

        int? votes = null;
        var votesMatch = RatingCountContentRegex().Match(html);
        if (!votesMatch.Success)
        {
            votesMatch = RatingCountInnerTextRegex().Match(html);
        }

        if (votesMatch.Success && TryParseVoteInt(votesMatch.Groups["val"].Value, out var parsedVotes))
        {
            votes = parsedVotes;
        }

        return new FilmAffinityScore(filmAffinityId, rating, votes);
    }

    private static bool ShouldParseAsJson(string body, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = body.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '{';
    }

    private static bool TryExtractFloat(JsonElement obj, out float value, params string[] names)
    {
        foreach (var property in obj.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetSingle(out value))
                {
                    return true;
                }

                if (property.Value.ValueKind == JsonValueKind.String
                    && TryParseRatingFloat(property.Value.GetString(), out value))
                {
                    return true;
                }
            }
        }

        value = 0f;
        return false;
    }

    private static bool TryExtractInt(JsonElement obj, out int value, params string[] names)
    {
        foreach (var property in obj.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out value))
                {
                    return true;
                }

                if (property.Value.ValueKind == JsonValueKind.String
                    && TryParseVoteInt(property.Value.GetString(), out value))
                {
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }

    private static bool TryParseRatingFloat(string? raw, out float value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = 0f;
            return false;
        }

        var normalized = raw.Trim().Replace(',', '.');
        return float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseVoteInt(string? raw, out int value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = 0;
            return false;
        }

        var digitsOnly = NonDigitRegex().Replace(raw, string.Empty);
        return int.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex(@"id\s*=\s*[""']movie-rat-avg[""'][^>]*content\s*=\s*[""'](?<val>[0-9]+(?:[.,][0-9]+)?)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MovieRatAvgContentRegex();

    [GeneratedRegex(@"itemprop\s*=\s*[""']ratingValue[""'][^>]*content\s*=\s*[""'](?<val>[0-9]+(?:[.,][0-9]+)?)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex RatingValueRegex();

    [GeneratedRegex(@"id\s*=\s*[""']movie-rat-avg[""'][^>]*>\s*(?<val>[0-9]+(?:[.,][0-9]+)?)\s*<", RegexOptions.IgnoreCase)]
    private static partial Regex MovieRatAvgInnerTextRegex();

    [GeneratedRegex(@"itemprop\s*=\s*[""']ratingCount[""'][^>]*content\s*=\s*[""'](?<val>[0-9.,\s]+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex RatingCountContentRegex();

    [GeneratedRegex(@"itemprop\s*=\s*[""']ratingCount[""'][^>]*>\s*(?<val>[0-9.,\s]+)\s*<", RegexOptions.IgnoreCase)]
    private static partial Regex RatingCountInnerTextRegex();

    [GeneratedRegex(@"[^\d]")]
    private static partial Regex NonDigitRegex();
}
