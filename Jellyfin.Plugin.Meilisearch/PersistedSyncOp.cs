namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// A persisted real-time sync operation.
/// The full <see cref="MediaBrowser.Controller.Entities.BaseItem"/> reference is not stored;
/// on restore the item is re-resolved via the library manager.
/// </summary>
/// <param name="Id">The item id (GUID in <c>"N"</c> format).</param>
/// <param name="Kind">Either <c>"Upsert"</c> or <c>"Remove"</c>.</param>
public sealed record PersistedSyncOp(string Id, string Kind);
