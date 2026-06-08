# Jellyfin Meilisearch Plugin

---

A Jellyfin plugin that integrates [Meilisearch](https://www.meilisearch.com/) as an external search provider, enabling fast, typo-tolerant search across your media library.

> **Important**: This plugin requires Jellyfin with external search provider support, added in [jellyfin/jellyfin#16121](https://github.com/jellyfin/jellyfin/pull/16121). This is included in Jellyfin unstable/nightly builds and the upcoming 12.0.0 release.

## Features

- Fast full-text search powered by Meilisearch
- Typo-tolerant search (finds "Strager Things" when you meant "Stranger Things")
- People-aware search - find titles by actor or director name
- Real-time index synchronization with a debounced, coalesced, persisted queue
- Scheduled tasks for full and incremental reindexing
- Background health monitor that pauses sync when Meilisearch is unreachable
- Live status panel (document count, index size, last sync) in the config page
- Custom synonyms (e.g. `mcu=marvel`, `lotr=lord of the rings`)
- Configurable minimum match score threshold
- Supports movies, TV shows, episodes, music, audiobooks, and more

## Requirements

### Jellyfin with External Search Support

This plugin requires Jellyfin with external search provider support, which adds the `IExternalSearchProvider` interface. This was merged into Jellyfin mainline in [jellyfin/jellyfin#16121](https://github.com/jellyfin/jellyfin/pull/16121) and is available in:

- Jellyfin unstable/nightly builds, and
- the upcoming Jellyfin 12.0.0 stable release.

No custom Jellyfin build is required — the plugin compiles against the official `Jellyfin.Controller` NuGet packages (see [Building](#building)).

### Meilisearch Server

You need a running Meilisearch instance. The easiest way to get started:

```bash
# Using Docker
docker run -d -p 7700:7700 -v $(pwd)/meili_data:/meili_data getmeili/meilisearch:latest
```

### Build Requirements

- .NET 10.0 SDK
- Jellyfin 12.0.0 or newer (unstable/nightly until 12.0.0 is released)

## Building

The plugin builds against the official Jellyfin NuGet packages. The unstable `12.0.0-*`
packages are published to Jellyfin's GitHub Packages feed (configured in `nuget.config`),
which requires authentication. Export a [GitHub personal access token](https://github.com/settings/tokens)
with the `read:packages` scope as `NUGET_AUTH_TOKEN` before building:

```bash
export NUGET_AUTH_TOKEN=<your-github-token>

cd jellyfin-plugin-meilisearch

# Build the plugin
dotnet build

# Build in Release mode
dotnet build -c Release

# The plugin DLL will be at:
# Jellyfin.Plugin.Meilisearch/bin/Release/net10.0/Jellyfin.Plugin.Meilisearch.dll
```

## Installation

### From the plugin repository (recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**.
2. Add a new repository:
   - **Name**: `Meilisearch`
   - **URL**: `https://raw.githubusercontent.com/Shadowghost/jellyfin-plugin-meilisearch/metadata/unstable/manifest.json`
3. Go to **Catalog**, find **Meilisearch** under the **Search** category, and install it.
4. Restart Jellyfin.

> Only the unstable feed is published for now. A stable feed will be available at
> `https://raw.githubusercontent.com/Shadowghost/jellyfin-plugin-meilisearch/metadata/stable/manifest.json`
> once a stable release is cut.

### Manual installation

1. Build the plugin as described above.
2. Copy `Jellyfin.Plugin.Meilisearch.dll` to your Jellyfin plugins directory:
   - Linux: `~/.local/share/jellyfin/plugins/Meilisearch/`
   - Windows: `%APPDATA%\jellyfin\plugins\Meilisearch\`
   - Docker: `/config/plugins/Meilisearch/`
3. Restart Jellyfin.

## Configuration

After installation, configure the plugin in Jellyfin's admin dashboard under **Plugins > Meilisearch**.

| Setting | Default | Description |
|---------|---------|-------------|
| Meilisearch URL | `http://localhost:7700` | URL of your Meilisearch server |
| API Key | (empty) | Meilisearch API key (if authentication is enabled) |
| Index Name | `jellyfin` | Name of the Meilisearch index to use |
| Enable Real-time Sync | `true` | Automatically update the index when library items change |
| Minimum Match Score | `50` | Filter out results below this relevance threshold (0-100) |
| Sync Batch Size | `500` | Max items per real-time sync flush |
| Sync Debounce (ms) | `2000` | Max wait before flushing a partial sync batch |
| Reindex Batch Size | `2000` | Items per push during full/incremental reindex |
| Reindex Parallelism | `2` | Concurrent indexing requests during reindex |
| Enable Health Monitor | `true` | Periodically pings Meilisearch and pauses sync when unreachable |
| Health Check Interval (s) | `60` | How often the health monitor runs |
| Synonyms | (empty) | One per line: `term=alt1,alt2` |

Use the **Test Connection** button to verify connectivity and that your API key is valid. The **Status** panel shows the live document count, index size, last incremental sync time, and field distribution.

## Indexing Your Library

### Initial Index

After configuring the plugin, run a full reindex:

1. Go to **Dashboard > Scheduled Tasks**
2. Find **Rebuild Meilisearch Index**
3. Click the play button to run immediately

The task fetches only indexable item types from the database, pushes them in configurable
batches (default 2,000) with bounded parallelism, awaits Meilisearch task completion before
reporting success, and pauses real-time sync while running so the freshly reset index
doesn't race with incoming events.

### Incremental Sync

A second scheduled task, **Incremental Meilisearch Sync**, runs hourly by default and
only indexes items modified since the last incremental run. Use this to keep the index
fresh without paying the cost of a full rebuild.

### Real-time Sync

When enabled, the plugin automatically updates the search index whenever items are:

- Added to your library
- Updated (metadata changes)
- Removed from your library

Events feed into a bounded, debounced queue that coalesces multiple updates to the same
item, flushes batches of up to `SyncBatchSize` (default 500) every `SyncBatchDebounceMs`
(default 2,000 ms), and persists any in-flight ops across plugin restarts to a JSON file
under the plugin configuration directory. If the health monitor detects Meilisearch is
unreachable, the queue is paused until the server recovers.

## Indexed Content Types

The plugin indexes the following item types:

- Movies
- TV Series
- Episodes
- Music (Artists, Albums, Tracks)
- Music Videos
- Books & Audiobooks
- Box Sets / Collections
- Playlists
- People
- Genres & Studios
- Trailers
- Live TV Channels & Programs

## Architecture

- **MeilisearchClientWrapper** - Singleton client managing Meilisearch connections, cached index handle, and settings application
- **MeilisearchSearchProvider** - Implements `IExternalSearchProvider` for Jellyfin integration (Jellyfin core handles user/parental access filtering on results)
- **MeilisearchIndexService** - Hosted service running a bounded, debounced, coalescing sync queue with pause/resume support
- **SyncQueuePersistence** - Persists pending sync ops across plugin restarts
- **MeilisearchHealthMonitor** - Hosted service that periodically pings Meilisearch and pauses sync when unreachable
- **MeilisearchController** - REST endpoints (`/Plugins/Meilisearch/Stats`, `/TestConnection`) backing the config-page status panel
- **ReindexTask** - Scheduled task for full library reindexing
- **IncrementalReindexTask** - Hourly scheduled task syncing items modified since the last run
