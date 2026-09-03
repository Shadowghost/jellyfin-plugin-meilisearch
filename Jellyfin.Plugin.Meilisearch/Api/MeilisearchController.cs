using System;
using System.Collections.Generic;
using System.Linq;
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
            EmbeddingModel: _embeddings.IsEnabled ? EmbeddingService.ActiveModel.DisplayName : null,
            EmbeddingModelDirectory: _embeddings.IsEnabled ? _embeddings.GetModelDirectory() : null,
            EmbeddingModelRebuildRequired: IsEmbeddingModelStale(),
            EmbeddingError: _embeddings.Error,
            EmbeddingCacheCount: _embeddings.CachedVectorCount,
            EmbeddingCacheHitRate: _embeddings.CacheHitRate,
            EmbeddingExecutionProvider: _embeddings.ActiveExecutionProvider?.ToString(),
            EmbeddingAvailableProviders: _embeddings.IsEnabled ? _embeddings.AvailableExecutionProviders : null,
            EmbeddingQueryTimeMilliseconds: _embeddings.AverageQueryEmbeddingMilliseconds,
            EmbeddingQueryCacheHitRate: _embeddings.QueryVectorCacheHitRate,
            SemanticRatioBalanced: _embeddings.IsSemanticRatioBalanced,
            MatchingStrategy: _client.EffectiveMatchingStrategy,
            AverageSearchTimeMilliseconds: _client.AverageSearchTimeMilliseconds,
            SearchCount: _client.SearchCount);

        return Ok(response);
    }

    /// <summary>
    /// Lists the embedding models this build can run.
    /// </summary>
    /// <response code="200">Models returned.</response>
    /// <returns>The available embedding models, in the order the settings page should list them.</returns>
    [HttpGet("EmbeddingModels")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<EmbeddingModelResponse>> GetEmbeddingModels()
        => Ok(EmbeddingModels.All
            .Select(model => new EmbeddingModelResponse(
                model.Id,
                model.DisplayName,
                model.Dimensions,
                model.ApproximateDownloadMegabytes,
                model.Repository))
            .ToList());

    /// <summary>
    /// Releases the embedding model from memory without turning semantic search off.
    /// </summary>
    /// <response code="200">The model was released, or there was nothing to release.</response>
    /// <response code="409">A reindex is running, or the model is still loading; nothing was released.</response>
    /// <returns>What happened.</returns>
    [HttpPost("UnloadEmbeddingModel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<UnloadOutcome> UnloadEmbeddingModel()
    {
        var outcome = _embeddings.RequestUnload();

        // A refusal is a conflict with state the caller has to resolve first - a running reindex, or
        // a load in progress - rather than a bad request or a success.
        return outcome is UnloadOutcome.ReindexRunning or UnloadOutcome.Busy
            ? Conflict(outcome)
            : Ok(outcome);
    }

    /// <summary>
    /// Discards every cached vector, so the next rebuild computes them again.
    /// </summary>
    /// <response code="200">The cache was cleared, or there was nothing to clear.</response>
    /// <response code="409">A reindex is running, or the model is still loading; nothing was cleared.</response>
    /// <returns>What happened, and how many vectors were discarded.</returns>
    [HttpPost("ClearVectorCache")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<ClearCacheResult> ClearVectorCache()
    {
        var result = _embeddings.ClearVectorCache();

        return result.Outcome is ClearCacheOutcome.ReindexRunning or ClearCacheOutcome.Busy
            ? Conflict(result)
            : Ok(result);
    }

    /// <summary>
    /// Determines whether the index was last built with a different embedding model than the one now
    /// selected, which leaves semantic search keyword-only until a rebuild.
    /// </summary>
    private static bool IsEmbeddingModelStale()
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.EnableSemanticSearch)
        {
            return false;
        }

        var indexed = configuration.IndexedEmbeddingModelId;
        return !string.IsNullOrEmpty(indexed)
            && !string.Equals(indexed, EmbeddingService.ActiveModel.IndexIdentity, StringComparison.Ordinal);
    }

    /// <summary>
    /// Drops the current Meilisearch connection so the next request reconnects, then reports the
    /// resulting health.
    /// </summary>
    /// <response code="200">Reconnect attempted; the payload describes the new state.</response>
    /// <returns>The connection state after reconnecting.</returns>
    /// <remarks>
    /// Rebuilding the <see cref="System.Net.Http.HttpClient"/> clears the pooled connection and the
    /// cached DNS entry, which is what makes a recreated Meilisearch container reachable again.
    /// Transient failures recover on their own; this is for seeing a fix take effect now.
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
    /// Queued through the task manager so it outlives this request and reports progress on the
    /// Scheduled Tasks page. A no-op if the task is already running.
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
