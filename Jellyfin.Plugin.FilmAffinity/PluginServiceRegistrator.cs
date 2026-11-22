using System;
using Jellyfin.Plugin.FilmAffinity.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FilmAffinity;

/// <summary>
/// Registers plugin services with Jellyfin's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(WikidataFilmAffinityResolver.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Jellyfin.Plugin.FilmAffinity/1.0 (https://github.com/pportxyz/jellyfin-plugin-filmaffinity)");
        });

        serviceCollection.AddHttpClient(FilmAffinityScoreClient.HttpClientName);

        serviceCollection.AddSingleton<FilmAffinityCacheService>();
        serviceCollection.AddSingleton<FilmAffinityRefreshStateStore>();
        serviceCollection.AddSingleton<WikidataFilmAffinityResolver>();
        serviceCollection.AddSingleton<FilmAffinityScoreClient>();
        serviceCollection.AddSingleton<FilmAffinityRatingService>();
    }
}
