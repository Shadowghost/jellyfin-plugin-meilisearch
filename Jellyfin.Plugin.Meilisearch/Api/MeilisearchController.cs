using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Meilisearch.Embeddings;
using Jellyfin.Plugin.Meilisearch.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Meilisearch.Api;

/// <summary>
/// REST API controller exposing Meilisearch plugin status and diagnostics endpoints.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/Meilisearch")]
[Produces("application/json")]
public class MeilisearchController : ControllerBase
{
    private readonly MeilisearchClientWrapper _client;
    private readonly EmbeddingService _embeddings;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeilisearchController"/> class.
    /// </summary>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="embeddings">The embedding service.</param>
    /// <param name="taskManager">The scheduled task manager, used to start a reindex on request.</param>
    public MeilisearchController(
        MeilisearchClientWrapper client,
        EmbeddingService embeddings,
        ITaskManager taskManager)
    {
        _client = client;
        _embeddings = embeddings;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Gets aggregated status information about the Meilisearch index and server.
    /// </summary>
    /// <response code="200">Stats returned.</response>
    /// <returns>The current Meilisearch index and health status.</returns>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<MeilisearchStatsResponse>> GetStats()
    {
        var cancellationToken = HttpContext?.RequestAborted ?? CancellationToken.None;

        var health = await _client.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        var stats = await _client.GetIndexStatsAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, int>? fieldDistribution = null;
        if (stats?.FieldDistribution is { Count: > 0 } source)
        {
            fieldDistribution = new Dictionary<string, int>(source.Count, StringComparer.Ordinal);
            foreach (var entry in source)
            {
                fieldDistribution[entry.Key] = entry.Value;
            }
        }

        long? documentCount = stats?.NumberOfDocuments;
        long? databaseSize = stats?.RawDocumentDbSize;
        bool? isIndexing = stats?.IsIndexing;

        var response = new MeilisearchStatsResponse(
            DocumentCount: documentCount,
            IsIndexing: isIndexing,
            DatabaseSize: databaseSize,
            FieldDistribution: fieldDistribution,
            IsHealthy: health.IsHealthy,
            IsAuthenticated: health.IsAuthenticated,
            LastIncrementalReindexUtc: Plugin.Instance?.Configuration.LastIncrementalReindexUtc,
            Error: health.Error,
            SemanticSearchEnabled: _embeddings.IsEnabled,
            EmbeddingState: _embeddings.State.ToString(),
            EmbeddingModelDirectory: _embeddings.IsEnabled ? _embeddings.GetModelDirectory() : null,
            EmbeddingError: _embeddings.Error,
            EmbeddingCacheCount: _embeddings.CachedVectorCount,
            EmbeddingCacheHitRate: _embeddings.CacheHitRate,
            MatchingStrategy: _client.EffectiveMatchingStrategy,
            AverageSearchTimeMilliseconds: _client.AverageSearchTimeMilliseconds,
            SearchCount: _client.SearchCount);

        return Ok(response);
    }

    /// <summary>
    /// Drops the current Meilisearch connection so the next request reconnects, then reports the
    /// resulting health.
    /// </summary>
    /// <response code="200">Reconnect attempted; the payload describes the new state.</response>
    /// <returns>The connection state after reconnecting.</returns>
    /// <remarks>
    /// Recreating the client rebuilds its <see cref="System.Net.Http.HttpClient"/>, which clears the
    /// pooled connection and the cached DNS entry - the reason a recreated Meilisearch container is
    /// reachable again without restarting Jellyfin. Transient failures already recover on their own;
    /// this exists for the case where an admin has just fixed something and wants to see it now.
    /// </remarks>
    [HttpPost("Reconnect")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<MeilisearchTestConnectionResponse>> Reconnect()
    {
        var cancellationToken = HttpContext?.RequestAborted ?? CancellationToken.None;
        _client.Reconnect();
        var health = await _client.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new MeilisearchTestConnectionResponse(health.IsHealthy, health.IsAuthenticated, health.Error));
    }

    /// <summary>
    /// Starts the full reindex scheduled task.
    /// </summary>
    /// <response code="204">The reindex task was started.</response>
    /// <returns>No content.</returns>
    /// <remarks>
    /// Queued through the task manager rather than run inline, so it survives this HTTP request and
    /// reports progress on the Scheduled Tasks page. If the task is already running the request is a
    /// no-op.
    /// </remarks>
    [HttpPost("Reindex")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Reindex()
    {
        _taskManager.QueueIfNotRunning<ReindexTask>();
        return NoContent();
    }

    /// <summary>
    /// Tests connectivity and authentication against the currently configured Meilisearch server.
    /// </summary>
    /// <response code="200">Connection test result.</response>
    /// <returns>The result of the connection and authentication test.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<MeilisearchTestConnectionResponse>> TestConnection()
    {
        var cancellationToken = HttpContext?.RequestAborted ?? CancellationToken.None;
        var health = await _client.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new MeilisearchTestConnectionResponse(health.IsHealthy, health.IsAuthenticated, health.Error));
    }
}
