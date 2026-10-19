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
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LumaraCompanion.Configuration;
using Jellyfin.Plugin.LumaraCompanion.UserData;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations.Seerr;

/// <summary>A person's own request preferences.</summary>
public sealed class RequestPrefs
{
    /// <summary>
    /// Gets or sets a value indicating whether the server may request the next
    /// season or film for them (when the admin has auto-requests on).
    /// </summary>
    public bool AutoRequest { get; set; } = true;
}

/// <summary>
/// Requests what comes next when someone finishes something: the next season
/// after the last episode of a show's newest season, or the next film in a
/// collection. The request is made as that person (their Seerr permissions and
/// quota apply), only when the admin switched it on and the person hasn't
/// switched it off.
/// </summary>
/// <remarks>
/// Listens to Jellyfin's <see cref="IUserDataManager.UserDataSaved"/> for a title
/// becoming played at the end of playback (or marked played). The work runs off
/// the event thread, and each (person, title) is tried at most once every ten minutes.
/// </remarks>
public sealed class AutoRequestService : IHostedService
{
    // Jellyfin saves user data several times as playback ends; this stops one
    // finish from asking Seerr more than once. Seerr refuses real duplicates itself.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly IUserDataManager _userData;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly SeerrGateway _seerr;
    private readonly UserJsonStore<RequestPrefs> _prefs;
    private readonly ILogger<AutoRequestService> _log;
    private readonly ConcurrentDictionary<string, DateTime> _tried = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoRequestService"/> class.
    /// </summary>
    /// <param name="userData">Jellyfin's played-state events.</param>
    /// <param name="users">Jellyfin's users.</param>
    /// <param name="library">Jellyfin's library.</param>
    /// <param name="seerr">Seerr gateway.</param>
    /// <param name="prefs">Per-person request preferences.</param>
    /// <param name="log">Logger (titles and season numbers only; never keys or addresses).</param>
    public AutoRequestService(IUserDataManager userData, IUserManager users, ILibraryManager library, SeerrGateway seerr, UserJsonStore<RequestPrefs> prefs, ILogger<AutoRequestService> log)
    {
        _userData = userData;
        _users = users;
        _library = library;
        _seerr = seerr;
        _prefs = prefs;
        _log = log;
    }

    private static RequestSettings Settings => Plugin.Instance?.Configuration.Requests ?? new RequestSettings();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether finishing <paramref name="episode"/> means the viewer has caught up:
    /// it's the highest-numbered episode of the highest season in the library.
    /// </summary>
    /// <param name="episode">(Season, episode) just finished.</param>
    /// <param name="library">(Season, episode) of every episode file in the library.</param>
    /// <returns>The next season number to request, or <c>null</c>.</returns>
    public static int? NextSeasonAfter((int Season, int Episode) episode, IReadOnlyCollection<(int Season, int Episode)> library)
    {
        var real = library.Where(e => e.Season > 0).ToList();
        if (episode.Season <= 0 || real.Count == 0)
        {
            return null;
        }

        var newest = real.Max(e => e.Season);
        if (episode.Season != newest || episode.Episode < real.Where(e => e.Season == newest).Max(e => e.Episode))
        {
            return null;
        }

        return newest + 1;
    }

    /// <summary>
    /// The next released film in a collection after <paramref name="tmdbId"/>.
    /// </summary>
    /// <param name="parts">Collection parts: (TMDB id, release date, already in library or requested).</param>
    /// <param name="tmdbId">The film just finished.</param>
    /// <param name="today">Today (UTC).</param>
    /// <returns>The next film's TMDB id, or <c>null</c>.</returns>
    public static long? NextInCollection(IReadOnlyList<(long Id, DateTime? Released, bool Have)> parts, long tmdbId, DateTime today)
    {
        var ordered = parts.Where(p => p.Released is not null).OrderBy(p => p.Released).ToList();
        var at = ordered.FindIndex(p => p.Id == tmdbId);
        if (at < 0 || at + 1 >= ordered.Count)
        {
            return null;
        }

        var next = ordered[at + 1];
        return next.Released <= today && !next.Have ? next.Id : null;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        var s = Settings;
        if (e.UserData is not { Played: true } || e.SaveReason is not (UserDataSaveReason.PlaybackFinished or UserDataSaveReason.TogglePlayed))
        {
            return;
        }

        var episode = e.Item is Episode && s.AutoRequestNextSeason;
        var movie = e.Item is Movie && s.AutoRequestNextMovie;
        if (!(episode || movie) || !_seerr.Configured)
        {
            return;
        }

        var userId = e.UserId;
        var item = e.Item;
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(userId, item, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
            {
                _log.LogWarning("Lumara auto-request skipped: {Reason}", ex.GetType().Name);
            }
        });
    }

    private async Task HandleAsync(Guid userId, BaseItem item, CancellationToken ct)
    {
        var user = _users.GetUserById(userId);
        if (user is null || !_prefs.Read(userId).AutoRequest)
        {
            return;
        }

        if (item is Episode ep && ep.Series is { } series && long.TryParse(series.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var showId))
        {
            var episodes = _library.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [series.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                IsVirtualItem = false,
                Recursive = true,
            })
                .OfType<Episode>()
                .Where(x => x.ParentIndexNumber is not null && x.IndexNumber is not null)
                .Select(x => (x.ParentIndexNumber!.Value, x.IndexNumberEnd ?? x.IndexNumber!.Value))
                .ToList();
            var next = NextSeasonAfter((ep.ParentIndexNumber ?? 0, ep.IndexNumberEnd ?? ep.IndexNumber ?? 0), episodes);
            if (next is null || !FirstTry($"{userId:N}:tv:{showId}:{next}"))
            {
                return;
            }

            var details = await _seerr.TitleAsync(user, "tv", showId, ct).ConfigureAwait(false);
            var season = details?.Seasons.FirstOrDefault(x => x.Number == next);
            if (season is null || season.Requested || season.Status is not ("unknown" or "deleted") || season.Episodes == 0)
            {
                return;
            }

            var result = await _seerr.RequestAsync(user, new NewRequest { MediaType = "tv", TmdbId = showId, Seasons = [next.Value] }, ct).ConfigureAwait(false);
            if (_log.IsEnabled(LogLevel.Information))
            {
                _log.LogInformation("Lumara auto-request: season {Season} of {Title} for {User}: {Result}", next, series.Name, user.Username, result.Message);
            }
            return;
        }

        if (item is Movie film && long.TryParse(film.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var filmId))
        {
            if (!FirstTry($"{userId:N}:movie:{filmId}"))
            {
                return;
            }

            var details = await _seerr.TitleAsync(user, "movie", filmId, ct).ConfigureAwait(false);
            if (details?.CollectionId is not { } collectionId)
            {
                return;
            }

            var seerrUser = await _seerr.UserForAsync(userId, ct).ConfigureAwait(false);
            if (seerrUser is null)
            {
                return;
            }

            using var doc = (await _seerr.AsUserAsync(seerrUser, HttpMethod.Get, $"/api/v1/collection/{collectionId}", null, ct).ConfigureAwait(false)).Json();
            if (doc is null)
            {
                return;
            }

            var lookup = _seerr.ItemLookup(null);
            var parts = doc.RootElement.Arr("parts")
                .Select(p =>
                {
                    var id = p.Long("id") ?? 0;
                    var status = p.Get("mediaInfo").Long("status") ?? 1;
                    return (id, p.Date("releaseDate"), status is not (1 or 7) || lookup("movie", id, null) is not null);
                })
                .ToList();
            var next = NextInCollection(parts, filmId, DateTime.UtcNow);
            if (next is null)
            {
                return;
            }

            var result = await _seerr.RequestAsync(user, new NewRequest { MediaType = "movie", TmdbId = next.Value }, ct).ConfigureAwait(false);
            if (_log.IsEnabled(LogLevel.Information))
            {
                _log.LogInformation("Lumara auto-request: next film in {Collection} for {User}: {Result}", details.CollectionName, user.Username, result.Message);
            }
        }
    }

    private bool FirstTry(string key)
    {
        var now = DateTime.UtcNow;
        if (_tried.TryGetValue(key, out var at) && now - at < RetryAfter)
        {
            return false;
        }

        if (_tried.Count > 5000)
        {
            _tried.Clear();
        }

        _tried[key] = now;
        return true;
    }
}
