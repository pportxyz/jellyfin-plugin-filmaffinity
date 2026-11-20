using System;
using System.Collections.Generic;
using Jellyfin.Plugin.FilmAffinity.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FilmAffinity.Providers;

/// <summary>
/// Provides clickable FilmAffinity external URLs for Movies and TV Series.
/// </summary>
public class FilmAffinityExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc />
    public string Name => "FilmAffinity";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item is not (Movie or Series))
        {
            yield break;
        }

        if (item.TryGetProviderId(Plugin.ProviderKey, out var filmAffinityId)
            && !string.IsNullOrWhiteSpace(filmAffinityId))
        {
            var lang = FilmAffinityScoreParser.NormalizeLanguage(Plugin.Instance?.Configuration.LinkLanguage);
            yield return $"https://www.filmaffinity.com/{lang}/film{filmAffinityId.Trim()}.html";
        }
    }
}
