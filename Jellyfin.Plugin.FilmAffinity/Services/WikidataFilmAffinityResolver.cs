using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FilmAffinity.Services;

/// <summary>
/// Resolves FilmAffinity IDs (Wikidata property P480) from IMDb or TMDb identifiers using the Wikidata SPARQL endpoint.
/// </summary>
public class WikidataFilmAffinityResolver
{
    /// <summary>
    /// The named <see cref="HttpClient"/> registered for Wikidata requests.
    /// </summary>
    public const string HttpClientName = "FilmAffinityWikidata";

    private const string SparqlEndpoint = "https://query.wikidata.org/sparql";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FilmAffinityCacheService _cache;
    private readonly ILogger<WikidataFilmAffinityResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WikidataFilmAffinityResolver"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="cache">The cache service.</param>
    /// <param name="logger">The logger.</param>
    public WikidataFilmAffinityResolver(
        IHttpClientFactory httpClientFactory,
        FilmAffinityCacheService cache,
        ILogger<WikidataFilmAffinityResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a FilmAffinity ID from an item's provider IDs.
    /// Priority: existing FilmAffinity ID -> IMDb (P345) -> TMDb Movie (P4947) or TMDb TV (P4983).
    /// </summary>
    /// <param name="providerIds">The Jellyfin item's provider IDs dictionary.</param>
    /// <param name="isSeries"><c>true</c> for TV series (uses TMDb TV P4983); <c>false</c> for movies (uses TMDb Movie P4947).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The numeric FilmAffinity ID if resolved; otherwise <c>null</c>.</returns>
    public async Task<string?> ResolveAsync(
        IReadOnlyDictionary<string, string>? providerIds,
        bool isSeries,
        CancellationToken cancellationToken)
    {
        if (providerIds is null || providerIds.Count == 0)
        {
            return null;
        }

        if (providerIds.TryGetValue(Plugin.ProviderKey, out var existingId)
            && !string.IsNullOrWhiteSpace(existingId))
        {
            return existingId.Trim();
        }

        if (providerIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdbId)
            && !string.IsNullOrWhiteSpace(imdbId))
        {
            var resolved = await QueryByPropertyAsync("P345", imdbId.Trim(), cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolved))
            {
                return resolved;
            }
        }

        if (providerIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId)
            && !string.IsNullOrWhiteSpace(tmdbId))
        {
            var property = isSeries ? "P4983" : "P4947";
            var resolved = await QueryByPropertyAsync(property, tmdbId.Trim(), cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the first FilmAffinity ID (<c>faId</c>) from a Wikidata SPARQL JSON response.
    /// </summary>
    /// <param name="sparqlJson">The raw SPARQL JSON string.</param>
    /// <returns>The FilmAffinity ID if found; otherwise <c>null</c>.</returns>
    public static string? ParseSparqlResponse(string sparqlJson)
    {
        if (string.IsNullOrWhiteSpace(sparqlJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(sparqlJson);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || !results.TryGetProperty("bindings", out var bindings)
                || bindings.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var binding in bindings.EnumerateArray())
            {
                if (binding.TryGetProperty("faId", out var faIdElement)
                    && faIdElement.TryGetProperty("value", out var valueElement)
                    && valueElement.ValueKind == JsonValueKind.String)
                {
                    var value = valueElement.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private async Task<string?> QueryByPropertyAsync(
        string wikidataProperty,
        string externalId,
        CancellationToken cancellationToken)
    {
        var cacheKey = wikidataProperty + ":" + externalId;
        if (_cache.TryGetFilmAffinityId(cacheKey, out var cachedId))
        {
            return cachedId;
        }

        // Escape any quotes or backslashes defensively before embedding into SPARQL literal.
        var escapedId = externalId
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        var query = $"SELECT ?faId WHERE {{ ?item wdt:{wikidataProperty} \"{escapedId}\" ; wdt:P480 ?faId . }} LIMIT 1";
        var requestUri = new Uri($"{SparqlEndpoint}?format=json&query={Uri.EscapeDataString(query)}");

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.ParseAdd("application/sparql-results+json");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Wikidata SPARQL lookup failed for {Property}={ExternalId} with HTTP {StatusCode}",
                    wikidataProperty,
                    externalId,
                    (int)response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var filmAffinityId = ParseSparqlResponse(content);
            _cache.SetFilmAffinityId(cacheKey, filmAffinityId);
            return filmAffinityId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Wikidata SPARQL request error for {Property}={ExternalId}",
                wikidataProperty,
                externalId);
            return null;
        }
    }
}
