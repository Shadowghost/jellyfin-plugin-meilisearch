# Jellyfin Meilisearch Plugin

---

A Jellyfin plugin that integrates [Meilisearch](https://www.meilisearch.com/) as an external search provider, enabling fast, typo-tolerant search across your media library.

> **Important**: This plugin requires a modified version of Jellyfin with external search provider support. You must have the `search-external` branch of [Shadowghost/jellyfin](https://github.com/Shadowghost/jellyfin) available locally and Jellyfin built with that branch.

## Features

- Fast full-text search powered by Meilisearch
- Typo-tolerant search (finds "Strager Things" when you meant "Stranger Things")
- Real-time index synchronization when items are added, updated, or removed
- Scheduled task for full library reindexing
- Configurable minimum match score threshold
- Supports movies, TV shows, episodes, music, audiobooks, and more

## Requirements

### Jellyfin with External Search Support

This plugin requires the `search-external` branch from [Shadowghost/jellyfin](https://github.com/Shadowghost/jellyfin) which adds the `ISearchProvider` interface for external search providers.

You must clone and build Jellyfin from this branch:

```bash
git clone https://github.com/Shadowghost/jellyfin.git
cd jellyfin
git checkout search-external
dotnet build
```

The plugin expects the Jellyfin source to be available at `../../jellyfin/` relative to this repository (i.e., both repositories should be in the same parent directory).

### Meilisearch Server

You need a running Meilisearch instance. The easiest way to get started:

```bash
# Using Docker
docker run -d -p 7700:7700 -v $(pwd)/meili_data:/meili_data getmeili/meilisearch:latest
```

### Build Requirements

- .NET 10.0 SDK
- Jellyfin 10.12.0 (from the search-external branch)

## Building

```bash
# Clone this repository next to your jellyfin checkout
# Directory structure should be:
#   parent/
#     jellyfin/          (search-external branch)
#     jellyfin-plugin-meilisearch/

# Build the plugin
cd jellyfin-plugin-meilisearch
dotnet build

# Build in Release mode
dotnet build -c Release

# The plugin DLL will be at:
# Jellyfin.Plugin.Meilisearch/bin/Release/net10.0/Jellyfin.Plugin.Meilisearch.dll
```

## Installation

1. Build the plugin as described above
2. Copy `Jellyfin.Plugin.Meilisearch.dll` to your Jellyfin plugins directory:
   - Linux: `~/.local/share/jellyfin/plugins/Meilisearch/`
   - Windows: `%APPDATA%\jellyfin\plugins\Meilisearch\`
   - Docker: `/config/plugins/Meilisearch/`
3. Restart Jellyfin

## Configuration

After installation, configure the plugin in Jellyfin's admin dashboard under **Plugins > Meilisearch**.

| Setting | Default | Description |
|---------|---------|-------------|
| Meilisearch URL | `http://localhost:7700` | URL of your Meilisearch server |
| API Key | (empty) | Meilisearch API key (if authentication is enabled) |
| Index Name | `jellyfin` | Name of the Meilisearch index to use |
| Enable Real-time Sync | `true` | Automatically update the index when library items change |
| Minimum Match Score | `50` | Filter out results below this relevance threshold (0-100) |

Use the **Test Connection** button to verify connectivity to your Meilisearch server.

## Indexing Your Library

### Initial Index

After configuring the plugin, run a full reindex:

1. Go to **Dashboard > Scheduled Tasks**
2. Find **Rebuild Meilisearch Index**
3. Click the play button to run immediately

The task processes items in batches of 10,000 for efficient indexing of large libraries.

### Real-time Sync

When enabled, the plugin automatically updates the search index whenever items are:

- Added to your library
- Updated (metadata changes)
- Removed from your library

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

- **MeilisearchClientWrapper** - Singleton client managing Meilisearch connections
- **MeilisearchSearchProvider** - Implements `ISearchProvider` for Jellyfin integration
- **MeilisearchIndexService** - Background service for real-time library sync
- **ReindexTask** - Scheduled task for full library reindexing
