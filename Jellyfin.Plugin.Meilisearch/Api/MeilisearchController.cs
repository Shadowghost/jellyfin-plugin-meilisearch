using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="MeilisearchController"/> class.
    /// </summary>
    /// <param name="client">The Meilisearch client wrapper.</param>
    public MeilisearchController(MeilisearchClientWrapper client)
    {
        _client = client;
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
            Error: health.Error);

        return Ok(response);
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
