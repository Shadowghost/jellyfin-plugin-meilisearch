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

        // The incremental sync is both a scheduled task and what the post-scan hook runs, so it is
        // registered as a concrete type and surfaced as IScheduledTask through the same instance.
        serviceCollection.AddSingleton<IncrementalReindexTask>();
        serviceCollection.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<IncrementalReindexTask>());

        // Jellyfin collects post-scan tasks by scanning plugin assemblies and instantiating them
        // through the container, so this registration exists to supply the constructor arguments.
        serviceCollection.AddSingleton<LibraryScanSyncTask>();
    }
}
