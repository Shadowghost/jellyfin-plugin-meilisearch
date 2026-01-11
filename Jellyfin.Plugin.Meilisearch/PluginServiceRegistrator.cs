using Jellyfin.Plugin.Meilisearch.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        serviceCollection.AddSingleton<MeilisearchIndexService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<MeilisearchIndexService>());

        serviceCollection.AddHostedService<MeilisearchHealthMonitor>();

        serviceCollection.AddSingleton<IScheduledTask, ReindexTask>();
        serviceCollection.AddSingleton<IScheduledTask, IncrementalReindexTask>();
    }
}
