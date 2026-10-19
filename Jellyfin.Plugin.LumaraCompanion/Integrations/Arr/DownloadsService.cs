// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations.Arr;

/// <summary>One download.</summary>
public sealed class DownloadItem
{
    /// <summary>Gets or sets a stable id ("sonarr:abc123").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets "sonarr", "radarr", "lidarr", "qbittorrent" or "sabnzbd".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the show, film, artist or file name.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets what's downloading ("S2 E4", "Season 2 · 10 episodes", the album).</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Gets or sets progress from 0 to 1.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets the total size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the bytes still to download.</summary>
    public long SizeLeft { get; set; }

    /// <summary>Gets or sets seconds left, when known.</summary>
    public long? SecondsLeft { get; set; }

    /// <summary>Gets or sets "downloading", "queued", "paused", "importing", "warning", "failed" or "completed".</summary>
    public string Status { get; set; } = "queued";

    /// <summary>Gets or sets the reason for a warning or failure (admins only).</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the download speed in bytes per second (download clients only).</summary>
    public long? Speed { get; set; }

    /// <summary>Gets or sets the signed poster address.</summary>
    public string? Poster { get; set; }

    /// <summary>Gets or sets "movie", "tv", "music" or "other".</summary>
    public string MediaType { get; set; } = "other";

    /// <summary>Gets or sets the TMDB id (films, and shows when Sonarr knows it).</summary>
    public long? TmdbId { get; set; }

    /// <summary>Gets or sets the TVDB id (shows).</summary>
    public long? TvdbId { get; set; }

    /// <summary>Gets or sets the library item, when the title is already in the library.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the download client's id for this download (torrent hash, NZB id), for de-duplication.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DownloadId { get; set; } = string.Empty;
}

/// <summary>The download list.</summary>
/// <param name="Items">Downloads, active first.</param>
/// <param name="Speed">Combined download speed in bytes per second (admins, when a client reports it).</param>
/// <param name="Everything">The list is the whole queue (admins) rather than the viewer's own requests.</param>
public sealed record DownloadsResult(IReadOnlyList<DownloadItem> Items, long? Speed, bool Everything);

/// <summary>
/// What's downloading, from the *arr queues and the download clients.
/// </summary>
/// <remarks>
/// Admins see everything, including downloads the *arrs don't know about and the
/// clients' speed. Everyone else sees only downloads of titles they requested in
/// Seerr (matched by TMDB/TVDB id), without client names, file names or error
/// text. The *arr queue is the main source because it knows which title a
/// download belongs to; client entries the *arrs already list (same hash or NZB
/// id) are dropped.
/// </remarks>
public sealed class DownloadsService
{
    private readonly ServiceClient _client;
    private readonly LibraryIndex _index;
    private readonly ImageSigner _images;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadsService"/> class.
    /// </summary>
    /// <param name="client">Service client.</param>
    /// <param name="index">Library lookups.</param>
    /// <param name="images">Signs posters.</param>
    public DownloadsService(ServiceClient client, LibraryIndex index, ImageSigner images)
    {
        _client = client;
        _index = index;
        _images = images;
    }

    /// <summary>Gets a value indicating whether any download source is set up.</summary>
    public bool Available => Sources.Any(_client.Integrations.IsConfigured);

    private static IntegrationKind[] Sources =>
        [IntegrationKind.Sonarr, IntegrationKind.Radarr, IntegrationKind.Lidarr, IntegrationKind.QBittorrent, IntegrationKind.Sabnzbd];

    /// <summary>
    /// The download list for a viewer.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="admin">They're an admin (see everything).</param>
    /// <param name="requested">For non-admins: the (type, TMDB id) and TVDB ids of titles they requested.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The list.</returns>
    public async Task<DownloadsResult> GetAsync(User user, bool admin, (HashSet<long> Movies, HashSet<long> Shows, HashSet<long> ShowsTvdb)? requested, CancellationToken cancellationToken)
    {
        var parts = await Task.WhenAll(
            Queue(IntegrationKind.Sonarr, "/api/v3/queue?page=1&pageSize=200&includeSeries=true&includeEpisode=true", cancellationToken),
            Queue(IntegrationKind.Radarr, "/api/v3/queue?page=1&pageSize=200&includeMovie=true", cancellationToken),
            Queue(IntegrationKind.Lidarr, "/api/v1/queue?page=1&pageSize=200&includeArtist=true&includeAlbum=true", cancellationToken)).ConfigureAwait(false);
        var items = parts.SelectMany(p => p).ToList();
        long? speed = null;
        if (admin)
        {
            var known = items.Select(i => i.DownloadId).Where(d => d.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var (qb, qbSpeed) = await QBittorrent(cancellationToken).ConfigureAwait(false);
            var (sab, sabSpeed) = await Sabnzbd(cancellationToken).ConfigureAwait(false);
            items.AddRange(qb.Where(i => !known.Contains(i.DownloadId)));
            items.AddRange(sab.Where(i => !known.Contains(i.DownloadId)));
            if (qbSpeed is not null || sabSpeed is not null)
            {
                speed = (qbSpeed ?? 0) + (sabSpeed ?? 0);
            }
        }
        else
        {
            var r = requested ?? (new HashSet<long>(), new HashSet<long>(), new HashSet<long>());
            items = items
                .Where(i => (i.MediaType == "movie" && i.TmdbId is { } m && r.Movies.Contains(m))
                    || (i.MediaType == "tv" && ((i.TmdbId is { } t && r.Shows.Contains(t)) || (i.TvdbId is { } v && r.ShowsTvdb.Contains(v)))))
                .ToList();
            foreach (var i in items)
            {
                // Release names and client errors are for admins.
                i.Message = null;
            }
        }

        foreach (var i in items.Where(i => i.MediaType is "movie" or "tv"))
        {
            i.ItemId = _index.FindVisible(user, i.MediaType, i.TmdbId, i.TvdbId)?.Id.ToString("N");
        }

        var ordered = items
            .OrderBy(i => i.Status switch { "downloading" => 0, "importing" => 1, "warning" => 2, "failed" => 3, "queued" => 4, "paused" => 5, _ => 6 })
            .ThenBy(i => i.SecondsLeft ?? long.MaxValue)
            .ToList();
        return new DownloadsResult(ordered, speed, admin);
    }

    /// <summary>
    /// A Sonarr, Radarr or Lidarr queue reply as downloads. Several episodes in one
    /// download (a season pack) become a single entry.
    /// </summary>
    /// <param name="root">Reply root ({records: [...]}).</param>
    /// <param name="source">"sonarr", "radarr" or "lidarr".</param>
    /// <param name="images">Signs posters.</param>
    /// <param name="serviceUrl">Stored service address.</param>
    /// <returns>Downloads.</returns>
    public static List<DownloadItem> ParseQueue(JsonElement root, string source, ImageSigner images, string? serviceUrl)
    {
        var records = root.ValueKind == JsonValueKind.Array ? root.Arr() : root.Arr("records");
        var groups = records.GroupBy(r => r.Str("downloadId") ?? ("id" + r.Long("id")), StringComparer.OrdinalIgnoreCase);
        var list = new List<DownloadItem>();
        foreach (var g in groups)
        {
            var first = g.First();
            var item = new DownloadItem
            {
                Id = source + ":" + g.Key,
                DownloadId = first.Str("downloadId") ?? string.Empty,
                Source = source,
                Size = (long)(first.Num("size") ?? 0),
                SizeLeft = (long)(first.Num("sizeleft") ?? 0),
                SecondsLeft = ParseTimeLeft(first.Str("timeleft")),
                Status = Status(first),
                Message = StatusMessage(first),
            };
            item.Progress = item.Size > 0 ? Math.Clamp(1 - ((double)item.SizeLeft / item.Size), 0, 1) : 0;
            switch (source)
            {
                case "sonarr":
                    var series = first.Get("series");
                    item.MediaType = "tv";
                    item.Title = series.Str("title") ?? first.Str("title") ?? string.Empty;
                    item.TmdbId = series.Long("tmdbId");
                    item.TvdbId = series.Long("tvdbId");
                    item.Poster = images.ArrImage(series, "sonarr", serviceUrl);
                    var episodes = g.Select(r => r.Get("episode")).Where(e => e.ValueKind == JsonValueKind.Object).ToList();
                    item.Detail = episodes.Count switch
                    {
                        0 => string.Empty,
                        1 => $"S{episodes[0].Int("seasonNumber")} E{episodes[0].Int("episodeNumber")}",
                        _ => episodes.Select(e => e.Int("seasonNumber")).Distinct().Count() == 1
                            ? $"Season {episodes[0].Int("seasonNumber")} · {episodes.Count} episodes"
                            : $"{episodes.Count} episodes",
                    };
                    break;
                case "radarr":
                    var movie = first.Get("movie");
                    item.MediaType = "movie";
                    item.Title = movie.Str("title") ?? first.Str("title") ?? string.Empty;
                    item.Detail = movie.Int("year") is { } y ? y.ToString(CultureInfo.InvariantCulture) : string.Empty;
                    item.TmdbId = movie.Long("tmdbId");
                    item.Poster = images.ArrImage(movie, "radarr", serviceUrl);
                    break;
                default:
                    var artist = first.Get("artist");
                    var album = first.Get("album");
                    item.MediaType = "music";
                    item.Title = artist.Str("artistName") ?? first.Str("title") ?? string.Empty;
                    item.Detail = album.Str("title") ?? string.Empty;
                    item.Poster = images.ArrImage(album, "lidarr", serviceUrl, "cover") ?? images.ArrImage(artist, "lidarr", serviceUrl);
                    break;
            }

            list.Add(item);
        }

        return list;
    }

    /// <summary>qBittorrent's torrent list as downloads (unfinished torrents only).</summary>
    /// <param name="root">/api/v2/torrents/info reply (an array).</param>
    /// <returns>Downloads.</returns>
    public static List<DownloadItem> ParseQBittorrent(JsonElement root)
    {
        var list = new List<DownloadItem>();
        foreach (var t in root.Arr())
        {
            var progress = t.Num("progress") ?? 0;
            var state = t.Str("state") ?? string.Empty;
            if (progress >= 1 || state is "uploading" or "stalledUP" or "pausedUP" or "stoppedUP" or "queuedUP" or "forcedUP" or "checkingUP")
            {
                continue;
            }

            var eta = t.Long("eta");
            list.Add(new DownloadItem
            {
                Id = "qbittorrent:" + t.Str("hash"),
                DownloadId = t.Str("hash") ?? string.Empty,
                Source = "qbittorrent",
                Title = t.Str("name") ?? string.Empty,
                Detail = t.Str("category") ?? string.Empty,
                Progress = Math.Clamp(progress, 0, 1),
                Size = t.Long("size") ?? 0,
                SizeLeft = t.Long("amount_left") ?? 0,
                // 8640000 is qBittorrent's "infinity".
                SecondsLeft = eta is > 0 and < 8640000 ? eta : null,
                Speed = t.Long("dlspeed"),
                Status = state switch
                {
                    "downloading" or "forcedDL" or "metaDL" or "forcedMetaDL" => "downloading",
                    "pausedDL" or "stoppedDL" => "paused",
                    "error" or "missingFiles" => "failed",
                    "stalledDL" => "warning",
                    _ => "queued",
                },
                Message = state == "stalledDL" ? "No peers to download from." : null,
            });
        }

        return list;
    }

    /// <summary>SABnzbd's queue as downloads.</summary>
    /// <param name="root">mode=queue reply ({queue: {slots: [...], kbpersec}}).</param>
    /// <returns>Downloads and the current speed in bytes per second.</returns>
    public static (List<DownloadItem> Items, long? Speed) ParseSabnzbd(JsonElement root)
    {
        var queue = root.Get("queue");
        var list = new List<DownloadItem>();
        foreach (var s in queue.Arr("slots"))
        {
            var mb = s.Num("mb") ?? 0;
            var left = s.Num("mbleft") ?? 0;
            var status = (s.Str("status") ?? string.Empty).ToLowerInvariant();
            list.Add(new DownloadItem
            {
                Id = "sabnzbd:" + s.Str("nzo_id"),
                DownloadId = s.Str("nzo_id") ?? string.Empty,
                Source = "sabnzbd",
                Title = s.Str("filename") ?? string.Empty,
                Detail = s.Str("cat") is { } c && c != "*" ? c : string.Empty,
                Progress = Math.Clamp((s.Num("percentage") ?? 0) / 100.0, 0, 1),
                Size = (long)(mb * 1024 * 1024),
                SizeLeft = (long)(left * 1024 * 1024),
                SecondsLeft = ParseTimeLeft(s.Str("timeleft")),
                Status = status switch
                {
                    "downloading" or "fetching" or "grabbing" => "downloading",
                    "paused" => "paused",
                    "failed" => "failed",
                    _ => "queued",
                },
            });
        }

        var kb = queue.Num("kbpersec");
        return (list, kb is null ? null : (long)(kb.Value * 1024));
    }

    /// <summary>Reads "1.02:03:04" or "02:03:04" style durations as seconds.</summary>
    /// <param name="text">The duration text.</param>
    /// <returns>Seconds, or <c>null</c>.</returns>
    public static long? ParseTimeLeft(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var t) && t > TimeSpan.Zero ? (long)t.TotalSeconds : null;
    }

    private static string Status(JsonElement r)
    {
        var state = r.Str("trackedDownloadState") ?? string.Empty;
        var tracked = r.Str("trackedDownloadStatus") ?? string.Empty;
        var status = (r.Str("status") ?? string.Empty).ToLowerInvariant();
        if (state is "importPending" or "importing" or "importBlocked")
        {
            return tracked == "warning" ? "warning" : "importing";
        }

        if (state is "failedPending" or "failed" || status == "failed" || tracked == "error")
        {
            return "failed";
        }

        if (tracked == "warning" || status is "warning" or "downloadclientunavailable")
        {
            return "warning";
        }

        return status switch
        {
            "downloading" => "downloading",
            "paused" => "paused",
            "completed" => "importing",
            _ => "queued",
        };
    }

    private static string? StatusMessage(JsonElement r)
    {
        var messages = r.Arr("statusMessages").SelectMany(m => m.Arr("messages")).Select(m => m.GetString()).OfType<string>().ToList();
        var error = r.Str("errorMessage");
        var text = error ?? messages.FirstOrDefault();
        return string.IsNullOrWhiteSpace(text) ? null : text.Length > 300 ? text[..300] : text;
    }

    private async Task<List<DownloadItem>> Queue(IntegrationKind kind, string path, CancellationToken ct)
    {
        var r = await _client.SendAsync(kind, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        using var doc = r.Json();
        return doc is null ? [] : ParseQueue(doc.RootElement, kind.ToString().ToLowerInvariant(), _images, _client.Integrations.Reveal(kind).Url);
    }

    private async Task<(List<DownloadItem>, long?)> QBittorrent(CancellationToken ct)
    {
        var r = await _client.SendAsync(IntegrationKind.QBittorrent, HttpMethod.Get, "/api/v2/torrents/info?filter=all", null, ct).ConfigureAwait(false);
        using var doc = r.Json();
        if (doc is null)
        {
            return ([], null);
        }

        var items = ParseQBittorrent(doc.RootElement);
        var t = await _client.SendAsync(IntegrationKind.QBittorrent, HttpMethod.Get, "/api/v2/transfer/info", null, ct).ConfigureAwait(false);
        using var info = t.Json();
        return (items, info?.RootElement.Long("dl_info_speed"));
    }

    private async Task<(List<DownloadItem>, long?)> Sabnzbd(CancellationToken ct)
    {
        var r = await _client.SendAsync(IntegrationKind.Sabnzbd, HttpMethod.Get, "/api?mode=queue&output=json", null, ct).ConfigureAwait(false);
        using var doc = r.Json();
        return doc is null ? ([], null) : ParseSabnzbd(doc.RootElement);
    }
}
