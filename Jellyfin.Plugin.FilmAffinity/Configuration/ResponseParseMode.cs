namespace Jellyfin.Plugin.FilmAffinity.Configuration;

/// <summary>
/// Controls how responses from the configured score endpoint are parsed.
/// </summary>
public enum ResponseParseMode
{
    /// <summary>
    /// Automatically detect JSON vs FilmAffinity HTML from content type and body.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Parse response as JSON (<c>{"rating": 8.7, "votes": 145000}</c>).
    /// </summary>
    Json = 1,

    /// <summary>
    /// Parse response as FilmAffinity HTML (<c>itemprop="ratingValue"</c> / <c>#movie-rat-avg</c>).
    /// </summary>
    Html = 2
}
