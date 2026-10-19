// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LumaraCompanion.Configuration;
using Jellyfin.Plugin.LumaraCompanion.Integrations;
using Jellyfin.Plugin.LumaraCompanion.Integrations.Arr;
using Jellyfin.Plugin.LumaraCompanion.Integrations.Seerr;
using Jellyfin.Plugin.LumaraCompanion.UserData;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LumaraCompanion.Api;

/// <summary>
/// Requests, discovery, the watchlist, the calendar, downloads and the *arr
/// tools, under <c>/Lumara</c>.
/// </summary>
/// <remarks>
/// Every endpoint works for the signed-in Jellyfin user. Seerr calls run as that
/// person's own Seerr account; *arr management needs a Jellyfin admin. Responses
/// are the plugin's own shapes: never raw service replies, service addresses or
/// keys. A missing integration gives 404 with an empty body, which Lumara apps
/// read as "feature not available" rather than an error.
/// </remarks>
[ApiController]
[Route("Lumara")]
[Authorize]
public class LumaraRequestsController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly SeerrGateway _seerr;
    private readonly Watchlist _watchlist;
    private readonly CalendarService _calendar;
    private readonly DownloadsService _downloads;
    private readonly ArrTools _arr;
    private readonly TagSync _tags;
    private readonly UserJsonStore<RequestPrefs> _prefs;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly IntegrationService _integrations;

    /// <summary>
    /// Initializes a new instance of the <see cref="LumaraRequestsController"/> class.
    /// </summary>
    /// <param name="seerr">Seerr gateway.</param>
    /// <param name="watchlist">Watchlist.</param>
    /// <param name="calendar">Calendar.</param>
    /// <param name="downloads">Downloads.</param>
    /// <param name="arr">Admin *arr tools.</param>
    /// <param name="tags">Tag copier.</param>
    /// <param name="prefs">Per-person request preferences.</param>
    /// <param name="users">Jellyfin users.</param>
    /// <param name="library">Jellyfin library.</param>
    /// <param name="integrations">Integration settings (which are set up).</param>
    public LumaraRequestsController(
        SeerrGateway seerr,
        Watchlist watchlist,
        CalendarService calendar,
        DownloadsService downloads,
        ArrTools arr,
        TagSync tags,
        UserJsonStore<RequestPrefs> prefs,
        IUserManager users,
        ILibraryManager library,
        IntegrationService integrations)
    {
        _seerr = seerr;
        _watchlist = watchlist;
        _calendar = calendar;
        _downloads = downloads;
        _arr = arr;
        _tags = tags;
        _prefs = prefs;
        _users = users;
        _library = library;
        _integrations = integrations;
    }

    private static RequestSettings Settings => Plugin.Instance?.Configuration.Requests ?? new RequestSettings();

    private bool IsAdmin => User.IsInRole("Administrator");

    /// <summary>What the signed-in person can use: requests, calendar, downloads, admin tools.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The features.</returns>
    [HttpGet("Requests/Features")]
    public async Task<ActionResult<RequestFeatures>> Features(CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        var s = Settings;
        var f = new RequestFeatures
        {
            Admin = IsAdmin,
            Calendar = _calendar.Available && (s.CalendarForEveryone || IsAdmin),
            Bazarr = IsAdmin && _integrations.IsConfigured(IntegrationKind.Bazarr),
            ParentalLimit = Parental.Limit(user) is not null,
            AutoRequestSeasons = s.AutoRequestNextSeason && _seerr.Configured,
            AutoRequestMovies = s.AutoRequestNextMovie && _seerr.Configured,
            AutoRequestOptOut = !_prefs.Read(user.Id).AutoRequest,
        };
        await _seerr.FillFeaturesAsync(f, user, cancellationToken).ConfigureAwait(false);
        f.Downloads = _downloads.Available && (IsAdmin || f.Linked);
        return f;
    }

    /// <summary>Saves the signed-in person's request preferences.</summary>
    /// <param name="prefs">The preferences.</param>
    /// <returns>204.</returns>
    [HttpPut("Requests/Preferences")]
    public IActionResult PutPreferences([FromBody] RequestPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        _prefs.Update(user.Id, p =>
        {
            p.AutoRequest = prefs.AutoRequest;
            return true;
        });
        return NoContent();
    }

    /// <summary>Searches for films and shows to request.</summary>
    /// <param name="query">Search text (a trailing year narrows it).</param>
    /// <param name="page">Page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Results, or 404 without Seerr.</returns>
    [HttpGet("Requests/Search")]
    public async Task<ActionResult<DiscoverPage>> Search([FromQuery] string query, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
        {
            return BadRequest();
        }

        return Found(await _seerr.SearchAsync(user, query, page, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>One discovery row.</summary>
    /// <param name="slider">Row name.</param>
    /// <param name="id">Genre, network or studio id.</param>
    /// <param name="page">Page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The row.</returns>
    [HttpGet("Requests/Discover/{slider}")]
    public async Task<ActionResult<DiscoverPage>> Discover([FromRoute] string slider, [FromQuery] long? id, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return Found(await _seerr.DiscoverAsync(user, slider, id, Math.Clamp(page, 1, 500), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Genres to browse.</summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The genres.</returns>
    [HttpGet("Requests/Genres/{mediaType}")]
    public async Task<ActionResult<IReadOnlyList<BrowseOption>>> Genres([FromRoute] string mediaType, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        var genres = await _seerr.GenresAsync(user, mediaType, cancellationToken).ConfigureAwait(false);
        return genres is null ? NotFound() : Ok(genres);
    }

    /// <summary>A title page from Seerr.</summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The details.</returns>
    [HttpGet("Requests/Title/{mediaType}/{tmdbId}")]
    public async Task<ActionResult<TitleDetails>> Title([FromRoute] string mediaType, [FromRoute] long tmdbId, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        var details = await _seerr.TitleAsync(user, mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
        if (details is null)
        {
            return NotFound();
        }

        details.OnWatchlist = _watchlist.Contains(user.Id, mediaType, tmdbId);
        return details;
    }

    /// <summary>The Seerr page for a library title (for "Request more" and problem reports on its details page).</summary>
    /// <param name="itemId">Jellyfin item id (film, show, season or episode).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The details, or 404.</returns>
    [HttpGet("Requests/ForItem/{itemId}")]
    public async Task<ActionResult<TitleDetails>> ForItem([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user || VisibleItem(user, itemId) is not { } item || Watchlist.Identify(item) is not { } id)
        {
            return NotFound();
        }

        var details = await _seerr.TitleAsync(user, id.Type, id.Tmdb, cancellationToken).ConfigureAwait(false);
        if (details is null)
        {
            return NotFound();
        }

        details.OnWatchlist = _watchlist.Contains(user.Id, id.Type, id.Tmdb);
        return details;
    }

    /// <summary>Recommendations or similar titles.</summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="kind">"recommendations" or "similar".</param>
    /// <param name="page">Page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The row.</returns>
    [HttpGet("Requests/Title/{mediaType}/{tmdbId}/{kind}")]
    public async Task<ActionResult<DiscoverPage>> Related([FromRoute] string mediaType, [FromRoute] long tmdbId, [FromRoute] string kind, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return Found(await _seerr.RelatedAsync(user, mediaType, tmdbId, kind, page, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Servers, quality profiles and folders for an advanced request.</summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="is4k">For a 4K request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The choices, or 403 when the person can't choose.</returns>
    [HttpGet("Requests/Options/{mediaType}")]
    public async Task<ActionResult<IReadOnlyList<ServerOption>>> Options([FromRoute] string mediaType, [FromQuery] bool is4k = false, CancellationToken cancellationToken = default)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        var options = await _seerr.OptionsAsync(user, mediaType, is4k, cancellationToken).ConfigureAwait(false);
        return options is null ? Forbid() : Ok(options);
    }

    /// <summary>Makes a request.</summary>
    /// <param name="request">What to request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome (200 either way; <c>Ok</c> says whether it worked).</returns>
    [HttpPost("Requests")]
    public async Task<ActionResult<WriteResult>> CreateRequest([FromBody] NewRequest request, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return await _seerr.RequestAsync(user, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The person's requests, or everyone's.</summary>
    /// <param name="scope">"mine" or "all".</param>
    /// <param name="filter">Status filter.</param>
    /// <param name="page">Page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page.</returns>
    [HttpGet("Requests")]
    public async Task<ActionResult<RequestPage>> Requests([FromQuery] string scope = "mine", [FromQuery] string filter = "all", [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return Found(await _seerr.RequestsAsync(user, scope == "all", filter, page, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Approves, declines or withdraws a request.</summary>
    /// <param name="requestId">Seerr request id.</param>
    /// <param name="verb">"approve", "decline" or "cancel". (Not named "action": that's a reserved MVC route value.)</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Requests/{requestId:long}/{verb}")]
    public async Task<ActionResult<WriteResult>> RequestAction([FromRoute] long requestId, [FromRoute] string verb, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return await _seerr.RequestActionAsync(user, requestId, verb, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The person's request quotas.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The quotas.</returns>
    [HttpGet("Requests/Quota")]
    public async Task<ActionResult<QuotaInfo>> Quota(CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return Found(await _seerr.QuotaAsync(user, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Problem reports.</summary>
    /// <param name="filter">"open", "resolved" or "all".</param>
    /// <param name="page">Page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page.</returns>
    [HttpGet("Requests/Issues")]
    public async Task<ActionResult<IssuePage>> Issues([FromQuery] string filter = "open", [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return Found(await _seerr.IssuesAsync(user, filter, page, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>One problem report with its comments.</summary>
    /// <param name="issueId">Seerr issue id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report.</returns>
    [HttpGet("Requests/Issues/{issueId:long}")]
    public async Task<ActionResult<IssueInfo>> Issue([FromRoute] long issueId, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return Found(await _seerr.IssueAsync(user, issueId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reports a problem with a library title or a Seerr title.</summary>
    /// <param name="issue">The report.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Requests/Issues")]
    public async Task<ActionResult<WriteResult>> CreateIssue([FromBody] NewIssue issue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        string? type = issue.MediaType;
        long tmdb = issue.TmdbId ?? 0;
        if (!string.IsNullOrEmpty(issue.ItemId))
        {
            if (!Guid.TryParse(issue.ItemId, out var itemId) || VisibleItem(user, itemId) is not { } item || Watchlist.Identify(item) is not { } id)
            {
                return new WriteResult(false, "This title can't be reported through Seerr (it has no TMDB id).");
            }

            (type, tmdb) = (id.Type, id.Tmdb);

            // An episode report carries its own season and episode.
            if (item is MediaBrowser.Controller.Entities.TV.Episode ep && issue.Season == 0)
            {
                issue.Season = ep.ParentIndexNumber ?? 0;
                issue.Episode = ep.IndexNumber ?? 0;
            }
            else if (item is MediaBrowser.Controller.Entities.TV.Season season && issue.Season == 0)
            {
                issue.Season = season.IndexNumber ?? 0;
            }
        }

        if (type is not ("movie" or "tv") || tmdb <= 0)
        {
            return BadRequest();
        }

        return await _seerr.CreateIssueAsync(user, type, tmdb, issue, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Comments on a problem report.</summary>
    /// <param name="issueId">Seerr issue id.</param>
    /// <param name="body">{ Message }.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Requests/Issues/{issueId:long}/Comment")]
    public async Task<ActionResult<WriteResult>> Comment([FromRoute] long issueId, [FromBody] CommentBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return await _seerr.CommentAsync(user, issueId, body.Message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves or reopens a problem report.</summary>
    /// <param name="issueId">Seerr issue id.</param>
    /// <param name="status">"resolved" or "open".</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Requests/Issues/{issueId:long}/{status}")]
    public async Task<ActionResult<WriteResult>> IssueStatus([FromRoute] long issueId, [FromRoute] string status, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return await _seerr.IssueStatusAsync(user, issueId, status, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every Jellyfin user next to their Seerr account (admins).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The audit.</returns>
    [HttpGet("Requests/Users")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<IReadOnlyList<SeerrUserAudit>>> SeerrUsers(CancellationToken cancellationToken)
    {
        // The user-list API differs between Jellyfin releases; see JellyfinCompat.
        var all = JellyfinCompat.AllUsers(_users);
        return Found(await _seerr.AuditAsync(all, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Creates Seerr accounts for Jellyfin users (admins).</summary>
    /// <param name="body">{ UserIds }.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Requests/Users/Import")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<WriteResult>> ImportUsers([FromBody] ImportBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var ids = body.UserIds.Select(i => Guid.TryParse(i, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty && _users.GetUserById(g) is not null).ToList();
        return await _seerr.ImportAsync(ids, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The request, calendar and tag options (admins).</summary>
    /// <returns>The options.</returns>
    [HttpGet("Requests/Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<RequestSettings> GetRequestSettings() => Plugin.Instance is null ? StatusCode(StatusCodes.Status503ServiceUnavailable) : Ok(Settings);

    /// <summary>Saves the request, calendar and tag options (admins).</summary>
    /// <param name="settings">New options.</param>
    /// <returns>204, or 400 for an unusable tag prefix.</returns>
    [HttpPut("Requests/Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult PutRequestSettings([FromBody] RequestSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var prefix = settings.TagPrefix ?? string.Empty;
        if (prefix.Trim().Length is 0 or > 20)
        {
            return BadRequest("The tag prefix must be 1 to 20 characters.");
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        plugin.Configuration.Requests = settings;
        plugin.SaveConfiguration();
        return NoContent();
    }

    /// <summary>The person's watchlist (synced with Seerr).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Titles, newest first.</returns>
    [HttpGet("Watchlist")]
    public async Task<ActionResult<IReadOnlyList<WatchlistItem>>> GetWatchlist(CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return Ok(await _watchlist.GetAsync(user, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Adds a title to the watchlist: a library item, or a TMDB title.</summary>
    /// <param name="body">{ ItemId } or { MediaType, TmdbId, Title }.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Watchlist")]
    public async Task<ActionResult<WriteResult>> AddToWatchlist([FromBody] WatchlistBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        if (!string.IsNullOrEmpty(body.ItemId))
        {
            if (!Guid.TryParse(body.ItemId, out var itemId) || VisibleItem(user, itemId) is not { } item || Watchlist.Identify(item) is not { } id)
            {
                return new WriteResult(false, "This title can't go on the watchlist (it has no TMDB id).");
            }

            return await _watchlist.AddAsync(user, id.Type, id.Tmdb, id.Title, cancellationToken).ConfigureAwait(false);
        }

        return await _watchlist.AddAsync(user, body.MediaType ?? string.Empty, body.TmdbId ?? 0, body.Title ?? string.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a title from the watchlist.</summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    [HttpDelete("Watchlist/{mediaType}/{tmdbId}")]
    public async Task<ActionResult<WriteResult>> RemoveFromWatchlist([FromRoute] string mediaType, [FromRoute] long tmdbId, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        return await _watchlist.RemoveAsync(user, mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Upcoming episodes, films and albums.</summary>
    /// <param name="start">First day (yyyy-MM-dd).</param>
    /// <param name="end">Day after the last (yyyy-MM-dd), at most 62 days later.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Entries.</returns>
    [HttpGet("Calendar")]
    public async Task<ActionResult<IReadOnlyList<CalendarEntry>>> Calendar([FromQuery] string start, [FromQuery] string end, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        if (!_calendar.Available || !(Settings.CalendarForEveryone || IsAdmin))
        {
            return NotFound();
        }

        if (!DateTime.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var from)
            || !DateTime.TryParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var to))
        {
            return BadRequest("Dates must look like 2026-10-20.");
        }

        return Ok(await _calendar.GetAsync(user, from, to, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>What's downloading: everything for admins, the person's own requests otherwise.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The list.</returns>
    [HttpGet("Downloads")]
    public async Task<ActionResult<DownloadsResult>> Downloads(CancellationToken cancellationToken)
    {
        if (Viewer() is not { } user)
        {
            return Unauthorized();
        }

        if (!_downloads.Available)
        {
            return NotFound();
        }

        if (IsAdmin)
        {
            return await _downloads.GetAsync(user, true, null, cancellationToken).ConfigureAwait(false);
        }

        // Everyone else: downloads of titles they asked for in the last 60 requests.
        var movies = new HashSet<long>();
        var shows = new HashSet<long>();
        for (var page = 1; page <= 3; page++)
        {
            var requests = await _seerr.RequestsAsync(user, false, "all", page, cancellationToken).ConfigureAwait(false);
            if (requests is null)
            {
                return NotFound();
            }

            foreach (var r in requests.Results)
            {
                (r.MediaType == "tv" ? shows : movies).Add(r.TmdbId);
            }

            if (page >= requests.TotalPages)
            {
                break;
            }
        }

        return await _downloads.GetAsync(user, false, (movies, shows, new HashSet<long>()), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What Radarr, Sonarr and Bazarr know about a library title (admins).</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The info, or 404 when no app has it.</returns>
    [HttpGet("Arr/Item/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<ArrItemInfo>> ArrItem([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        return Found(await _arr.ItemAsync(item, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Starts a release search (Radarr/Sonarr) or a subtitle search (Bazarr) for a title (admins).</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="what">"Search" or "Subtitles".</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Arr/Item/{itemId}/{what}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<WriteResult>> ArrSearch([FromRoute] Guid itemId, [FromRoute] string what, CancellationToken cancellationToken)
    {
        var item = _library.GetItemById(itemId);
        if (item is null || what is not ("Search" or "Subtitles"))
        {
            return NotFound();
        }

        return await _arr.SearchAsync(item, what == "Subtitles", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Health of the *arr apps and Prowlarr's indexers (admins).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One entry per configured app.</returns>
    [HttpGet("Arr/Health")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<IReadOnlyList<ServiceHealth>>> ArrHealth(CancellationToken cancellationToken)
    {
        return Ok(await _arr.HealthAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Jellystat's most-watched lists (admins).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The numbers, or 404 without Jellystat.</returns>
    [HttpGet("Arr/Stats")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<WatchStats>> Stats(CancellationToken cancellationToken)
    {
        return Found(await _arr.StatsAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Copies the *arr tags now (admins).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What changed.</returns>
    [HttpPost("Arr/TagSync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<TagSyncResult>> RunTagSync(CancellationToken cancellationToken)
    {
        return await _tags.RunAsync(null, cancellationToken).ConfigureAwait(false);
    }

    private ActionResult Found<T>(T? value)
        where T : class
        => value is null ? NotFound() : Ok(value);

    private User? Viewer()
    {
        var value = User.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out var id) && id != Guid.Empty ? _users.GetUserById(id) : null;
    }

    private MediaBrowser.Controller.Entities.BaseItem? VisibleItem(User user, Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        return item is not null && item.IsVisible(user) ? item : null;
    }
}

/// <summary>Body of a comment.</summary>
public sealed class CommentBody
{
    /// <summary>Gets or sets the comment.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Body of a Seerr user import.</summary>
public sealed class ImportBody
{
    /// <summary>Gets or sets the Jellyfin user ids.</summary>
    public IReadOnlyList<string> UserIds { get; set; } = [];
}

/// <summary>Body of a watchlist addition.</summary>
public sealed class WatchlistBody
{
    /// <summary>Gets or sets a library item id (films, shows, seasons, episodes).</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets "movie" or "tv" (titles not in the library).</summary>
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public long? TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }
}
