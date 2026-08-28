using Jellyfin.Plugin.Meilisearch.Embeddings;
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

        // Registered before the index service, which depends on it. The hosted-service registration
        // resolves the same singleton so the model is loaded once, not once per consumer.
        serviceCollection.AddSingleton<EmbeddingService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<EmbeddingService>());

        serviceCollection.AddSingleton<MeilisearchIndexService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<MeilisearchIndexService>());

        serviceCollection.AddHostedService<MeilisearchHealthMonitor>();

        serviceCollection.AddSingleton<IScheduledTask, ReindexTask>();
        serviceCollection.AddSingleton<IScheduledTask, DownloadEmbeddingModelTask>();

        serviceCollection.AddSingleton<IScheduledTask, IncrementalReindexTask>();

        // Jellyfin collects post-scan tasks by scanning plugin assemblies and instantiating them
        // through the container, so this registration exists to supply the constructor arguments.
        serviceCollection.AddSingleton<LibraryScanSyncTask>();
    }
}
