// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LumaraCompanion.Integrations.Seerr;
using Jellyfin.Plugin.LumaraCompanion.UserData;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations;

/// <summary>One title on a watchlist, as stored.</summary>
public sealed class WatchlistEntry
{
    /// <summary>Gets or sets "movie" or "tv".</summary>
    public string Type { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id.</summary>
    public long Tmdb { get; set; }

    /// <summary>Gets or sets the title when it was added.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was added (ISO 8601 UTC).</summary>
    public string Added { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether Seerr has it too. An entry that was
    /// synced and is then missing from Seerr was removed there; one that was never
    /// synced is still waiting to be sent.
    /// </summary>
    public bool Synced { get; set; }
}

/// <summary>A user's stored watchlist.</summary>
public sealed class WatchlistDoc
{
    /// <summary>Gets the titles, oldest first.</summary>
    public List<WatchlistEntry> Items { get; init; } = [];

    /// <summary>
    /// Gets titles removed here while Seerr couldn't be reached, still to be
    /// removed there ("type:tmdb").
    /// </summary>
    public List<string> Removed { get; init; } = [];
}

/// <summary>A watchlist title as apps receive it.</summary>
/// <param name="MediaType">"movie" or "tv".</param>
/// <param name="TmdbId">TMDB id.</param>
/// <param name="Title">Title.</param>
/// <param name="Year">Year, when known.</param>
/// <param name="Poster">Signed poster address (titles not in the library).</param>
/// <param name="ItemId">Library item id when it's in the library.</param>
/// <param name="Status">Seerr status, when known.</param>
/// <param name="Added">When it was added (ISO 8601 UTC).</param>
public sealed record WatchlistItem(string MediaType, long TmdbId, string Title, int? Year, string? Poster, string? ItemId, string Status, string Added);

/// <summary>
/// The watchlist: titles a person wants to watch, in the library or not yet.
/// Kept by the plugin for every user, and synced both ways with their Seerr
/// watchlist when Seerr is set up (adding in Seerr's web page shows up in Lumara,
/// and the reverse).
/// </summary>
/// <remarks>
/// Sync happens when the list is read. Each entry remembers whether Seerr has it,
/// so a title missing from Seerr is either "removed there" (it was synced) or
/// "not sent yet" (it wasn't), with no timestamps to compare. Removals made while
/// Seerr is down wait in <see cref="WatchlistDoc.Removed"/>.
/// </remarks>
public sealed class Watchlist
{
    /// <summary>Most titles a watchlist keeps.</summary>
    public const int MaxItems = 500;

    private readonly UserJsonStore<WatchlistDoc> _store;
    private readonly SeerrGateway _seerr;
    private readonly LibraryIndex _index;
    private readonly ImageSigner _images;

    /// <summary>
    /// Initializes a new instance of the <see cref="Watchlist"/> class.
    /// </summary>
    /// <param name="store">Per-user files.</param>
    /// <param name="seerr">Seerr gateway (sync is skipped when it isn't set up).</param>
    /// <param name="index">Library lookups.</param>
    /// <param name="images">Signs posters.</param>
    public Watchlist(UserJsonStore<WatchlistDoc> store, SeerrGateway seerr, LibraryIndex index, ImageSigner images)
    {
        _store = store;
        _seerr = seerr;
        _index = index;
        _images = images;
    }

    /// <summary>
    /// The TMDB id and type of a library film or show (episodes and seasons use their show).
    /// </summary>
    /// <param name="item">Library item.</param>
    /// <returns>Type, id and title, or <c>null</c> when it has no TMDB id.</returns>
    public static (string Type, long Tmdb, string Title)? Identify(BaseItem? item)
    {
        var title = item switch
        {
            Episode e => (BaseItem?)e.Series,
            Season s => s.Series,
            _ => item,
        };
        var type = title switch
        {
            Movie => "movie",
            Series => "tv",
            _ => null,
        };
        if (type is null || title is null || !long.TryParse(title.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var tmdb))
        {
            return null;
        }

        return (type, tmdb, title.Name);
    }

    /// <summary>
    /// The user's watchlist, synced with Seerr first.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Titles, newest first.</returns>
    public async Task<IReadOnlyList<WatchlistItem>> GetAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var doc = await SyncAsync(user, cancellationToken).ConfigureAwait(false);
        var seerr = await _seerr.UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var lookup = _seerr.ItemLookup(user);
        var result = new List<WatchlistItem>();
        foreach (var e in doc.Items.AsEnumerable().Reverse())
        {
            var itemId = lookup(e.Type, e.Tmdb, null);
            int? year = null;
            string? poster = null;
            var status = itemId is null ? "unknown" : "available";
            if (seerr is not null && itemId is null)
            {
                // Posters and status for titles not in the library come from Seerr's cached details.
                var body = await _seerr.CachedDetailsAsync(seerr, e.Type, e.Tmdb, cancellationToken).ConfigureAwait(false);
                if (body is not null)
                {
                    using var d = JsonDocument.Parse(body);
                    var r = d.RootElement;
                    year = Json.Year(e.Type == "tv" ? r.Str("firstAirDate") : r.Str("releaseDate"));
                    poster = _images.Tmdb(r.Str("posterPath"), "w342");
                    status = SeerrMapper.MediaStatus(r.Get("mediaInfo").Long("status"));
                }
            }

            result.Add(new WatchlistItem(e.Type, e.Tmdb, e.Title, year, poster, itemId, status, e.Added));
        }

        return result;
    }

    /// <summary>
    /// Whether a title is on the user's watchlist (no sync; for title pages).
    /// </summary>
    /// <param name="userId">Jellyfin user id.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <returns><c>true</c> when it's there.</returns>
    public bool Contains(Guid userId, string mediaType, long tmdbId) =>
        _store.Read(userId).Items.Any(i => i.Type == mediaType && i.Tmdb == tmdbId);

    /// <summary>
    /// Adds a title (and sends it to Seerr when possible).
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="title">Title.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<WriteResult> AddAsync(User user, string mediaType, long tmdbId, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (mediaType is not ("movie" or "tv") || tmdbId <= 0)
        {
            return new WriteResult(false, "That title can't go on the watchlist.");
        }

        var full = false;
        _store.Update(user.Id, doc =>
        {
            doc.Removed.Remove(mediaType + ":" + tmdbId.ToString(CultureInfo.InvariantCulture));
            if (doc.Items.Any(i => i.Type == mediaType && i.Tmdb == tmdbId))
            {
                return true;
            }

            if (doc.Items.Count >= MaxItems)
            {
                full = true;
                return false;
            }

            doc.Items.Add(new WatchlistEntry { Type = mediaType, Tmdb = tmdbId, Title = Trim(title), Added = Json.Iso(DateTime.UtcNow) });
            return true;
        });
        if (full)
        {
            return new WriteResult(false, $"Your watchlist is full ({MaxItems} titles). Remove some first.");
        }

        var seerr = await _seerr.UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is not null && await _seerr.AddToWatchlistAsync(seerr, mediaType, tmdbId, Trim(title), cancellationToken).ConfigureAwait(false))
        {
            _store.Update(user.Id, doc =>
            {
                var e = doc.Items.FirstOrDefault(i => i.Type == mediaType && i.Tmdb == tmdbId);
                if (e is null || e.Synced)
                {
                    return false;
                }

                e.Synced = true;
                return true;
            });
        }

        return new WriteResult(true, "Added to your watchlist.");
    }

    /// <summary>
    /// Removes a title (here and in Seerr).
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<WriteResult> RemoveAsync(User user, string mediaType, long tmdbId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var key = mediaType + ":" + tmdbId.ToString(CultureInfo.InvariantCulture);
        var wasSynced = false;
        _store.Update(user.Id, doc =>
        {
            var e = doc.Items.FirstOrDefault(i => i.Type == mediaType && i.Tmdb == tmdbId);
            if (e is null)
            {
                return false;
            }

            wasSynced = e.Synced;
            doc.Items.Remove(e);
            return true;
        });

        var seerr = await _seerr.UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is not null && !await _seerr.RemoveFromWatchlistAsync(seerr, mediaType, tmdbId, cancellationToken).ConfigureAwait(false) && wasSynced)
        {
            // Seerr is down: remove it there on the next sync instead.
            _store.Update(user.Id, doc =>
            {
                if (doc.Removed.Contains(key))
                {
                    return false;
                }

                doc.Removed.Add(key);
                return true;
            });
        }

        return new WriteResult(true, "Removed from your watchlist.");
    }

    /// <summary>
    /// Merges the stored list with Seerr's. Pure function over the three inputs,
    /// so the rules are unit-tested.
    /// </summary>
    /// <param name="doc">Stored list (changed in place).</param>
    /// <param name="remote">Seerr's list.</param>
    /// <param name="now">Current time.</param>
    /// <returns>What to send to Seerr: titles to add, and titles to remove.</returns>
    public static (List<WatchlistEntry> Add, List<(string Type, long Tmdb)> Remove) Merge(WatchlistDoc doc, IReadOnlyList<(string Type, long TmdbId, string Title)> remote, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(remote);
        var remoteKeys = remote.Select(r => Key(r.Type, r.TmdbId)).ToHashSet();
        var removedHere = doc.Removed.ToHashSet();
        var toRemove = new List<(string, long)>();
        foreach (var k in removedHere)
        {
            if (remoteKeys.Contains(k) && Parse(k) is { } p)
            {
                toRemove.Add(p);
            }
        }

        doc.Removed.Clear();

        // Synced entries Seerr no longer has were removed in Seerr.
        doc.Items.RemoveAll(i => i.Synced && !remoteKeys.Contains(Key(i.Type, i.Tmdb)));

        // Entries Seerr already has (added there too, or a sync that half-finished).
        foreach (var i in doc.Items.Where(i => !i.Synced && remoteKeys.Contains(Key(i.Type, i.Tmdb))))
        {
            i.Synced = true;
        }

        var toAdd = doc.Items.Where(i => !i.Synced).ToList();

        // Titles added in Seerr.
        var local = doc.Items.Select(i => Key(i.Type, i.Tmdb)).ToHashSet();
        foreach (var r in remote)
        {
            var k = Key(r.Type, r.TmdbId);
            if (!local.Contains(k) && !removedHere.Contains(k) && doc.Items.Count < MaxItems)
            {
                doc.Items.Add(new WatchlistEntry { Type = r.Type, Tmdb = r.TmdbId, Title = Trim(r.Title), Added = Json.Iso(now), Synced = true });
                local.Add(k);
            }
        }

        return (toAdd, toRemove);
    }

    private static string Key(string type, long tmdb) => type + ":" + tmdb.ToString(CultureInfo.InvariantCulture);

    private static (string Type, long Tmdb)? Parse(string key)
    {
        var parts = key.Split(':');
        return parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? (parts[0], id) : null;
    }

    private static string Trim(string? title) => (title ?? string.Empty).Trim() is { Length: > 200 } t ? t[..200] : (title ?? string.Empty).Trim();

    private async Task<WatchlistDoc> SyncAsync(User user, CancellationToken cancellationToken)
    {
        var seerr = await _seerr.UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null)
        {
            return _store.Read(user.Id);
        }

        var remote = await _seerr.WatchlistAsync(seerr, cancellationToken).ConfigureAwait(false);
        if (remote is null)
        {
            // Seerr unreadable: show the stored list, change nothing.
            return _store.Read(user.Id);
        }

        List<WatchlistEntry> add = [];
        List<(string Type, long Tmdb)> remove = [];
        _store.Update(user.Id, doc =>
        {
            (add, remove) = Merge(doc, remote, DateTime.UtcNow);
            return true;
        });

        var stillRemoved = new List<string>();
        foreach (var (type, tmdb) in remove)
        {
            if (!await _seerr.RemoveFromWatchlistAsync(seerr, type, tmdb, cancellationToken).ConfigureAwait(false))
            {
                stillRemoved.Add(Key(type, tmdb));
            }
        }

        var sent = new HashSet<string>();
        foreach (var e in add)
        {
            if (await _seerr.AddToWatchlistAsync(seerr, e.Type, e.Tmdb, e.Title, cancellationToken).ConfigureAwait(false))
            {
                sent.Add(Key(e.Type, e.Tmdb));
            }
        }

        return _store.Update(user.Id, doc =>
        {
            // Removals Seerr refused stay queued for the next sync.
            var changed = stillRemoved.Count > 0;
            doc.Removed.AddRange(stillRemoved.Where(k => !doc.Removed.Contains(k)));
            foreach (var i in doc.Items.Where(i => !i.Synced && sent.Contains(Key(i.Type, i.Tmdb))))
            {
                i.Synced = true;
                changed = true;
            }

            return changed;
        });
    }
}
