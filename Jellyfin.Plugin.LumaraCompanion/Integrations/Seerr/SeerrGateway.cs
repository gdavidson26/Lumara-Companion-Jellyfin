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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations.Seerr;

/// <summary>A Seerr account linked to a Jellyfin user.</summary>
/// <param name="Id">Seerr user id.</param>
/// <param name="Permissions">Permission bits.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="JellyfinUserId">Linked Jellyfin user id (32 hex digits).</param>
/// <param name="RequestCount">Number of requests made.</param>
public sealed record SeerrUser(long Id, long Permissions, string DisplayName, string JellyfinUserId, int RequestCount);

/// <summary>
/// Talks to Seerr on behalf of each Jellyfin user ("single sign-on").
/// </summary>
/// <remarks>
/// <para>
/// The admin's Seerr API key stays on this server. Every call made for a viewer
/// carries <c>X-Api-User: &lt;their Seerr id&gt;</c>, which makes Seerr act as that
/// user (seerr server/middleware/auth.ts), so Seerr applies their own permissions,
/// quotas and auto-approval exactly as if they'd signed in to Seerr. The Seerr
/// account is the one Seerr linked to their Jellyfin account (its
/// <c>jellyfinUserId</c>); people without one see requests as unavailable, and an
/// admin can import them (<see cref="ImportAsync"/>).
/// </para>
/// <para>
/// Parental limits: Seerr doesn't know Jellyfin's, so for a limited viewer search
/// stays library-only, discovery rows come from TMDB's certification filter, and
/// title pages above the limit aren't served.
/// </para>
/// </remarks>
public sealed partial class SeerrGateway : IDisposable
{
    // Seerr checks permissions on every call itself; this cache only decides what
    // Lumara shows, so a permission change in Seerr appears within a minute.
    private static readonly TimeSpan UsersFor = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UsersMissRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DetailsFor = TimeSpan.FromHours(6);
    private static readonly TimeSpan ServicesFor = TimeSpan.FromMinutes(10);
    private const int MaxDetails = 3000;

    private static readonly HashSet<string> Sliders = new(StringComparer.Ordinal)
    {
        "trending", "movies", "tv", "movies-upcoming", "tv-upcoming", "movie-genre", "tv-genre", "tv-network", "movie-studio",
    };

    private readonly ServiceClient _client;
    private readonly LibraryIndex _index;
    private readonly ImageSigner _images;
    private readonly ILocalizationManager? _localization;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, (DateTime At, string Body)> _details = new();
    private readonly ConcurrentDictionary<string, (DateTime At, string Body)> _services = new();
    private readonly SemaphoreSlim _usersGate = new(1, 1);
    private IReadOnlyList<SeerrUser> _users = [];
    private DateTime _usersAt = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrGateway"/> class.
    /// </summary>
    /// <param name="client">Calls Seerr with the stored key.</param>
    /// <param name="index">Finds library items for results.</param>
    /// <param name="images">Signs poster addresses.</param>
    /// <param name="localization">Rating scores for parental limits (optional in tests).</param>
    /// <param name="clock">Current time (UTC), injectable for tests.</param>
    public SeerrGateway(ServiceClient client, LibraryIndex index, ImageSigner images, ILocalizationManager? localization, Func<DateTime>? clock = null)
    {
        _client = client;
        _index = index;
        _images = images;
        _localization = localization;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc />
    public void Dispose() => _usersGate.Dispose();

    /// <summary>Gets a value indicating whether Seerr is set up.</summary>
    public bool Configured => _client.Integrations.IsConfigured(IntegrationKind.Seerr);

    /// <summary>
    /// The Seerr account linked to a Jellyfin user, or <c>null</c>.
    /// </summary>
    /// <param name="jellyfinUserId">Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The account.</returns>
    public async Task<SeerrUser?> UserForAsync(Guid jellyfinUserId, CancellationToken cancellationToken)
    {
        if (!Configured)
        {
            return null;
        }

        var key = jellyfinUserId.ToString("N");
        var users = await UsersAsync(false, cancellationToken).ConfigureAwait(false);
        var hit = users.FirstOrDefault(u => u.JellyfinUserId == key);
        if (hit is null && _clock() - _usersAt > UsersMissRetry)
        {
            // Maybe imported a moment ago.
            users = await UsersAsync(true, cancellationToken).ConfigureAwait(false);
            hit = users.FirstOrDefault(u => u.JellyfinUserId == key);
        }

        return hit;
    }

    /// <summary>
    /// Fills in the Seerr part of a person's features.
    /// </summary>
    /// <param name="features">Features to update.</param>
    /// <param name="user">The viewer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task FillFeaturesAsync(RequestFeatures features, User user, CancellationToken cancellationToken)
    {
        features.Seerr = Configured;
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null)
        {
            return;
        }

        var p = seerr.Permissions;
        features.Linked = true;
        features.RequestMovies = SeerrMapper.Has(p, SeerrAccess.Request) || SeerrMapper.Has(p, SeerrAccess.RequestMovie);
        features.RequestTv = SeerrMapper.Has(p, SeerrAccess.Request) || SeerrMapper.Has(p, SeerrAccess.RequestTv);
        var (movie4k, tv4k) = await Has4kServersAsync(seerr, cancellationToken).ConfigureAwait(false);
        features.Request4kMovies = movie4k && (SeerrMapper.Has(p, SeerrAccess.Request4k) || SeerrMapper.Has(p, SeerrAccess.Request4kMovie));
        features.Request4kTv = tv4k && (SeerrMapper.Has(p, SeerrAccess.Request4k) || SeerrMapper.Has(p, SeerrAccess.Request4kTv));
        features.Advanced = SeerrMapper.Has(p, SeerrAccess.RequestAdvanced) || SeerrMapper.Has(p, SeerrAccess.ManageRequests);
        features.ManageRequests = SeerrMapper.Has(p, SeerrAccess.ManageRequests);
        features.CreateIssues = SeerrMapper.Has(p, SeerrAccess.CreateIssues) || SeerrMapper.Has(p, SeerrAccess.ManageIssues);
        features.ManageIssues = SeerrMapper.Has(p, SeerrAccess.ManageIssues);
    }

    /// <summary>
    /// Searches Seerr (TMDB) for films and shows. A trailing year ("Dune 1984" or
    /// "Dune (1984)") narrows the results to that year, since Seerr's search has
    /// no year filter of its own.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="query">Search text.</param>
    /// <param name="page">Page (1-based).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The results, or <c>null</c> when the viewer can't use Seerr.</returns>
    public async Task<DiscoverPage?> SearchAsync(User user, string query, int page, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null)
        {
            return null;
        }

        // Search results carry no certification, so a limited viewer only searches the library.
        if (Parental.Limit(user) is not null)
        {
            return new DiscoverPage(1, 1, []);
        }

        var (text, year) = SplitYear(query.Trim());
        if (text.Length == 0)
        {
            return new DiscoverPage(1, 1, []);
        }

        // Seerr passes the query to TMDB as-is and rejects unencoded reserved characters.
        var path = $"/api/v1/search?query={Uri.EscapeDataString(text)}&page={Math.Max(1, page)}";
        using var doc = (await AsUserAsync(seerr, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false)).Json();
        if (doc is null)
        {
            return new DiscoverPage(1, 1, []);
        }

        return SeerrMapper.Page(doc.RootElement, _images, ItemLookup(user), null, (r, item) => !r.Bool("adult") && (year is null || item.Year == year));
    }

    /// <summary>
    /// One discovery row.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="slider">"trending", "movies", "tv", "movies-upcoming", "tv-upcoming", "movie-genre", "tv-genre", "tv-network" or "movie-studio".</param>
    /// <param name="id">Genre, network or studio id for the last four.</param>
    /// <param name="page">Page (1-based).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The row, or <c>null</c> when unavailable.</returns>
    public async Task<DiscoverPage?> DiscoverAsync(User user, string slider, long? id, int page, CancellationToken cancellationToken)
    {
        if (!Sliders.Contains(slider))
        {
            return null;
        }

        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null)
        {
            return null;
        }

        var tv = slider.StartsWith("tv", StringComparison.Ordinal);
        var limit = Parental.Limit(user);
        string path;
        string? forcedType = slider switch
        {
            "trending" => null,
            _ => tv ? "tv" : "movie",
        };
        if (limit is not null)
        {
            // TMDB's own certification filter, so every card is within the limit.
            var cert = Parental.HighestAllowed(limit.Value, tv, _localization);
            if (cert is null)
            {
                return new DiscoverPage(1, 1, []);
            }

            var extra = slider switch
            {
                "movie-genre" or "tv-genre" => $"&genre={id}",
                "tv-network" => $"&network={id}",
                "movie-studio" => $"&studio={id}",
                _ => string.Empty,
            };
            forcedType = tv ? "tv" : "movie";
            path = $"/api/v1/discover/{(tv ? "tv" : "movies")}?page={page}&certificationCountry=US&certificationLte={Uri.EscapeDataString(cert)}{extra}";
        }
        else
        {
            path = slider switch
            {
                "trending" => $"/api/v1/discover/trending?page={page}",
                "movies" => $"/api/v1/discover/movies?page={page}",
                "tv" => $"/api/v1/discover/tv?page={page}",
                "movies-upcoming" => $"/api/v1/discover/movies/upcoming?page={page}",
                "tv-upcoming" => $"/api/v1/discover/tv/upcoming?page={page}",
                "movie-genre" => $"/api/v1/discover/movies/genre/{id}?page={page}",
                "tv-genre" => $"/api/v1/discover/tv/genre/{id}?page={page}",
                "tv-network" => $"/api/v1/discover/tv/network/{id}?page={page}",
                _ => $"/api/v1/discover/movies/studio/{id}?page={page}",
            };
        }

        if (slider is "movie-genre" or "tv-genre" or "tv-network" or "movie-studio" && id is null or <= 0)
        {
            return null;
        }

        using var doc = (await AsUserAsync(seerr, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false)).Json();
        return doc is null ? new DiscoverPage(1, 1, []) : SeerrMapper.Page(doc.RootElement, _images, ItemLookup(user), forcedType, (r, _) => !r.Bool("adult"));
    }

    /// <summary>Genres to browse, each with a backdrop.</summary>
    /// <param name="user">The viewer.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The genres, or <c>null</c>.</returns>
    public async Task<IReadOnlyList<BrowseOption>?> GenresAsync(User user, string mediaType, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null || mediaType is not ("movie" or "tv"))
        {
            return null;
        }

        using var doc = (await AsUserAsync(seerr, HttpMethod.Get, "/api/v1/discover/genreslider/" + mediaType, null, cancellationToken).ConfigureAwait(false)).Json();
        if (doc is null)
        {
            return [];
        }

        return doc.RootElement.Arr()
            .Select(g => new BrowseOption(g.Long("id") ?? 0, g.Str("name") ?? string.Empty, _images.Tmdb(g.Arr("backdrops").Select(b => b.GetString()).FirstOrDefault(), "w780")))
            .Where(g => g.Id > 0 && g.Name.Length > 0)
            .ToList();
    }

    /// <summary>
    /// A film or show's page: details, seasons, what's requested, and what this
    /// person can request.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The details, or <c>null</c> (not found, not allowed, or no Seerr account).</returns>
    public async Task<TitleDetails?> TitleAsync(User user, string mediaType, long tmdbId, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null || mediaType is not ("movie" or "tv") || tmdbId <= 0)
        {
            return null;
        }

        // Fresh, not cached: the status and requests on this page must be current.
        var response = await AsUserAsync(seerr, HttpMethod.Get, $"/api/v1/{mediaType}/{tmdbId}", null, cancellationToken).ConfigureAwait(false);
        using var doc = response.Json();
        if (doc is null)
        {
            return null;
        }

        CacheDetails(mediaType, tmdbId, response.Body);
        var root = doc.RootElement;
        var details = SeerrMapper.Details(root, mediaType, _images, ItemLookup(user));
        if (!Parental.Allows(Parental.Limit(user), details.Certification, _localization) || root.Bool("adult"))
        {
            return null;
        }

        var p = seerr.Permissions;
        var media = root.Get("mediaInfo");
        var tv = mediaType == "tv";
        var canKind = SeerrMapper.Has(p, SeerrAccess.Request) || SeerrMapper.Has(p, tv ? SeerrAccess.RequestTv : SeerrAccess.RequestMovie);
        var openRequests = media.Arr("requests").Where(r => r.Long("status") is 1 or 2).ToList();
        details.Requests = openRequests
            .Select(r =>
            {
                var info = SeerrMapper.Request(r, seerr.Id, p);
                info.Title = details.Item.Title;
                info.Year = details.Item.Year;
                info.Poster = details.Item.Poster;
                info.MediaType = mediaType;
                info.TmdbId = tmdbId;
                return info;
            })
            .ToList();
        details.CanRequest = canKind && (tv
            ? details.Seasons.Any(s => s.Number > 0 && !s.Requested && s.Status is "unknown" or "deleted")
            : details.Item.Status is "unknown" or "deleted" && !openRequests.Any(r => !r.Bool("is4k")));
        var (movie4k, tv4k) = await Has4kServersAsync(seerr, cancellationToken).ConfigureAwait(false);
        var can4k = (tv ? tv4k : movie4k)
            && (SeerrMapper.Has(p, SeerrAccess.Request4k) || SeerrMapper.Has(p, tv ? SeerrAccess.Request4kTv : SeerrAccess.Request4kMovie));
        details.CanRequest4k = can4k
            && details.Item.Status4k is "unknown" or "deleted"
            && !openRequests.Any(r => r.Bool("is4k"));
        return details;
    }

    /// <summary>Recommendations or similar titles.</summary>
    /// <param name="user">The viewer.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="kind">"recommendations" or "similar".</param>
    /// <param name="page">Page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The row, or <c>null</c>.</returns>
    public async Task<DiscoverPage?> RelatedAsync(User user, string mediaType, long tmdbId, string kind, int page, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null || mediaType is not ("movie" or "tv") || kind is not ("recommendations" or "similar"))
        {
            return null;
        }

        // These lists have no certification either.
        if (Parental.Limit(user) is not null)
        {
            return new DiscoverPage(1, 1, []);
        }

        using var doc = (await AsUserAsync(seerr, HttpMethod.Get, $"/api/v1/{mediaType}/{tmdbId}/{kind}?page={Math.Max(1, page)}", null, cancellationToken).ConfigureAwait(false)).Json();
        return doc is null ? new DiscoverPage(1, 1, []) : SeerrMapper.Page(doc.RootElement, _images, ItemLookup(user), mediaType, (r, _) => !r.Bool("adult"));
    }

    /// <summary>
    /// Servers, quality profiles and folders for an advanced request.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="is4k">For a 4K request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The choices, or <c>null</c> when the viewer may not choose.</returns>
    public async Task<IReadOnlyList<ServerOption>?> OptionsAsync(User user, string mediaType, bool is4k, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null || !(SeerrMapper.Has(seerr.Permissions, SeerrAccess.RequestAdvanced) || SeerrMapper.Has(seerr.Permissions, SeerrAccess.ManageRequests)))
        {
            return null;
        }

        var service = mediaType == "tv" ? "sonarr" : "radarr";
        using var list = (await AsUserAsync(seerr, HttpMethod.Get, "/api/v1/service/" + service, null, cancellationToken).ConfigureAwait(false)).Json();
        if (list is null)
        {
            return [];
        }

        var result = new List<ServerOption>();
        foreach (var s in list.RootElement.Arr().Where(s => s.Bool("is4k") == is4k))
        {
            var id = s.Long("id") ?? 0;
            using var one = (await AsUserAsync(seerr, HttpMethod.Get, $"/api/v1/service/{service}/{id}", null, cancellationToken).ConfigureAwait(false)).Json();
            var root = one?.RootElement ?? default;
            result.Add(new ServerOption(
                id,
                s.Str("name") ?? service,
                s.Bool("is4k"),
                s.Bool("isDefault"),
                root.Arr("profiles").Select(p => new ProfileOption(p.Long("id") ?? 0, p.Str("name") ?? string.Empty)).ToList(),
                root.Arr("rootFolders").Select(f => f.Str("path")).OfType<string>().ToList(),
                s.Long("activeProfileId"),
                s.Str("activeDirectory")));
        }

        return result;
    }

    /// <summary>
    /// Makes a request as the viewer.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="request">What to request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it worked, with a message for the viewer.</returns>
    public async Task<WriteResult> RequestAsync(User user, NewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null)
        {
            return new WriteResult(false, "You don't have a Seerr account yet. Ask the server admin to add you.");
        }

        if (request.MediaType is not ("movie" or "tv") || request.TmdbId <= 0)
        {
            return new WriteResult(false, "That title can't be requested.");
        }

        // The title page is the parental check: it refuses titles above the limit.
        if (await TitleAsync(user, request.MediaType, request.TmdbId, cancellationToken).ConfigureAwait(false) is null)
        {
            return new WriteResult(false, "That title can't be requested.");
        }

        var body = new Dictionary<string, object?>
        {
            ["mediaType"] = request.MediaType,
            ["mediaId"] = request.TmdbId,
            ["is4k"] = request.Is4k,
        };
        if (request.MediaType == "tv")
        {
            body["seasons"] = request.Seasons.Count == 0 ? "all" : request.Seasons.Where(s => s > 0).Distinct().ToArray();
        }

        if (request.ServerId is not null || request.ProfileId is not null || request.RootFolder is not null)
        {
            if (!(SeerrMapper.Has(seerr.Permissions, SeerrAccess.RequestAdvanced) || SeerrMapper.Has(seerr.Permissions, SeerrAccess.ManageRequests)))
            {
                return new WriteResult(false, "You can't choose the quality or folder for requests.");
            }

            body["serverId"] = request.ServerId;
            body["profileId"] = request.ProfileId;
            body["rootFolder"] = request.RootFolder;
        }

        var response = await AsUserAsync(seerr, HttpMethod.Post, "/api/v1/request", JsonSerializer.Serialize(body), cancellationToken).ConfigureAwait(false);
        return response.Status switch
        {
            201 => new WriteResult(true, "Requested."),
            202 => new WriteResult(false, "Every season is already requested or in the library."),
            409 => new WriteResult(false, "This has already been requested."),
            403 when Message(response).Contains("quota", StringComparison.OrdinalIgnoreCase) => new WriteResult(false, "You've reached your request limit for now."),
            403 when Message(response).Contains("blocklist", StringComparison.OrdinalIgnoreCase) => new WriteResult(false, "The server admin has blocked requests for this title."),
            401 or 403 => new WriteResult(false, "Seerr doesn't allow you to request this."),
            _ => Unavailable(response),
        };
    }

    /// <summary>
    /// The viewer's requests, or everyone's (for people who manage requests).
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="everyone">Everyone's requests (needs permission; otherwise just theirs).</param>
    /// <param name="filter">"all", "pending", "approved", "processing", "available", "failed".</param>
    /// <param name="page">Page (1-based).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page, or <c>null</c>.</returns>
    public async Task<RequestPage?> RequestsAsync(User user, bool everyone, string filter, int page, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null)
        {
            return null;
        }

        const int Take = 20;
        page = Math.Max(1, page);
        var all = everyone && (SeerrMapper.Has(seerr.Permissions, SeerrAccess.ManageRequests) || SeerrMapper.Has(seerr.Permissions, SeerrAccess.RequestView));
        var allowedFilter = filter is "pending" or "approved" or "processing" or "available" or "failed" or "unavailable" ? filter : "all";
        var path = $"/api/v1/request?take={Take}&skip={(page - 1) * Take}&sort=added&filter={allowedFilter}" + (all ? string.Empty : $"&requestedBy={seerr.Id}");
        using var doc = (await AsUserAsync(seerr, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false)).Json();
        if (doc is null)
        {
            return new RequestPage(page, 1, []);
        }

        var lookup = ItemLookup(user);
        var list = new List<RequestInfo>();
        foreach (var r in doc.RootElement.Arr("results"))
        {
            var info = SeerrMapper.Request(r, seerr.Id, seerr.Permissions);
            await FillTitleAsync(seerr, info.MediaType, info.TmdbId, (t, y, p) => { info.Title = t; info.Year = y; info.Poster = p; }, cancellationToken).ConfigureAwait(false);
            info.ItemId = lookup(info.MediaType, info.TmdbId, r.Get("media").Long("tvdbId"));
            list.Add(info);
        }

        var pages = doc.RootElement.Get("pageInfo").Int("pages") ?? 1;
        return new RequestPage(page, Math.Max(1, pages), list);
    }

    /// <summary>
    /// Approves, declines or withdraws a request.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="requestId">Seerr request id.</param>
    /// <param name="action">"approve", "decline" or "cancel".</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<WriteResult> RequestActionAsync(User user, long requestId, string action, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null || requestId <= 0)
        {
            return new WriteResult(false, "Requests aren't available.");
        }

        var response = action switch
        {
            "approve" or "decline" => await AsUserAsync(seerr, HttpMethod.Post, $"/api/v1/request/{requestId}/{action}", null, cancellationToken).ConfigureAwait(false),
            "cancel" => await AsUserAsync(seerr, HttpMethod.Delete, $"/api/v1/request/{requestId}", null, cancellationToken).ConfigureAwait(false),
            _ => new ServiceResponse(400, string.Empty),
        };
        return response.Status switch
        {
            >= 200 and < 300 => new WriteResult(true, action switch { "approve" => "Approved.", "decline" => "Declined.", _ => "Request withdrawn." }),
            401 or 403 => new WriteResult(false, "Seerr doesn't allow you to do that."),
            404 => new WriteResult(false, "That request no longer exists."),
            409 => new WriteResult(false, "Someone already dealt with this request."),
            400 => new WriteResult(false, "Unknown action."),
            _ => Unavailable(response),
        };
    }

    /// <summary>The viewer's request quotas.</summary>
    /// <param name="user">The viewer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The quotas, or <c>null</c>.</returns>
    public async Task<QuotaInfo?> QuotaAsync(User user, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null)
        {
            return null;
        }

        using var doc = (await AsUserAsync(seerr, HttpMethod.Get, $"/api/v1/user/{seerr.Id}/quota", null, cancellationToken).ConfigureAwait(false)).Json();
        return doc is null ? null : SeerrMapper.Quota(doc.RootElement);
    }

    /// <summary>Problem reports: the viewer's own, or everyone's for people who may see them.</summary>
    /// <param name="user">The viewer.</param>
    /// <param name="filter">"open", "resolved" or "all".</param>
    /// <param name="page">Page (1-based).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page, or <c>null</c>.</returns>
    public async Task<IssuePage?> IssuesAsync(User user, string filter, int page, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null)
        {
            return null;
        }

        const int Take = 20;
        page = Math.Max(1, page);
        var f = filter is "open" or "resolved" ? filter : "all";
        // No createdBy filter: Seerr's request validator rejects it (its API spec
        // doesn't list it, seerr-api.yml /issue), and Seerr already limits people
        // without View issues to their own reports.
        var path = $"/api/v1/issue?take={Take}&skip={(page - 1) * Take}&sort=modified&filter={f}";
        using var doc = (await AsUserAsync(seerr, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false)).Json();
        if (doc is null)
        {
            return new IssuePage(page, 1, []);
        }

        var lookup = ItemLookup(user);
        var list = new List<IssueInfo>();
        foreach (var i in doc.RootElement.Arr("results"))
        {
            var info = SeerrMapper.Issue(i, seerr.Permissions, seerr.Id);
            await FillTitleAsync(seerr, info.MediaType, info.TmdbId, (t, _, p) => { info.Title = t; info.Poster = p; }, cancellationToken).ConfigureAwait(false);
            info.ItemId = lookup(info.MediaType, info.TmdbId, i.Get("media").Long("tvdbId"));
            list.Add(info);
        }

        return new IssuePage(page, Math.Max(1, doc.RootElement.Get("pageInfo").Int("pages") ?? 1), list);
    }

    /// <summary>
    /// One problem report with its whole conversation (Seerr's list leaves out
    /// who wrote each comment; the single-issue reply has them).
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="issueId">Seerr issue id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report, or <c>null</c> (not found, or not theirs to see).</returns>
    public async Task<IssueInfo?> IssueAsync(User user, long issueId, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null)
        {
            return null;
        }

        using var doc = (await AsUserAsync(seerr, HttpMethod.Get, $"/api/v1/issue/{issueId}", null, cancellationToken).ConfigureAwait(false)).Json();
        if (doc is null)
        {
            return null;
        }

        var info = SeerrMapper.Issue(doc.RootElement, seerr.Permissions, seerr.Id);
        await FillTitleAsync(seerr, info.MediaType, info.TmdbId, (t, _, p) => { info.Title = t; info.Poster = p; }, cancellationToken).ConfigureAwait(false);
        info.ItemId = ItemLookup(user)(info.MediaType, info.TmdbId, doc.RootElement.Get("media").Long("tvdbId"));
        return info;
    }

    /// <summary>
    /// Reports a problem with a title.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="issue">The report.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<WriteResult> CreateIssueAsync(User user, string mediaType, long tmdbId, NewIssue issue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null)
        {
            return new WriteResult(false, "You don't have a Seerr account yet. Ask the server admin to add you.");
        }

        var message = issue.Message.Trim();
        if (message.Length is 0 or > 1000)
        {
            return new WriteResult(false, "Describe the problem in up to 1000 characters.");
        }

        // Seerr files issues against its own media id, which only exists once Seerr has seen the title.
        using var details = (await AsUserAsync(seerr, HttpMethod.Get, $"/api/v1/{mediaType}/{tmdbId}", null, cancellationToken).ConfigureAwait(false)).Json();
        var mediaId = details?.RootElement.Get("mediaInfo").Long("id");
        if (mediaId is null)
        {
            return new WriteResult(false, "Seerr doesn't know this title yet. It appears after Seerr's next library scan.");
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["issueType"] = SeerrMapper.IssueTypeNumber(issue.Type),
            ["message"] = message,
            ["mediaId"] = mediaId.Value,
            ["problemSeason"] = Math.Max(0, issue.Season),
            ["problemEpisode"] = Math.Max(0, issue.Episode),
        });
        var response = await AsUserAsync(seerr, HttpMethod.Post, "/api/v1/issue", body, cancellationToken).ConfigureAwait(false);
        return response.Status switch
        {
            >= 200 and < 300 => new WriteResult(true, "Thanks. The problem was reported."),
            401 or 403 => new WriteResult(false, "Seerr doesn't allow you to report problems."),
            _ => Unavailable(response),
        };
    }

    /// <summary>Adds a comment to a problem report.</summary>
    /// <param name="user">The viewer.</param>
    /// <param name="issueId">Seerr issue id.</param>
    /// <param name="message">Comment text.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<WriteResult> CommentAsync(User user, long issueId, string message, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        message = (message ?? string.Empty).Trim();
        if (seerr is null || message.Length is 0 or > 1000)
        {
            return new WriteResult(false, "Write a comment of up to 1000 characters.");
        }

        var response = await AsUserAsync(seerr, HttpMethod.Post, $"/api/v1/issue/{issueId}/comment", JsonSerializer.Serialize(new { message }), cancellationToken).ConfigureAwait(false);
        return response.Ok ? new WriteResult(true, "Comment added.") : response.Status is 401 or 403 ? new WriteResult(false, "Seerr doesn't allow you to comment here.") : Unavailable(response);
    }

    /// <summary>Marks a problem report resolved, or opens it again.</summary>
    /// <param name="user">The viewer.</param>
    /// <param name="issueId">Seerr issue id.</param>
    /// <param name="status">"resolved" or "open".</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<WriteResult> IssueStatusAsync(User user, long issueId, string status, CancellationToken cancellationToken)
    {
        var seerr = await UserForAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (seerr is null || status is not ("resolved" or "open"))
        {
            return new WriteResult(false, "Unknown action.");
        }

        var response = await AsUserAsync(seerr, HttpMethod.Post, $"/api/v1/issue/{issueId}/{status}", null, cancellationToken).ConfigureAwait(false);
        return response.Ok
            ? new WriteResult(true, status == "resolved" ? "Marked as fixed." : "Opened again.")
            : response.Status is 401 or 403 ? new WriteResult(false, "Seerr doesn't allow you to change this report.") : Unavailable(response);
    }

    /// <summary>
    /// The viewer's Seerr watchlist as (type, TMDB id, title).
    /// </summary>
    /// <param name="seerr">The viewer's Seerr account.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The entries, or <c>null</c> when Seerr couldn't be read (so a sync doesn't treat it as empty).</returns>
    public async Task<IReadOnlyList<(string Type, long TmdbId, string Title)>?> WatchlistAsync(SeerrUser seerr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seerr);
        var list = new List<(string, long, string)>();
        for (var page = 1; page <= 10; page++)
        {
            using var doc = (await AsUserAsync(seerr, HttpMethod.Get, $"/api/v1/discover/watchlist?page={page}", null, cancellationToken).ConfigureAwait(false)).Json();
            if (doc is null)
            {
                return null;
            }

            foreach (var w in doc.RootElement.Arr("results"))
            {
                var type = w.Str("mediaType");
                var tmdb = w.Long("tmdbId") ?? w.Long("id") ?? 0;
                if (type is "movie" or "tv" && tmdb > 0)
                {
                    list.Add((type, tmdb, w.Str("title") ?? string.Empty));
                }
            }

            if (page >= (doc.RootElement.Int("totalPages") ?? 1))
            {
                break;
            }
        }

        return list;
    }

    /// <summary>Adds a title to the viewer's Seerr watchlist.</summary>
    /// <param name="seerr">Their Seerr account.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="title">Title.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><c>true</c> when Seerr has it (a duplicate counts).</returns>
    public async Task<bool> AddToWatchlistAsync(SeerrUser seerr, string mediaType, long tmdbId, string title, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { tmdbId, mediaType, title });
        var r = await AsUserAsync(seerr, HttpMethod.Post, "/api/v1/watchlist", body, cancellationToken).ConfigureAwait(false);
        return r.Ok || r.Status == 409;
    }

    /// <summary>Removes a title from the viewer's Seerr watchlist.</summary>
    /// <param name="seerr">Their Seerr account.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><c>true</c> when it's gone (already gone counts).</returns>
    public async Task<bool> RemoveFromWatchlistAsync(SeerrUser seerr, string mediaType, long tmdbId, CancellationToken cancellationToken)
    {
        var r = await AsUserAsync(seerr, HttpMethod.Delete, $"/api/v1/watchlist/{tmdbId}?mediaType={mediaType}", null, cancellationToken).ConfigureAwait(false);
        return r.Ok || r.Status == 404;
    }

    /// <summary>
    /// Every Jellyfin user next to their Seerr account, with warnings.
    /// </summary>
    /// <param name="jellyfinUsers">All Jellyfin users.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One row per Jellyfin user, or <c>null</c> when Seerr can't be read.</returns>
    public async Task<IReadOnlyList<SeerrUserAudit>?> AuditAsync(IEnumerable<User> jellyfinUsers, CancellationToken cancellationToken)
    {
        if (!Configured)
        {
            return null;
        }

        var seerrUsers = await UsersAsync(true, cancellationToken).ConfigureAwait(false);
        return jellyfinUsers
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u =>
            {
                var s = seerrUsers.FirstOrDefault(x => x.JellyfinUserId == u.Id.ToString("N"));
                var admin = u.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator);
                var disabled = u.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled);
                var limited = Parental.Limit(u) is not null;
                return new SeerrUserAudit
                {
                    UserId = u.Id.ToString("N"),
                    Name = u.Username,
                    JellyfinAdmin = admin,
                    Disabled = disabled,
                    ParentalLimit = limited,
                    Linked = s is not null,
                    SeerrName = s?.DisplayName,
                    Requests = s?.RequestCount ?? 0,
                    Permissions = s is null ? [] : SeerrMapper.PermissionLabels(s.Permissions),
                    Warnings = SeerrMapper.AuditWarnings(admin, disabled, limited, s?.Permissions),
                };
            })
            .ToList();
    }

    /// <summary>
    /// Creates Seerr accounts for Jellyfin users (Seerr's own "Import Jellyfin
    /// users"; Seerr links each one to the Jellyfin account and gives it Seerr's
    /// default permissions).
    /// </summary>
    /// <param name="jellyfinUserIds">Jellyfin user ids.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome with how many were added.</returns>
    public async Task<WriteResult> ImportAsync(IReadOnlyList<Guid> jellyfinUserIds, CancellationToken cancellationToken)
    {
        if (!Configured)
        {
            return new WriteResult(false, "Seerr isn't set up.");
        }

        var ids = jellyfinUserIds.Select(i => i.ToString("N")).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new WriteResult(false, "Choose at least one person.");
        }

        // As Seerr's admin (the API key's own account): importing needs Manage users.
        var response = await _client.SendAsync(IntegrationKind.Seerr, HttpMethod.Post, "/api/v1/user/import-from-jellyfin", JsonSerializer.Serialize(new { jellyfinUserIds = ids }), cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
        {
            return Unavailable(response);
        }

        await UsersAsync(true, cancellationToken).ConfigureAwait(false);
        using var doc = response.Json();
        var added = doc?.RootElement.Arr().Count() ?? 0;
        return new WriteResult(true, added == 1 ? "Added 1 person to Seerr." : $"Added {added} people to Seerr.");
    }

    /// <summary>
    /// Raw details JSON for a title, from a six-hour cache (titles in request and
    /// issue lists; Seerr's lists only carry ids). Not for status: that's always fetched.
    /// </summary>
    /// <param name="seerr">Account to ask as.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The JSON text, or <c>null</c>.</returns>
    public async Task<string?> CachedDetailsAsync(SeerrUser? seerr, string mediaType, long tmdbId, CancellationToken cancellationToken)
    {
        var key = mediaType + ":" + tmdbId.ToString(CultureInfo.InvariantCulture);
        if (_details.TryGetValue(key, out var hit) && _clock() - hit.At < DetailsFor)
        {
            return hit.Body;
        }

        var path = $"/api/v1/{mediaType}/{tmdbId}";
        var response = seerr is null
            ? await _client.SendAsync(IntegrationKind.Seerr, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false)
            : await AsUserAsync(seerr, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
        {
            return null;
        }

        CacheDetails(mediaType, tmdbId, response.Body);
        return response.Body;
    }

    /// <summary>
    /// Calls Seerr as the given account.
    /// </summary>
    /// <param name="seerr">The account.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Path and query.</param>
    /// <param name="body">JSON body.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Seerr's answer.</returns>
    public Task<ServiceResponse> AsUserAsync(SeerrUser seerr, HttpMethod method, string path, string? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seerr);
        var headers = new Dictionary<string, string> { ["X-Api-User"] = seerr.Id.ToString(CultureInfo.InvariantCulture) };
        return _client.SendAsync(IntegrationKind.Seerr, method, path, body, cancellationToken, headers);
    }

    /// <summary>
    /// Splits a trailing year off a search ("Dune 1984", "Dune (1984)").
    /// </summary>
    /// <param name="query">Search text.</param>
    /// <returns>The text without the year, and the year if there was one.</returns>
    public static (string Text, int? Year) SplitYear(string query)
    {
        var m = YearSuffix().Match(query);
        if (m.Success && m.Groups[1].Value.Trim().Length > 0)
        {
            return (m.Groups[1].Value.Trim(), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        return (query, null);
    }

    /// <summary>
    /// A lookup from (type, TMDB id, TVDB id) to the library item id the viewer can see.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <returns>The lookup.</returns>
    public Func<string, long, long?, string?> ItemLookup(User? user) =>
        (type, tmdb, tvdb) => _index.FindVisible(user, type, tmdb, tvdb)?.Id.ToString("N");

    private static string Message(ServiceResponse response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            return doc.RootElement.Str("message") ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static WriteResult Unavailable(ServiceResponse response) => response.Status switch
    {
        ServiceResponse.NotConfigured => new WriteResult(false, "Seerr isn't set up on this server."),
        ServiceResponse.Unreachable or ServiceResponse.TimedOut => new WriteResult(false, "Seerr isn't answering right now. Try again in a moment."),
        _ => new WriteResult(false, $"Seerr couldn't do that ({response.Status})."),
    };

    [GeneratedRegex(@"^(.*?)[\s]*\(?((?:19|20)\d{2})\)?$")]
    private static partial Regex YearSuffix();

    private void CacheDetails(string mediaType, long tmdbId, string body)
    {
        if (_details.Count >= MaxDetails)
        {
            _details.Clear();
        }

        _details[mediaType + ":" + tmdbId.ToString(CultureInfo.InvariantCulture)] = (_clock(), body);
    }

    private async Task FillTitleAsync(SeerrUser seerr, string mediaType, long tmdbId, Action<string, int?, string?> set, CancellationToken cancellationToken)
    {
        var body = await CachedDetailsAsync(seerr, mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        var title = mediaType == "tv" ? r.Str("name") : r.Str("title");
        var date = mediaType == "tv" ? r.Str("firstAirDate") : r.Str("releaseDate");
        set(title ?? string.Empty, Json.Year(date), _images.Tmdb(r.Str("posterPath"), "w342"));
    }

    private async Task<(bool Movie, bool Tv)> Has4kServersAsync(SeerrUser seerr, CancellationToken cancellationToken)
    {
        async Task<bool> Any4k(string service)
        {
            if (!_services.TryGetValue(service, out var hit) || _clock() - hit.At > ServicesFor)
            {
                var r = await AsUserAsync(seerr, HttpMethod.Get, "/api/v1/service/" + service, null, cancellationToken).ConfigureAwait(false);
                hit = (_clock(), r.Ok ? r.Body : "[]");
                _services[service] = hit;
            }

            try
            {
                using var doc = JsonDocument.Parse(hit.Body);
                return doc.RootElement.Arr().Any(s => s.Bool("is4k"));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return (await Any4k("radarr").ConfigureAwait(false), await Any4k("sonarr").ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<SeerrUser>> UsersAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _clock() - _usersAt < UsersFor)
        {
            return _users;
        }

        await _usersGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _clock() - _usersAt < UsersFor)
            {
                return _users;
            }

            var list = new List<SeerrUser>();
            const int Take = 100;
            for (var page = 0; page < 50; page++)
            {
                var r = await _client.SendAsync(IntegrationKind.Seerr, HttpMethod.Get, $"/api/v1/user?take={Take}&skip={page * Take}", null, cancellationToken).ConfigureAwait(false);
                using var doc = r.Json();
                if (doc is null)
                {
                    // Keep what we had rather than "nobody is linked" during a Seerr hiccup.
                    _usersAt = _clock();
                    return _users;
                }

                foreach (var u in doc.RootElement.Arr("results"))
                {
                    var jf = (u.Str("jellyfinUserId") ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                    if (jf.Length == 32)
                    {
                        list.Add(new SeerrUser(
                            u.Long("id") ?? 0,
                            u.Long("permissions") ?? 0,
                            u.Str("displayName") ?? u.Str("jellyfinUsername") ?? u.Str("username") ?? string.Empty,
                            jf,
                            u.Int("requestCount") ?? 0));
                    }
                }

                if (page + 1 >= (doc.RootElement.Get("pageInfo").Int("pages") ?? 1))
                {
                    break;
                }
            }

            _users = list;
            _usersAt = _clock();
            return list;
        }
        finally
        {
            _usersGate.Release();
        }
    }
}
