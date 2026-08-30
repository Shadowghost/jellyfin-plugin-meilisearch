# Jellyfin Meilisearch Plugin

---

A Jellyfin plugin that integrates [Meilisearch](https://www.meilisearch.com/) as an external search provider, enabling fast, typo-tolerant search across your media library.

> **Important**: This plugin requires Jellyfin with external search provider support, added in [jellyfin/jellyfin#16121](https://github.com/jellyfin/jellyfin/pull/16121). This is included in Jellyfin unstable/nightly builds and the upcoming 12.0.0 release.

## Features

- Fast full-text search powered by Meilisearch
- Typo-tolerant search (finds "Strager Things" when you meant "Stranger Things")
- People-aware search - find titles by actor or director name
- File-name search - find an item by its release name, not just its metadata
- Real-time index synchronization with a debounced, coalesced, persisted queue
- Scheduled tasks for full and incremental reindexing
- Background health monitor that pauses sync when Meilisearch is unreachable
- Live status panel (document count, index size, last sync, search latency, embedding model state) in the config page
- Rebuild, reconnect and unload-model buttons in the config page
- Optional semantic search using a locally run embedding model, picked from a list (off by default)
- Custom synonyms (e.g. `mcu=marvel`, `lotr=lord of the rings`)
- Configurable minimum match score threshold and matching strategy
- Per-type result quotas, so songs matching an artist name can't crowd out the movies and episodes
- Automatic reconnect when the Meilisearch container is restarted or gets a new address
- Automatic incremental sync after every library scan
- REST endpoints for status and connection testing
- Supports movies, TV shows, episodes, music, audiobooks, and more

## Requirements

### Jellyfin with External Search Support

This plugin requires Jellyfin with external search provider support, which adds the `IExternalSearchProvider` interface. This was merged into Jellyfin mainline in [jellyfin/jellyfin#16121](https://github.com/jellyfin/jellyfin/pull/16121) and is available in:

- Jellyfin unstable/nightly builds, and
- the upcoming Jellyfin 12.0.0 stable release.

No custom Jellyfin build is required - the plugin compiles against the official `Jellyfin.Controller` NuGet packages (see [Building](#building)).

### Meilisearch Server

You need a running Meilisearch instance. The easiest way to get started:

```bash
# Using Docker
docker run -d -p 7700:7700 -v $(pwd)/meili_data:/meili_data \
  -e MEILI_ENV=production \
  -e MEILI_MASTER_KEY=<a-long-random-key> \
  -e MEILI_NO_ANALYTICS=true \
  getmeili/meilisearch:latest
```

> **Why these settings?** See the [Meilisearch configuration reference](https://www.meilisearch.com/docs/resources/self_hosting/configuration/reference).
>
> - `MEILI_ENV` defaults to `development`, which disables authentication and serves the
>   bundled search preview UI. Set it to [`production`](https://www.meilisearch.com/docs/resources/self_hosting/configuration/reference#environment)
>   so the API key is enforced and the preview is disabled.
> - `MEILI_MASTER_KEY` is [required in production](https://www.meilisearch.com/docs/resources/self_hosting/configuration/reference#master-key).
>   Generate a random value of at least 16 bytes and use it (or a derived API key) as the
>   **API Key** in the plugin configuration below.
> - Meilisearch collects [anonymous analytics by default](https://www.meilisearch.com/docs/resources/self_hosting/configuration/reference#disable-analytics)
>   (opt-out). `MEILI_NO_ANALYTICS=true` disables it.

### For semantic search (optional)

- Meilisearch 1.10 or newer, for vector search support
- ~610 MB of free disk space for the model, and roughly 1-2 GB of RAM while it is loaded
- A platform ONNX Runtime publishes a native library for: Linux and Windows on x64 or arm64, and
  macOS on Apple silicon. Intel Macs are out - ONNX Runtime no longer ships an `osx-x64` build. On
  an unsupported host the plugin says so in the log and the **Status** panel and downloads nothing;
  keyword search is unaffected.

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

ONNX Runtime ships a native library per platform, each 15-30 MB. The release artifact carries all of
them - JPRM builds it with `dotnet publish`, which stages every platform - but a plain `dotnet build`
stages only the machine it runs on, since a development build is only ever used there. Point it
somewhere else when you build here and deploy elsewhere:

```bash
dotnet build -p:OnnxRuntimeBuildPlatforms=linux-x64

# Several at once - the separator has to be escaped as %3B, or the CLI reads it as another switch
dotnet build -p:OnnxRuntimeBuildPlatforms=linux-x64%3Bwin-x64
```

There is no `osx-x64` native: ONNX Runtime no longer ships one, so semantic search cannot run on
Intel Macs. Keyword search is unaffected.

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
2. Copy these from `bin/Release/net10.0/` to your Jellyfin plugins directory:
   - `Jellyfin.Plugin.Meilisearch.dll` and `Meilisearch.dll`
   - For semantic search only: `Microsoft.ML.OnnxRuntime.dll`, `Microsoft.ML.Tokenizers.dll`,
     `System.Numerics.Tensors.dll`, `Google.Protobuf.dll`, and the `native/` directory (keeping its
     structure - the plugin looks for the native library there, under `native/<rid>/`, as well as
     next to itself and under `runtimes/<rid>/native/`). A plain build stages only the building
     machine's own platform there; see [Building](#building). Omit all of these to run keyword-only.

   The plugins directory is:
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
| Matching Strategy | `frequency` | How a query that cannot be matched in full is narrowed (see [Matching strategy](#matching-strategy)) |
| Sync Batch Size | `500` | Max items per real-time sync flush |
| Sync Debounce (ms) | `2000` | Max wait before flushing a partial sync batch |
| Reindex Batch Size | `2000` | Items per push during full/incremental reindex |
| Reindex Parallelism | `2` | Concurrent indexing requests during reindex |
| Enable Health Monitor | `true` | Periodically pings Meilisearch and pauses sync when unreachable |
| Health Check Interval (s) | `60` | How often the health monitor runs |
| Synonyms | (empty) | One per line: `term=alt1,alt2` |
| Embedding Model | `Off` | Meaning-based matching via a locally run model, or off for keyword-only (see [Semantic Search](#semantic-search)) |
| Download the model automatically | `true` | Fetch the embedding model as soon as semantic search is enabled |
| Semantic Ratio | `50` | 0 is pure keyword, 100 is pure meaning |
| Max Tokens per Item | `256` | How much of each item's metadata is embedded |
| Embedding Batch Size | `8` | Items per inference pass |
| Inference Threads | `0` | 0 uses half the available CPU cores |
| Compress stored vectors | `true` | Binary quantization: 32x smaller in Meilisearch, at some ranking precision |
| Cache computed vectors on disk | `true` | Reuse vectors across rebuilds instead of recomputing them |
| Cache Size Limit | `0` | Cap on cached vectors; `0` is unlimited |
| Model Directory | (empty) | Empty means `<jellyfin-data>/meilisearch-embeddings`; each model gets a subdirectory |

Use the **Test Connection** button to verify connectivity and that your API key is valid. The **Status** panel shows the live document count, index size, last incremental sync time, and field distribution.

## How Search Works

### The plugin never fully replaces Jellyfin's own search

Jellyfin's `SearchManager` runs *external* providers (this plugin) and *internal* providers
(the built-in SQL provider) **in parallel** on every query. External results win whenever
they are non-empty; the SQL results are used only as a fallback. Two consequences worth
knowing:

- If the Meilisearch index is empty, stale, or the server is down, search silently degrades
  to the built-in SQL search rather than returning nothing. Check the **Status** panel and
  the server log if results look like they're coming from the wrong engine.
- Jellyfin core applies user, library-visibility and parental-rating filtering *after* the
  provider returns. The plugin deliberately does not index permissions, so a document in the
  index is never by itself a leak - but it does mean the number of hits a user sees can be
  lower than the number the plugin returned.

### Query handling

- **Single item type (or none)** - one Meilisearch query, with the item type and any parent,
  media-type or exclusion constraints applied as a filter.
- **Multiple item types** - one query per type inside a single HTTP multi-search request,
  each with its own share of the result budget. Without this, a strongly-matching type (all
  the songs by an artist) would consume the whole result set and hide weaker but relevant
  matches in other types (the artist, their albums, a documentary about them).
- **Parent scoping** - `parentId` matches against the item's full indexed ancestor chain, not
  just its direct parent, so scoping to a TV library still matches episodes nested under
  seasons under series.

### Matching strategy

When a query has no document matching every word, Meilisearch narrows it until something matches.
Which word it gives up on is the **Matching Strategy** setting:

| Value | Behaviour |
|-------|-----------|
| `frequency` (default) | Drops the word that occurs most often across your library first. A search for "the matrix reloaded" gives up "the" and keeps "reloaded". |
| `last` | Drops words from the end of the query. The same search gives up "reloaded" and returns every Matrix film, plus anything else matching "the". |
| `all` | Never narrows: only items matching every word are returned. Precise, and returns nothing when a single word is wrong. |

`frequency` requires Meilisearch 1.11 or newer. On an older server the plugin notices the rejection
on the first search, logs it once, and uses `last` for the rest of the session - so the setting is
safe to leave at its default regardless of server version. The **Status** panel shows which strategy
is actually in use.

### Index configuration

The plugin owns the index settings and re-applies them whenever the configuration changes.
They are idempotent, so editing them by hand in Meilisearch will be overwritten.

| Setting | Value |
|---------|-------|
| Searchable attributes (in priority order) | `name`, `originalTitle`, `sortName`, `seriesName`, `seasonName`, `albumName`, `artists`, `albumArtists`, `people`, `genres`, `tags`, `studios`, `providerIds.*`, `productionLocations`, `tagline`, `overview`, `path` |
| Ranking rules | Meilisearch's defaults, then `typeRank`, `sort`, `productionYear`, `communityRating`, `criticRating` - each one breaking ties left by the one before |
| Typo tolerance | Enabled; 1 typo from 4 characters, 2 typos from 8 |
| Displayed attributes | `id` and `itemType` - the provider consumes nothing else, which keeps search responses small |
| Synonyms | Taken from the **Synonyms** setting |

Because `name` outranks `overview`, a title match always beats a plot-summary match, and because
`path` comes last a file-name match never outranks either. Adding synonyms is the supported way to
tune recall; the attribute order itself is not configurable.

`path` holds only the item's file or folder name - `The.Matrix.1999.1080p.mkv`, not
`/mnt/media/Movies/The Matrix (1999)/The.Matrix.1999.1080p.mkv`. Indexing the directories above an
item would repeat their words on everything beneath them, which would let a search for "movies"
match a whole library.

## Semantic Search

Off by default. When a model is selected, the plugin embeds each indexed item and each query with
it - currently [Qwen3-Embedding-0.6B](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B) - and asks
Meilisearch for a hybrid keyword + vector search. That finds items whose words never appear in the query:

| Query | Finds |
|-------|-------|
| `space movie with a robot` | WALL·E |
| `cooking show about famous chefs` | Chef's Table |
| `caped crusader in gotham` | The Dark Knight |

The model runs **locally, inside the Jellyfin process**. No embedding service, API key or
outbound request is involved beyond the one-time model download, and no library data leaves
your server.

### What it costs

Be deliberate about turning this on - it is a real trade, which is why it ships disabled:

- **~610 MB download**, once, into the model directory.
- **~1-2 GB of RAM** while the model is loaded.
- **CPU time per item.** Every indexed item needs a forward pass. The first full rebuild of a large
  library takes hours rather than minutes, and the incremental sync and real-time queue pay the
  same cost per changed item. Inference defaults to half your CPU cores to leave headroom for
  transcoding. Later rebuilds are much cheaper thanks to the vector cache below.
- **~4 KB per item on disk** for that cache, under Jellyfin's data directory.
- **Index growth.** Meilisearch stores one vector per document: 128 bytes with **Compress stored
  vectors** on, as it ships, or 4 KB of full-precision floats with it off.

Queries themselves stay fast: one short forward pass for the search term, then Meilisearch does
the vector comparison.

### Enabling it

1. Pick a model under **Embedding Model** in the plugin configuration and save. With automatic
   download left on, the model is fetched in the background; otherwise run the **Download Meilisearch
   Embedding Model** scheduled task. The **Status** panel reports `Ready` when the model is loaded.
2. Run **Rebuild Meilisearch Index**. Vectors are written as items are indexed, so documents
   already in the index have none until you rebuild.

Search keeps working normally throughout. Until the model is loaded, and for any document without
a vector, queries fall back to pure keyword matching - enabling semantic search never makes search
unavailable, only gradually better as vectors land.

Setting **Embedding Model** back to *Off* releases the model and removes the embedder from
Meilisearch, which drops the stored vectors and reclaims the index space.

### Freeing the memory again

**Unload Model** in the plugin configuration releases the model from memory - the 1-2 GB it holds -
without turning semantic search off, losing the download or clearing the vector cache. Useful on a
server that has finished indexing and is only serving playback.

It stays unloaded until something loads it again: the next reindex, the next time you save the
plugin configuration, or a restart. Searches run keyword-only in the meantime, and items added or
edited while it is unloaded are indexed without a vector until the next rebuild - the same as when
semantic search has never been switched on.

**While a reindex is running, the button refuses** and nothing is released. The model itself can be
disposed safely at any moment - an inference already in flight is waited for, and calls arriving
afterwards get no vector rather than an error - but during a rebuild "no vector" means every item
from that point on is indexed without one, which is precisely the half-vectorized index a rebuild
exists to avoid. Cancel the task on the Scheduled Tasks page first if you really mean it. The same
refusal applies while the model is still downloading or loading.

### Switching models

Each model has its own directory under the model path, its own vector cache and its own Meilisearch
embedder, so switching away from one and back again costs neither a re-download nor a re-embed. The
index is a different matter: vectors from one model mean nothing to another, so the plugin drops the
previous model's embedder - and its vectors - and semantic search behaves as keyword-only until you
run **Rebuild Meilisearch Index**. The **Status** panel says so when the index and the selected model
disagree.

### Tuning

**Semantic Ratio** is the dial that matters. At 0 vectors are ignored; at 100 keyword matching is
ignored, and exact title searches get noticeably worse - a vector search for `Alien` happily returns
every science-fiction film. The default of 50 keeps exact titles winning while letting descriptive
queries work. If precise titles start losing to thematically similar items, lower it.

**Cache computed vectors on disk** is on by default and worth leaving on. It is persistent: it lives
in `meilisearch-embedding-cache/<model id>` under Jellyfin's data directory (not the cache directory,
which routine cleanups empty) and is reopened on every start, so restarting Jellyfin - or having it
killed outright - costs nothing. Vectors reach the operating system as they are computed, and are forced out
to disk every thousand entries, at the end of a rebuild and on shutdown, so even a host that loses
power gives up at most a few seconds of re-embedding. A half-written tail from such a crash is
detected and discarded on the next open rather than being read back as a corrupt vector. A rebuild
re-embeds the whole library, but for items whose metadata has not changed since the last run the
text handed to the model is byte-identical, so the vector is too - the cache turns that forward pass back into a
file read, which is the difference between a rebuild taking hours and taking minutes. Edited items
miss the cache and are re-embedded, exactly as they should be. A clean full rebuild also prunes
cached vectors it no longer needed, so the cache tracks the library rather than growing forever -
which is why **Cache Size Limit** defaults to `0`, unlimited. Set one only to cap disk use. The
**Status** panel reports how many vectors are cached and how many lookups this session were served
from it.

**Compress stored vectors** is on by default. It stores each vector in Meilisearch at one bit per
dimension instead of a 32-bit float - 32 times smaller, which is usually the difference between the
vectors staying in memory and being read from disk on every search, worth seconds on a cold index.
The trade is some ranking precision in the vector half of a search; keyword matching is exact either
way. Measured on real library vectors, the compressed top ten keeps about two thirds of the same
entries, and the ones it swaps in are near-ties from just below the cut, within roughly 1.5% of the
similarity of what they replace. Turning it back off needs a rebuild, since Meilisearch discards the
full vectors as it compresses them - but that rebuild reads full precision from the vector cache, so
it re-uploads rather than re-running the model.

**Max Tokens per Item** trades indexing time for context. The embedded text is ordered
title → series/album → artists → type → year → genres → studios → tags → people → tagline →
overview, and truncation drops from the end, so a low value keeps the identifying fields and gives
up the overview.

> **The list of models is fixed, by design.** The tokenizer, the vector width, the key/value head
> geometry, the pooling and the query instruction prefix are all part of a model, not settings around
> it - each one needs an `ITextEmbedder` that knows them. So **Embedding Model** picks among the
> models this build ships code for; pointing it at an arbitrary repository is not offered, because
> that would produce vectors of the right shape and the wrong meaning. Adding a model is a code
> change, in `Embeddings/` plus an entry in `EmbeddingModels`.

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

The same sweep also runs automatically as soon as a library scan finishes. Real-time sync
normally indexes what a scan discovers, but not if it was disabled, paused by the health monitor,
or missed an event - and a scan is when the library and the index are most likely to have drifted.
On an unchanged library the sweep costs a single query, and it yields if a full reindex is already
running.

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

The plugin indexes the item types the built-in SQL search would return:

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
- Video extras (behind the scenes, deleted scenes, interviews, featurettes, shorts)

Deliberately **not** indexed:

- **Seasons** - users search for the series, not for "Season 3".
- **Virtual / missing items** - episodes and movies that have metadata but no media file.
- **Folders, collection folders, playlist folders and years** - Jellyfin's own search excludes
  these too.

## Upgrading

The plugin stamps a document schema version into its configuration after each successful full
reindex. When a new plugin version writes a newer schema than the one your index was built
with, it logs a warning at startup:

> The Meilisearch index was built with document schema v1 but this plugin writes v2 …

Filters can only match fields that exist on a document, so until you act on that warning,
searches that rely on newly added fields (parent scoping and media-type scoping, for
instance) will under-report. **Run the "Rebuild Meilisearch Index" task after any upgrade
that logs this warning.** Everything else keeps working in the meantime.

### Upgrading from a version before model selection

Model files and cached vectors used to live directly in their directories, since only one model
existed. They now live in a subdirectory named after the model. The plugin moves an existing set
into place by itself the first time semantic search initializes, and logs that it did - no
re-download and no re-embedding. If the move fails it says so and falls back to fetching the model
again; nothing is lost either way.

## Troubleshooting

| Symptom | Likely cause and fix |
|---------|----------------------|
| No results, or results identical to stock Jellyfin | The index is empty and search fell back to the SQL provider. Run **Rebuild Meilisearch Index** and check the document count in the **Status** panel. |
| Parent-scoped or media-type-scoped searches miss items | Stale document schema - see [Upgrading](#upgrading). |
| Results stop updating after adding media | Real-time sync is disabled, or the health monitor paused it because Meilisearch is unreachable. The log records both. Sync resumes automatically once the server returns. |
| Test Connection reports reachable but not authenticated | The **API Key** is wrong or lacks permission. A master key or a key with search + documents + settings + tasks access is required. |
| Meilisearch was restarted / its container was recreated | Handled automatically: on a communication failure the plugin rebuilds its HTTP client (clearing the pooled connection and cached DNS entry) and retries once. No Jellyfin restart needed. |
| Fewer results than expected for a fuzzy query | Lower **Minimum Match Score**. It maps to Meilisearch's `rankingScoreThreshold`, so raising it buys precision at the cost of recall. |
| Too many loosely related results | Set **Matching Strategy** to `all`, which returns only items matching every word of the query. |
| Log says the `frequency` matching strategy was rejected | The server predates Meilisearch 1.11. The plugin has already fallen back to `last`; upgrade Meilisearch to get `frequency`. |
| Pending sync operations after an unclean shutdown | They are persisted to a JSON file in the plugin's configuration directory and replayed on the next start. |
| Status shows semantic search `Failed` | The log carries the reason. Most often the model download was interrupted - delete the model's directory and retry. Keyword search is unaffected either way. |
| Status shows `Not supported on this platform` | ONNX Runtime has no native library for this OS and architecture, or the package was assembled without it. The **Status** panel names which. Nothing is downloaded while this is the case; either install ONNX Runtime system-wide or turn semantic search off. |
| Status shows `Model not downloaded` | Automatic download is off. Run the **Download Meilisearch Embedding Model** task. |
| Semantic search is `Ready` but results are unchanged | The existing documents have no vectors yet. Run **Rebuild Meilisearch Index**. |
| Registering the embedder failed | Vector search needs Meilisearch 1.10 or newer. Older servers keep working as keyword-only. |
| A rebuild re-embeds everything even though nothing changed | The vector cache starts empty for a model that has never been used, and is discarded if the same model's export or vector width changes. Switching between models does not reset either one's cache. |
| Status shows `Released from memory` | Someone pressed **Unload Model**, or did on a previous page visit. It loads again on the next reindex, the next configuration save, or a restart. |
| **Unload Model** says a reindex is running | Deliberate - see [Freeing the memory again](#freeing-the-memory-again). Cancel the task on the Scheduled Tasks page if you mean it, or wait. |
| Vector cache disk usage is too high | Lower **Cache Size Limit**, or untick **Cache computed vectors on disk** and delete `meilisearch-embedding-cache` from Jellyfin's data directory. Each model caches into its own subdirectory there, so an old model's cache can be deleted on its own. |

## REST API

All endpoints require an authenticated administrator and back the config page.

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/Plugins/Meilisearch/Stats` | Document count, index size, indexing state, field distribution, health, authentication state, last incremental sync timestamp, matching strategy in use, average search latency, embedding model and state |
| `GET` | `/Plugins/Meilisearch/EmbeddingModels` | The embedding models this build can run, as offered by the model picker |
| `POST` | `/Plugins/Meilisearch/UnloadEmbeddingModel` | Releases the model from memory. Answers with the outcome - `Unloaded`, `NotLoaded`, or `409` with `ReindexRunning` / `Busy` |
| `POST` | `/Plugins/Meilisearch/TestConnection` | Reachability and API-key validation |
| `POST` | `/Plugins/Meilisearch/Reconnect` | Drops the connection, dials again, and reports the resulting state |
| `POST` | `/Plugins/Meilisearch/Reindex` | Queues the **Rebuild Meilisearch Index** task |

## Architecture

- **MeilisearchClientWrapper** - Singleton client managing Meilisearch connections, cached index handle, and settings application
- **MeilisearchSearchProvider** - Implements `IExternalSearchProvider` for Jellyfin integration (Jellyfin core handles user/parental access filtering on results)
- **MeilisearchIndexService** - Hosted service running a bounded, debounced, coalescing sync queue with pause/resume support
- **SyncQueuePersistence** - Persists pending sync ops across plugin restarts
- **MeilisearchHealthMonitor** - Hosted service that periodically pings Meilisearch and pauses sync when unreachable
- **MeilisearchController** - REST endpoints (`/Plugins/Meilisearch/Stats`, `/EmbeddingModels`, `/UnloadEmbeddingModel`, `/TestConnection`, `/Reconnect`, `/Reindex`) backing the config page
- **ReindexTask** - Scheduled task for full library reindexing
- **IncrementalReindexTask** - Hourly scheduled task syncing items modified since the last run
- **LibraryScanSyncTask** - Post-scan hook that runs the incremental sync once a library scan finishes
- **ReindexCoordinator** - Process-wide gate that keeps the full and incremental reindex from overlapping, and that unloading the embedding model refuses to cross
- **EmbeddingService** - Hosted service owning the embedding model's lifecycle; a no-op while semantic search is off
- **EmbeddingModels** / **EmbeddingModelDefinition** / **EmbeddingModelDescriptor** - The fixed list of runnable models, what each one is (repository, files, vector width, embedder name, query prompt, loader), and where its files live on this server
- **ITextEmbedder** - The one abstraction a model family has to implement; everything model-specific lives behind it
- **Embeddings.Qwen** - The Qwen3-Embedding implementation: ONNX Runtime inference with last-token pooling and L2 normalization, the byte-level BPE tokenizer and its split pattern
- **EmbeddingCache** - Disk-backed store of computed vectors keyed by the exact text they came from, so a rebuild reuses them instead of recomputing; scoped per model
- **EmbeddingStorageMigration** - Moves a pre-selection flat model download and vector cache into the per-model layout
- **EmbeddingModelDownloader** - Fetches the model to a temporary file and moves it into place, so an interrupted download never looks complete
- **OnnxRuntimeNativeLoader** - Resolves ONNX Runtime's native library from the plugin directory, which the default RID probing does not reach inside a plugin load context
- **DownloadEmbeddingModelTask** - Scheduled task to fetch the model on demand
