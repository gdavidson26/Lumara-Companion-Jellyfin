// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations.Arr;

/// <summary>One thing coming out on a day.</summary>
public sealed class CalendarEntry
{
    /// <summary>Gets or sets a stable id ("sonarr:123", "radarr:45:digital").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets when (ISO 8601 UTC). For <see cref="AllDay"/> entries, midnight UTC of that date.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether only the date is known (film releases, albums).</summary>
    public bool AllDay { get; set; }

    /// <summary>Gets or sets "episode", "movie" or "album".</summary>
    public string Kind { get; set; } = "episode";

    /// <summary>Gets or sets "sonarr", "radarr", "lidarr" or "shoko".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the show, film or artist.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode or album title.</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the season number (episodes).</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets "airing", "cinema", "digital", "physical" or "album".</summary>
    public string Release { get; set; } = "airing";

    /// <summary>Gets or sets a value indicating whether the file is already downloaded.</summary>
    public bool HasFile { get; set; }

    /// <summary>Gets or sets the signed poster address.</summary>
    public string? Poster { get; set; }

    /// <summary>Gets or sets the library item (show or film) when it's in the library and visible.</summary>
    public string? ItemId { get; set; }
}

/// <summary>
/// Upcoming episodes, films and albums from Sonarr, Radarr, Lidarr and Shoko, in
/// one list. Raw replies are cached for five minutes (a calendar page opens many
/// times while someone browses weeks); filtering by viewer happens per request.
/// </summary>
/// <remarks>
/// Endpoints: Sonarr <c>/api/v3/calendar?includeSeries=true</c> (episodes with
/// <c>airDateUtc</c>), Radarr <c>/api/v3/calendar</c> (films with <c>inCinemas</c>,
/// <c>digitalRelease</c>, <c>physicalRelease</c>; each date in range is one entry),
/// Lidarr <c>/api/v1/calendar?includeArtist=true</c> (albums with
/// <c>releaseDate</c>), Shoko <c>/api/v3/Dashboard/AniDBCalendar</c> (episodes with
/// <c>AirDate</c>, a date only; restricted series left out). Unmonitored titles are
/// left out everywhere.
/// </remarks>
public sealed class CalendarService
{
    /// <summary>Longest range one request may ask for.</summary>
    public const int MaxDays = 62;

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly ServiceClient _client;
    private readonly LibraryIndex _index;
    private readonly ImageSigner _images;
    private readonly ILocalizationManager? _localization;
    private readonly ConcurrentDictionary<string, (DateTime At, List<(CalendarEntry Entry, string? Certification, string MediaType, long? Tmdb, long? Tvdb)> Rows)> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CalendarService"/> class.
    /// </summary>
    /// <param name="client">Service client.</param>
    /// <param name="index">Library lookups.</param>
    /// <param name="images">Signs posters.</param>
    /// <param name="localization">Rating scores (optional in tests).</param>
    public CalendarService(ServiceClient client, LibraryIndex index, ImageSigner images, ILocalizationManager? localization)
    {
        _client = client;
        _index = index;
        _images = images;
        _localization = localization;
    }

    /// <summary>Gets a value indicating whether any calendar source is set up.</summary>
    public bool Available =>
        _client.Integrations.IsConfigured(IntegrationKind.Sonarr)
        || _client.Integrations.IsConfigured(IntegrationKind.Radarr)
        || _client.Integrations.IsConfigured(IntegrationKind.Lidarr)
        || _client.Integrations.IsConfigured(IntegrationKind.Shoko);

    /// <summary>
    /// Entries between two dates for a viewer.
    /// </summary>
    /// <param name="user">The viewer (parental limit and library links apply).</param>
    /// <param name="start">First day (UTC).</param>
    /// <param name="end">Day after the last (UTC).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Entries in time order.</returns>
    public async Task<IReadOnlyList<CalendarEntry>> GetAsync(User user, DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        start = start.Date;
        end = end.Date;
        if (end <= start)
        {
            end = start.AddDays(1);
        }

        if ((end - start).TotalDays > MaxDays)
        {
            end = start.AddDays(MaxDays);
        }

        var cacheKey = start.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + end.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (!_cache.TryGetValue(cacheKey, out var hit) || DateTime.UtcNow - hit.At > CacheFor)
        {
            var rows = new List<(CalendarEntry, string?, string, long?, long?)>();
            var tasks = new[]
            {
                Sonarr(start, end, cancellationToken),
                Radarr(start, end, cancellationToken),
                Lidarr(start, end, cancellationToken),
                Shoko(start, end, cancellationToken),
            };
            foreach (var part in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                rows.AddRange(part);
            }

            if (_cache.Count > 50)
            {
                _cache.Clear();
            }

            hit = (DateTime.UtcNow, rows);
            _cache[cacheKey] = hit;
        }

        var limit = Parental.Limit(user);
        var result = new List<CalendarEntry>();
        foreach (var (entry, cert, type, tmdb, tvdb) in hit.Rows)
        {
            // Albums have no rating system; everything else needs to be within the limit.
            if (limit is not null && entry.Kind != "album" && !Parental.Allows(limit, cert, _localization))
            {
                continue;
            }

            var copy = Clone(entry);
            copy.ItemId = type is "movie" or "tv" ? _index.FindVisible(user, type, tmdb, tvdb)?.Id.ToString("N") : null;
            result.Add(copy);
        }

        return result.OrderBy(e => e.Date, StringComparer.Ordinal).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Sonarr's calendar reply as entries.</summary>
    /// <param name="root">Reply root (an array).</param>
    /// <param name="images">Signs posters.</param>
    /// <param name="serviceUrl">Stored Sonarr address (for image paths).</param>
    /// <returns>Rows with each show's certification and ids.</returns>
    public static List<(CalendarEntry Entry, string? Certification, string MediaType, long? Tmdb, long? Tvdb)> ParseSonarr(JsonElement root, ImageSigner images, string? serviceUrl)
    {
        var list = new List<(CalendarEntry, string?, string, long?, long?)>();
        foreach (var e in root.Arr())
        {
            if (!e.Bool("monitored", true) || e.Date("airDateUtc") is not { } when)
            {
                continue;
            }

            var series = e.Get("series");
            list.Add((
                new CalendarEntry
                {
                    Id = "sonarr:" + e.Long("id"),
                    Date = Json.Iso(when),
                    Kind = "episode",
                    Source = "sonarr",
                    Title = series.Str("title") ?? string.Empty,
                    Subtitle = e.Str("title") ?? string.Empty,
                    Season = e.Int("seasonNumber"),
                    Episode = e.Int("episodeNumber"),
                    Release = "airing",
                    HasFile = e.Bool("hasFile"),
                    Poster = images.ArrImage(series, "sonarr", serviceUrl),
                },
                series.Str("certification"),
                "tv",
                series.Long("tmdbId"),
                series.Long("tvdbId")));
        }

        return list;
    }

    /// <summary>Radarr's calendar reply as entries (one per release date in range).</summary>
    /// <param name="root">Reply root (an array).</param>
    /// <param name="images">Signs posters.</param>
    /// <param name="serviceUrl">Stored Radarr address.</param>
    /// <param name="start">Range start.</param>
    /// <param name="end">Range end.</param>
    /// <returns>Rows.</returns>
    public static List<(CalendarEntry Entry, string? Certification, string MediaType, long? Tmdb, long? Tvdb)> ParseRadarr(JsonElement root, ImageSigner images, string? serviceUrl, DateTime start, DateTime end)
    {
        var list = new List<(CalendarEntry, string?, string, long?, long?)>();
        foreach (var m in root.Arr())
        {
            if (!m.Bool("monitored", true))
            {
                continue;
            }

            foreach (var (field, release) in new[] { ("inCinemas", "cinema"), ("digitalRelease", "digital"), ("physicalRelease", "physical") })
            {
                if (m.Date(field) is not { } when || when < start || when >= end)
                {
                    continue;
                }

                list.Add((
                    new CalendarEntry
                    {
                        Id = "radarr:" + m.Long("id") + ":" + release,
                        Date = Json.Iso(when.Date),
                        AllDay = true,
                        Kind = "movie",
                        Source = "radarr",
                        Title = m.Str("title") ?? string.Empty,
                        Subtitle = m.Int("year") is { } y ? y.ToString(CultureInfo.InvariantCulture) : string.Empty,
                        Release = release,
                        HasFile = m.Bool("hasFile"),
                        Poster = images.ArrImage(m, "radarr", serviceUrl),
                    },
                    m.Str("certification"),
                    "movie",
                    m.Long("tmdbId"),
                    null));
            }
        }

        return list;
    }

    /// <summary>Lidarr's calendar reply as entries.</summary>
    /// <param name="root">Reply root (an array).</param>
    /// <param name="images">Signs covers.</param>
    /// <param name="serviceUrl">Stored Lidarr address.</param>
    /// <returns>Rows.</returns>
    public static List<(CalendarEntry Entry, string? Certification, string MediaType, long? Tmdb, long? Tvdb)> ParseLidarr(JsonElement root, ImageSigner images, string? serviceUrl)
    {
        var list = new List<(CalendarEntry, string?, string, long?, long?)>();
        foreach (var a in root.Arr())
        {
            if (!a.Bool("monitored", true) || a.Date("releaseDate") is not { } when)
            {
                continue;
            }

            var artist = a.Get("artist");
            list.Add((
                new CalendarEntry
                {
                    Id = "lidarr:" + a.Long("id"),
                    Date = Json.Iso(when.Date),
                    AllDay = true,
                    Kind = "album",
                    Source = "lidarr",
                    Title = artist.Str("artistName") ?? string.Empty,
                    Subtitle = a.Str("title") ?? string.Empty,
                    Release = "album",
                    HasFile = (a.Get("statistics").Int("trackFileCount") ?? 0) > 0,
                    Poster = images.ArrImage(a, "lidarr", serviceUrl, "cover") ?? images.ArrImage(artist, "lidarr", serviceUrl),
                },
                null,
                "music",
                null,
                null));
        }

        return list;
    }

    /// <summary>Shoko's AniDB calendar as entries.</summary>
    /// <param name="root">Reply root (an array of Dashboard.Episode).</param>
    /// <param name="images">Signs posters.</param>
    /// <param name="start">Range start.</param>
    /// <param name="end">Range end.</param>
    /// <returns>Rows (anime carries no certification).</returns>
    public static List<(CalendarEntry Entry, string? Certification, string MediaType, long? Tmdb, long? Tvdb)> ParseShoko(JsonElement root, ImageSigner images, DateTime start, DateTime end)
    {
        var list = new List<(CalendarEntry, string?, string, long?, long?)>();
        foreach (var e in root.Arr())
        {
            if (e.Date("AirDate") is not { } when || when < start || when >= end)
            {
                continue;
            }

            var poster = e.Get("SeriesPoster");
            var posterPath = poster.Str("Source") is { } src && poster.Str("Type") is { } type && poster.Str("ID") is { } id
                ? $"/api/v3/Image/{src}/{type}/{id}"
                : null;
            list.Add((
                new CalendarEntry
                {
                    Id = "shoko:" + e.Get("IDs").Long("ID"),
                    Date = Json.Iso(when.Date),
                    AllDay = true,
                    Kind = "episode",
                    Source = "shoko",
                    Title = e.Str("SeriesTitle") ?? string.Empty,
                    Subtitle = e.Str("Title") ?? string.Empty,
                    Episode = e.Int("Number"),
                    Release = "airing",
                    HasFile = false,
                    Poster = images.Url("shoko", posterPath),
                },
                null,
                "anime",
                null,
                null));
        }

        return list;
    }

    private static CalendarEntry Clone(CalendarEntry e) => new()
    {
        Id = e.Id,
        Date = e.Date,
        AllDay = e.AllDay,
        Kind = e.Kind,
        Source = e.Source,
        Title = e.Title,
        Subtitle = e.Subtitle,
        Season = e.Season,
        Episode = e.Episode,
        Release = e.Release,
        HasFile = e.HasFile,
        Poster = e.Poster,
    };

    private static string Range(DateTime start, DateTime end) =>
        $"start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}";

    private async Task<List<(CalendarEntry, string?, string, long?, long?)>> Sonarr(DateTime start, DateTime end, CancellationToken ct)
    {
        var r = await _client.SendAsync(IntegrationKind.Sonarr, HttpMethod.Get, $"/api/v3/calendar?{Range(start, end)}&unmonitored=false&includeSeries=true", null, ct).ConfigureAwait(false);
        using var doc = r.Json();
        return doc is null ? [] : ParseSonarr(doc.RootElement, _images, _client.Integrations.Reveal(IntegrationKind.Sonarr).Url);
    }

    private async Task<List<(CalendarEntry, string?, string, long?, long?)>> Radarr(DateTime start, DateTime end, CancellationToken ct)
    {
        var r = await _client.SendAsync(IntegrationKind.Radarr, HttpMethod.Get, $"/api/v3/calendar?{Range(start, end)}&unmonitored=false", null, ct).ConfigureAwait(false);
        using var doc = r.Json();
        return doc is null ? [] : ParseRadarr(doc.RootElement, _images, _client.Integrations.Reveal(IntegrationKind.Radarr).Url, start, end);
    }

    private async Task<List<(CalendarEntry, string?, string, long?, long?)>> Lidarr(DateTime start, DateTime end, CancellationToken ct)
    {
        var r = await _client.SendAsync(IntegrationKind.Lidarr, HttpMethod.Get, $"/api/v1/calendar?{Range(start, end)}&unmonitored=false&includeArtist=true", null, ct).ConfigureAwait(false);
        using var doc = r.Json();
        return doc is null ? [] : ParseLidarr(doc.RootElement, _images, _client.Integrations.Reveal(IntegrationKind.Lidarr).Url);
    }

    private async Task<List<(CalendarEntry, string?, string, long?, long?)>> Shoko(DateTime start, DateTime end, CancellationToken ct)
    {
        // Shoko only looks forward from today, so past days of the range come back empty.
        var days = (int)Math.Ceiling((end - DateTime.UtcNow.Date).TotalDays);
        if (days <= 0)
        {
            return [];
        }

        var r = await _client.SendAsync(IntegrationKind.Shoko, HttpMethod.Get, $"/api/v3/Dashboard/AniDBCalendar?numberOfDays={Math.Min(days, MaxDays + 31)}&showAll=false&includeRestricted=false", null, ct).ConfigureAwait(false);
        using var doc = r.Json();
        return doc is null ? [] : ParseShoko(doc.RootElement, _images, start, end);
    }
}
