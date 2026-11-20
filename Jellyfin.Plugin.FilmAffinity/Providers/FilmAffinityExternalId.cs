using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.FilmAffinity.Providers;

/// <summary>
/// Registers FilmAffinity as a clickable external link on Movies and TV Series.
/// </summary>
public class FilmAffinityExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "FilmAffinity";

    /// <inheritdoc />
    public string Key => Plugin.ProviderKey;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => null;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item)
    {
        return item is Movie or Series;
    }
}
