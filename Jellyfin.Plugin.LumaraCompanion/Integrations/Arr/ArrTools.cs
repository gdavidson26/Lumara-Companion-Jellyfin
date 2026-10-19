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
using Jellyfin.Plugin.LumaraCompanion.Integrations.Seerr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations.Arr;

/// <summary>A link an admin can open in a browser.</summary>
/// <param name="Name">App name ("Radarr", "Bazarr").</param>
/// <param name="Url">The address, built from the admin's link address.</param>
public sealed record AppLink(string Name, string Url);

/// <summary>Subtitles Bazarr knows are missing.</summary>
/// <param name="Missing">Language names.</param>
/// <param name="Link">Link to the title in Bazarr, if a link address is set.</param>
public sealed record BazarrInfo(IReadOnlyList<string> Missing, string? Link);

/// <summary>What Radarr or Sonarr (and Bazarr) say about a library title. Admins only.</summary>
public sealed class ArrItemInfo
{
    /// <summary>Gets or sets "radarr" or "sonarr".</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the app monitors it.</summary>
    public bool Monitored { get; set; }

    /// <summary>Gets or sets the quality profile name.</summary>
    public string Profile { get; set; } = string.Empty;

    /// <summary>Gets or sets the size on disk in bytes.</summary>
    public long SizeOnDisk { get; set; }

    /// <summary>Gets or sets "12 of 20 episodes" style file status for shows, or the film's quality.</summary>
    public string Files { get; set; } = string.Empty;

    /// <summary>Gets or sets the app's tag names.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Gets or sets links to the apps (only when the admin set link addresses).</summary>
    public IReadOnlyList<AppLink> Links { get; set; } = [];

    /// <summary>Gets or sets Bazarr's subtitle status, when Bazarr is set up.</summary>
    public BazarrInfo? Bazarr { get; set; }
}

/// <summary>One app's health.</summary>
public sealed class ServiceHealth
{
    /// <summary>Gets or sets the app name.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it answered.</summary>
    public bool Reachable { get; set; }

    /// <summary>Gets or sets its version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets problems it reports ("warning: ...", "error: ...").</summary>
    public IReadOnlyList<HealthIssue> Issues { get; set; } = [];

    /// <summary>Gets or sets the number of indexers (Prowlarr).</summary>
    public int? Indexers { get; set; }

    /// <summary>Gets or sets indexers that are failing (Prowlarr).</summary>
    public IReadOnlyList<string> FailingIndexers { get; set; } = [];
}

/// <summary>A problem an app reports about itself.</summary>
/// <param name="Level">"error", "warning" or "notice".</param>
/// <param name="Message">The app's message.</param>
public sealed record HealthIssue(string Level, string Message);

/// <summary>A play count.</summary>
/// <param name="Name">Title or user.</param>
/// <param name="Plays">Plays in the period.</param>
public sealed record PlayCount(string Name, long Plays);

/// <summary>Jellystat's numbers for the last 30 days.</summary>
/// <param name="Days">Period length.</param>
/// <param name="Movies">Most watched films.</param>
/// <param name="Series">Most watched shows.</param>
/// <param name="Music">Most played music.</param>
/// <param name="Users">Most active people.</param>
public sealed record WatchStats(int Days, IReadOnlyList<PlayCount> Movies, IReadOnlyList<PlayCount> Series, IReadOnlyList<PlayCount> Music, IReadOnlyList<PlayCount> Users);

/// <summary>
/// Admin tools for library titles: what Radarr/Sonarr/Bazarr know about one, a
/// search or subtitle search on demand, app health, and Jellystat's statistics.
/// </summary>
/// <remarks>
/// Matching a Jellyfin title to an app uses ids: Radarr <c>/api/v3/movie?tmdbId=</c>,
/// Sonarr <c>/api/v3/series?tvdbId=</c> (then a TMDB match over the full list).
/// Bazarr: <c>/api/movies?radarrid[]=</c>, <c>/api/episodes?seriesid[]=</c> for the
/// missing languages, and <c>PATCH /api/movies|series?…&amp;action=search-missing</c>
/// (bazarr/api/movies/movies.py) to look for them.
/// </remarks>
public sealed class ArrTools
{
    private static readonly TimeSpan ProfilesFor = TimeSpan.FromMinutes(10);

    private readonly ServiceClient _client;
    private readonly ConcurrentDictionary<string, (DateTime At, Dictionary<long, string> Map)> _names = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrTools"/> class.
    /// </summary>
    /// <param name="client">Service client.</param>
    public ArrTools(ServiceClient client)
    {
        _client = client;
    }

    /// <summary>
    /// What the apps know about a library title.
    /// </summary>
    /// <param name="item">Film, show, season or episode.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The info, or <c>null</c> when no app has it.</returns>
    public async Task<ArrItemInfo?> ItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var target = Resolve(item);
        if (target is Movie movie)
        {
            var m = await FindMovie(movie, cancellationToken).ConfigureAwait(false);
            if (m is not { } r)
            {
                return null;
            }

            var id = r.Long("id") ?? 0;
            var info = new ArrItemInfo
            {
                Service = "radarr",
                Monitored = r.Bool("monitored"),
                Profile = (await Names(IntegrationKind.Radarr, "/api/v3/qualityprofile", cancellationToken).ConfigureAwait(false)).GetValueOrDefault(r.Long("qualityProfileId") ?? -1, string.Empty),
                SizeOnDisk = r.Long("sizeOnDisk") ?? 0,
                Files = r.Bool("hasFile") ? r.Get("movieFile").Get("quality").Get("quality").Str("name") ?? "Downloaded" : "Missing",
                Tags = await TagNames(IntegrationKind.Radarr, "/api/v3/tag", r, cancellationToken).ConfigureAwait(false),
            };
            var links = new List<AppLink>();
            AddLink(links, IntegrationKind.Radarr, "Radarr", "/movie/" + (r.Str("titleSlug") ?? r.Long("tmdbId")?.ToString(CultureInfo.InvariantCulture)));
            info.Bazarr = await BazarrMovie(id, cancellationToken).ConfigureAwait(false);
            if (info.Bazarr is not null)
            {
                AddLink(links, IntegrationKind.Bazarr, "Bazarr", "/movies/" + id.ToString(CultureInfo.InvariantCulture));
                info.Bazarr = info.Bazarr with { Link = links.FirstOrDefault(l => l.Name == "Bazarr")?.Url };
            }

            info.Links = links;
            return info;
        }

        if (target is Series series)
        {
            var s = await FindSeries(series, cancellationToken).ConfigureAwait(false);
            if (s is not { } r)
            {
                return null;
            }

            var id = r.Long("id") ?? 0;
            var stats = r.Get("statistics");
            var info = new ArrItemInfo
            {
                Service = "sonarr",
                Monitored = r.Bool("monitored"),
                Profile = (await Names(IntegrationKind.Sonarr, "/api/v3/qualityprofile", cancellationToken).ConfigureAwait(false)).GetValueOrDefault(r.Long("qualityProfileId") ?? -1, string.Empty),
                SizeOnDisk = stats.Long("sizeOnDisk") ?? 0,
                Files = $"{stats.Int("episodeFileCount") ?? 0} of {stats.Int("episodeCount") ?? 0} episodes",
                Tags = await TagNames(IntegrationKind.Sonarr, "/api/v3/tag", r, cancellationToken).ConfigureAwait(false),
            };
            var links = new List<AppLink>();
            AddLink(links, IntegrationKind.Sonarr, "Sonarr", "/series/" + r.Str("titleSlug"));
            info.Bazarr = await BazarrSeries(id, cancellationToken).ConfigureAwait(false);
            if (info.Bazarr is not null)
            {
                AddLink(links, IntegrationKind.Bazarr, "Bazarr", "/series/" + id.ToString(CultureInfo.InvariantCulture));
                info.Bazarr = info.Bazarr with { Link = links.FirstOrDefault(l => l.Name == "Bazarr")?.Url };
            }

            info.Links = links;
            return info;
        }

        return null;
    }

    /// <summary>
    /// Asks Radarr or Sonarr to search for the title now, or Bazarr to look for
    /// missing subtitles.
    /// </summary>
    /// <param name="item">Library title.</param>
    /// <param name="subtitles">Bazarr subtitle search instead of a release search.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<WriteResult> SearchAsync(BaseItem item, bool subtitles, CancellationToken cancellationToken)
    {
        var target = Resolve(item);
        ServiceResponse response;
        if (target is Movie movie && await FindMovie(movie, cancellationToken).ConfigureAwait(false) is { } m)
        {
            var id = m.Long("id") ?? 0;
            response = subtitles
                ? await _client.SendAsync(IntegrationKind.Bazarr, HttpMethod.Patch, $"/api/movies?radarrid={id}&action=search-missing", null, cancellationToken).ConfigureAwait(false)
                : await _client.SendAsync(IntegrationKind.Radarr, HttpMethod.Post, "/api/v3/command", JsonSerializer.Serialize(new { name = "MoviesSearch", movieIds = new[] { id } }), cancellationToken).ConfigureAwait(false);
        }
        else if (target is Series series && await FindSeries(series, cancellationToken).ConfigureAwait(false) is { } s)
        {
            var id = s.Long("id") ?? 0;
            response = subtitles
                ? await _client.SendAsync(IntegrationKind.Bazarr, HttpMethod.Patch, $"/api/series?seriesid={id}&action=search-missing", null, cancellationToken).ConfigureAwait(false)
                : await _client.SendAsync(IntegrationKind.Sonarr, HttpMethod.Post, "/api/v3/command", JsonSerializer.Serialize(new { name = "SeriesSearch", seriesId = id }), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return new WriteResult(false, "Radarr or Sonarr doesn't have this title.");
        }

        return response.Ok
            ? new WriteResult(true, subtitles ? "Bazarr is looking for missing subtitles." : "Search started.")
            : response.Status == ServiceResponse.NotConfigured
                ? new WriteResult(false, subtitles ? "Bazarr isn't set up." : "That app isn't set up.")
                : new WriteResult(false, $"The app couldn't start the search ({response.Status}).");
    }

    /// <summary>Health of every configured *arr app (and Bazarr, Prowlarr's indexers).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One entry per configured app.</returns>
    public async Task<IReadOnlyList<ServiceHealth>> HealthAsync(CancellationToken cancellationToken)
    {
        var apps = new (IntegrationKind Kind, string Status, string? Health)[]
        {
            (IntegrationKind.Sonarr, "/api/v3/system/status", "/api/v3/health"),
            (IntegrationKind.Radarr, "/api/v3/system/status", "/api/v3/health"),
            (IntegrationKind.Lidarr, "/api/v1/system/status", "/api/v1/health"),
            (IntegrationKind.Prowlarr, "/api/v1/system/status", "/api/v1/health"),
            (IntegrationKind.Bazarr, "/api/system/status", null),
        };
        var list = new List<ServiceHealth>();
        foreach (var (kind, statusPath, healthPath) in apps)
        {
            if (!_client.Integrations.IsConfigured(kind))
            {
                continue;
            }

            var entry = new ServiceHealth { Service = kind.ToString() };
            var status = await _client.SendAsync(kind, HttpMethod.Get, statusPath, null, cancellationToken).ConfigureAwait(false);
            using (var doc = status.Json())
            {
                entry.Reachable = doc is not null;
                // Bazarr wraps it: {data: {bazarr_version}}.
                entry.Version = doc?.RootElement.Str("version") ?? doc?.RootElement.Get("data").Str("bazarr_version") ?? string.Empty;
            }

            if (entry.Reachable && healthPath is not null)
            {
                using var health = (await _client.SendAsync(kind, HttpMethod.Get, healthPath, null, cancellationToken).ConfigureAwait(false)).Json();
                entry.Issues = health?.RootElement.Arr()
                    .Select(h => new HealthIssue((h.Str("type") ?? "notice").ToLowerInvariant(), h.Str("message") ?? string.Empty))
                    .Where(h => h.Message.Length > 0)
                    .ToList() ?? [];
            }

            if (entry.Reachable && kind == IntegrationKind.Prowlarr)
            {
                using var indexers = (await _client.SendAsync(kind, HttpMethod.Get, "/api/v1/indexer", null, cancellationToken).ConfigureAwait(false)).Json();
                using var failing = (await _client.SendAsync(kind, HttpMethod.Get, "/api/v1/indexerstatus", null, cancellationToken).ConfigureAwait(false)).Json();
                var names = indexers?.RootElement.Arr().ToDictionary(i => i.Long("id") ?? 0, i => i.Str("name") ?? string.Empty) ?? [];
                entry.Indexers = names.Count;
                entry.FailingIndexers = failing?.RootElement.Arr()
                    .Where(s => s.Date("disabledTill") is { } until && until > DateTime.UtcNow)
                    .Select(s => names.GetValueOrDefault(s.Long("indexerId") ?? 0, "Indexer"))
                    .ToList() ?? [];
            }

            list.Add(entry);
        }

        return list;
    }

    /// <summary>
    /// Jellystat's most-watched lists for the last 30 days
    /// (<c>POST /stats/getMostViewedByType</c> and <c>/stats/getMostActiveUsers</c>).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The numbers, or <c>null</c> when Jellystat isn't set up or answering.</returns>
    public async Task<WatchStats?> StatsAsync(CancellationToken cancellationToken)
    {
        const int Days = 30;
        if (!_client.Integrations.IsConfigured(IntegrationKind.Jellystat))
        {
            return null;
        }

        async Task<IReadOnlyList<PlayCount>?> Top(string path, object body)
        {
            using var doc = (await _client.SendAsync(IntegrationKind.Jellystat, HttpMethod.Post, path, JsonSerializer.Serialize(body), cancellationToken).ConfigureAwait(false)).Json();
            return doc?.RootElement.Arr()
                .Select(r => new PlayCount(r.Str("Name") ?? r.Str("UserName") ?? r.Str("name") ?? string.Empty, r.Long("Plays") ?? r.Long("plays") ?? 0))
                .Where(p => p.Name.Length > 0)
                .Take(5)
                .ToList();
        }

        var movies = await Top("/stats/getMostViewedByType", new { days = Days, type = "Movie" }).ConfigureAwait(false);
        if (movies is null)
        {
            return null;
        }

        var series = await Top("/stats/getMostViewedByType", new { days = Days, type = "Series" }).ConfigureAwait(false) ?? [];
        var music = await Top("/stats/getMostViewedByType", new { days = Days, type = "Audio" }).ConfigureAwait(false) ?? [];
        var users = await Top("/stats/getMostActiveUsers", new { days = Days }).ConfigureAwait(false) ?? [];
        return new WatchStats(Days, movies, series, music, users);
    }

    /// <summary>The film or show a library item belongs to.</summary>
    /// <param name="item">Any library item.</param>
    /// <returns>The film or show, or <c>null</c>.</returns>
    public static BaseItem? Resolve(BaseItem? item) => item switch
    {
        Episode e => e.Series,
        Season s => s.Series,
        Movie or Series => item,
        _ => null,
    };

    private void AddLink(List<AppLink> links, IntegrationKind kind, string name, string? path)
    {
        var baseUrl = _client.Integrations.LinkUrl(kind);
        if (baseUrl.Length > 0 && !string.IsNullOrEmpty(path))
        {
            links.Add(new AppLink(name, baseUrl + path));
        }
    }

    private async Task<JsonElement?> FindMovie(Movie movie, CancellationToken ct)
    {
        var tmdb = movie.GetProviderId(MetadataProvider.Tmdb);
        if (string.IsNullOrEmpty(tmdb) || !tmdb.All(char.IsAsciiDigit))
        {
            return null;
        }

        var r = await _client.SendAsync(IntegrationKind.Radarr, HttpMethod.Get, "/api/v3/movie?tmdbId=" + tmdb, null, ct).ConfigureAwait(false);
        using var doc = r.Json();
        var first = doc?.RootElement.Arr().FirstOrDefault() ?? default;
        return first.ValueKind == JsonValueKind.Object ? first.Clone() : null;
    }

    private async Task<JsonElement?> FindSeries(Series series, CancellationToken ct)
    {
        var tvdb = series.GetProviderId(MetadataProvider.Tvdb);
        if (!string.IsNullOrEmpty(tvdb) && tvdb.All(char.IsAsciiDigit))
        {
            using var doc = (await _client.SendAsync(IntegrationKind.Sonarr, HttpMethod.Get, "/api/v3/series?tvdbId=" + tvdb, null, ct).ConfigureAwait(false)).Json();
            var hit = doc?.RootElement.Arr().FirstOrDefault(s => s.Long("tvdbId")?.ToString(CultureInfo.InvariantCulture) == tvdb) ?? default;
            if (hit.ValueKind == JsonValueKind.Object)
            {
                return hit.Clone();
            }
        }

        // No TVDB id in Jellyfin (or an older Sonarr ignoring the filter): match by TMDB over the list.
        var tmdb = series.GetProviderId(MetadataProvider.Tmdb);
        if (string.IsNullOrEmpty(tmdb))
        {
            return null;
        }

        using var all = (await _client.SendAsync(IntegrationKind.Sonarr, HttpMethod.Get, "/api/v3/series", null, ct).ConfigureAwait(false)).Json();
        var match = all?.RootElement.Arr().FirstOrDefault(s => s.Long("tmdbId")?.ToString(CultureInfo.InvariantCulture) == tmdb) ?? default;
        return match.ValueKind == JsonValueKind.Object ? match.Clone() : null;
    }

    private async Task<BazarrInfo?> BazarrMovie(long radarrId, CancellationToken ct)
    {
        using var doc = (await _client.SendAsync(IntegrationKind.Bazarr, HttpMethod.Get, $"/api/movies?radarrid[]={radarrId}", null, ct).ConfigureAwait(false)).Json();
        if (doc is null)
        {
            return null;
        }

        var missing = doc.RootElement.Arr("data").SelectMany(m => m.Arr("missing_subtitles")).Select(LanguageName).OfType<string>().Distinct().ToList();
        return new BazarrInfo(missing, null);
    }

    private async Task<BazarrInfo?> BazarrSeries(long sonarrId, CancellationToken ct)
    {
        using var doc = (await _client.SendAsync(IntegrationKind.Bazarr, HttpMethod.Get, $"/api/episodes?seriesid[]={sonarrId}", null, ct).ConfigureAwait(false)).Json();
        if (doc is null)
        {
            return null;
        }

        var missing = doc.RootElement.Arr("data").SelectMany(m => m.Arr("missing_subtitles")).Select(LanguageName).OfType<string>().Distinct().ToList();
        return new BazarrInfo(missing, null);
    }

    private static string? LanguageName(JsonElement l)
    {
        var name = l.Str("name") ?? l.Str("code2");
        if (name is null)
        {
            return null;
        }

        // Bazarr flags hearing-impaired and forced variants separately.
        return l.Bool("hi") ? name + " (SDH)" : l.Bool("forced") ? name + " (forced)" : name;
    }

    private async Task<IReadOnlyList<string>> TagNames(IntegrationKind kind, string path, JsonElement item, CancellationToken ct)
    {
        var names = await Names(kind, path, ct, "label").ConfigureAwait(false);
        return item.Arr("tags").Select(t => t.ValueKind == JsonValueKind.Number ? names.GetValueOrDefault(t.GetInt64()) : null).OfType<string>().ToList();
    }

    private async Task<Dictionary<long, string>> Names(IntegrationKind kind, string path, CancellationToken ct, string field = "name")
    {
        var key = kind + path + "#" + _client.Integrations.Revision.ToString(CultureInfo.InvariantCulture);
        if (_names.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < ProfilesFor)
        {
            return hit.Map;
        }

        using var doc = (await _client.SendAsync(kind, HttpMethod.Get, path, null, ct).ConfigureAwait(false)).Json();
        var map = doc?.RootElement.Arr()
            .Where(p => p.Long("id") is not null)
            .GroupBy(p => p.Long("id")!.Value)
            .ToDictionary(g => g.Key, g => g.First().Str(field) ?? string.Empty) ?? [];
        _names[key] = (DateTime.UtcNow, map);
        return map;
    }
}
