using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FilmAffinity.Configuration;

/// <summary>
/// Configuration for the FilmAffinity Ratings plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        ScoreEndpointUrl = string.Empty;
        ResponseMode = ResponseParseMode.Auto;
        AuthHeaderName = string.Empty;
        AuthHeaderValue = string.Empty;
        LinkLanguage = "es";
        MinVotes = 50;
        SetCriticRating = false;
        ThrottleMs = 1500;
        RequestTimeoutSeconds = 20;
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    }

    /// <summary>
    /// Gets or sets the external score endpoint URL or URL template.
    /// Supports <c>{id}</c> and <c>{lang}</c> placeholders (e.g. <c>http://localhost:8080/score/{id}</c>
    /// or <c>https://my-proxy.local/{lang}/film{id}.html</c>).
    /// If no <c>{id}</c> placeholder is present, <c>/score/{id}</c> is appended.
    /// If empty, defaults to <c>https://www.filmaffinity.com/{lang}/film{id}.html</c>.
    /// </summary>
    public string ScoreEndpointUrl { get; set; }

    /// <summary>
    /// Gets or sets how score endpoint responses are parsed.
    /// </summary>
    public ResponseParseMode ResponseMode { get; set; }

    /// <summary>
    /// Gets or sets an optional HTTP header name for authenticating against the score endpoint (e.g. <c>Authorization</c> or <c>X-API-Key</c>).
    /// </summary>
    public string AuthHeaderName { get; set; }

    /// <summary>
    /// Gets or sets the optional HTTP header value paired with <see cref="AuthHeaderName"/>.
    /// </summary>
    public string AuthHeaderValue { get; set; }

    /// <summary>
    /// Gets or sets the language code used for FilmAffinity external links and default HTML URLs (<c>es</c> or <c>en</c>).
    /// </summary>
    public string LinkLanguage { get; set; }

    /// <summary>
    /// Gets or sets the minimum vote count required before writing a FilmAffinity rating.
    /// Scores whose vote count is reported and below this threshold are ignored.
    /// </summary>
    public int MinVotes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to also populate Jellyfin's <c>CriticRating</c> field (FilmAffinity rating * 10).
    /// </summary>
    public bool SetCriticRating { get; set; }

    /// <summary>
    /// Gets or sets the minimum delay in milliseconds between consecutive score endpoint requests.
    /// </summary>
    public int ThrottleMs { get; set; }

    /// <summary>
    /// Gets or sets the HTTP request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the User-Agent header sent to the score endpoint.
    /// </summary>
    public string UserAgent { get; set; }
}
