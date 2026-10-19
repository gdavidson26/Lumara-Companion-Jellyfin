// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations;

/// <summary>
/// Finds the Jellyfin film or show that matches a TMDB, TVDB or IMDb id, so a
/// title from Seerr, the calendar or a download can link to (and play from) the
/// library.
/// </summary>
/// <remarks>
/// One query per discovery card would be twenty queries per row, so the index
/// keeps a map of every film and show's ids, rebuilt after ten minutes, when the
/// library adds an item, or (at most once a minute) when a lookup misses. Callers
/// always check the viewer may see the item (<see cref="FindVisible"/>), so parental
/// limits and library access apply to every link.
/// </remarks>
public sealed class LibraryIndex : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MissRebuild = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _library;
    private readonly object _gate = new();
    private Dictionary<string, Guid> _ids = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _built = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryIndex"/> class.
    /// </summary>
    /// <param name="library">Jellyfin's library.</param>
    public LibraryIndex(ILibraryManager library)
    {
        _library = library;
        _library.ItemAdded += OnItemAdded;
    }

    /// <summary>
    /// The library film or show for an id, if the viewer may see it.
    /// </summary>
    /// <param name="user">The viewer (null: no visibility check, for server tasks).</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id, if known.</param>
    /// <param name="tvdbId">TVDB id (shows), if known.</param>
    /// <param name="imdbId">IMDb id, if known.</param>
    /// <returns>The item, or <c>null</c>.</returns>
    public BaseItem? FindVisible(Jellyfin.Database.Implementations.Entities.User? user, string mediaType, long? tmdbId, long? tvdbId = null, string? imdbId = null)
    {
        var id = Lookup(mediaType, tmdbId, tvdbId, imdbId);
        if (id is null)
        {
            return null;
        }

        var item = _library.GetItemById(id.Value);
        return item is not null && (user is null || item.IsVisible(user)) ? item : null;
    }

    /// <summary>Forces a rebuild on the next lookup.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _built = DateTime.MinValue;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _library.ItemAdded -= OnItemAdded;
    }

    /// <summary>The index key for one id.</summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="provider">"tmdb", "tvdb" or "imdb".</param>
    /// <param name="value">The id.</param>
    /// <returns>The key.</returns>
    internal static string Key(string mediaType, string provider, string value) => mediaType + ":" + provider + ":" + value;

    private Guid? Lookup(string mediaType, long? tmdbId, long? tvdbId, string? imdbId)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Dictionary<string, Guid> ids;
            lock (_gate)
            {
                var age = DateTime.UtcNow - _built;
                if (age > MaxAge || (attempt == 1 && age > MissRebuild))
                {
                    _ids = Build();
                    _built = DateTime.UtcNow;
                }
                else if (attempt == 1)
                {
                    return null;
                }

                ids = _ids;
            }

            if (tmdbId is > 0 && ids.TryGetValue(Key(mediaType, "tmdb", tmdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), out var a))
            {
                return a;
            }

            if (tvdbId is > 0 && ids.TryGetValue(Key(mediaType, "tvdb", tvdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), out var b))
            {
                return b;
            }

            if (!string.IsNullOrEmpty(imdbId) && ids.TryGetValue(Key(mediaType, "imdb", imdbId), out var c))
            {
                return c;
            }
        }

        return null;
    }

    private Dictionary<string, Guid> Build()
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false,
        });
        foreach (var item in items)
        {
            var type = item is Series ? "tv" : item is Movie ? "movie" : null;
            if (type is null)
            {
                continue;
            }

            Add(map, type, "tmdb", item.GetProviderId(MetadataProvider.Tmdb), item.Id);
            Add(map, type, "tvdb", item.GetProviderId(MetadataProvider.Tvdb), item.Id);
            Add(map, type, "imdb", item.GetProviderId(MetadataProvider.Imdb), item.Id);
        }

        return map;
    }

    private static void Add(Dictionary<string, Guid> map, string type, string provider, string? value, Guid id)
    {
        if (!string.IsNullOrEmpty(value))
        {
            // First one wins: two copies of a film (4K and HD libraries) both link to the first.
            map.TryAdd(Key(type, provider, value), id);
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series)
        {
            Invalidate();
        }
    }
}
