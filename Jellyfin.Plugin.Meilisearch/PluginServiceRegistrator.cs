using Jellyfin.Plugin.Meilisearch.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Registers the plugin's services with the DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MeilisearchClientWrapper>();
        serviceCollection.AddHostedService<MeilisearchIndexService>();
        serviceCollection.AddSingleton<IScheduledTask, ReindexTask>();
    }
}
