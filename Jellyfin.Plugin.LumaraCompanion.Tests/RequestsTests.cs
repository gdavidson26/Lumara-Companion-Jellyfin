// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.LumaraCompanion.Integrations;
using Jellyfin.Plugin.LumaraCompanion.Integrations.Arr;
using Jellyfin.Plugin.LumaraCompanion.Integrations.Seerr;
using Xunit;

namespace Jellyfin.Plugin.LumaraCompanion.Tests;

/// <summary>Seerr mapping, the watchlist merge, *arr parsers and the image signer.</summary>
public sealed class RequestsTests
{
    private static readonly ImageSigner Images = new(new byte[32]);

    private static string? NoItem(string type, long tmdb, long? tvdb) => null;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ----- Seerr permissions and statuses -----

    [Fact]
    public void AdminPassesEveryPermissionCheck()
    {
        Assert.True(SeerrMapper.Has((long)SeerrAccess.Admin, SeerrAccess.ManageRequests));
        Assert.True(SeerrMapper.Has((long)SeerrAccess.Request, SeerrAccess.Request));
        Assert.False(SeerrMapper.Has((long)SeerrAccess.Request, SeerrAccess.ManageRequests));
        Assert.Equal(["Seerr admin"], SeerrMapper.PermissionLabels(2));
        Assert.Equal(["Request", "Auto-approve"], SeerrMapper.PermissionLabels(32 | 128));
    }

    [Fact]
    public void StatusNumbersMapToNames()
    {
        Assert.Equal("unknown", SeerrMapper.MediaStatus(1));
        Assert.Equal("partial", SeerrMapper.MediaStatus(4));
        Assert.Equal("available", SeerrMapper.MediaStatus(5));
        Assert.Equal("unknown", SeerrMapper.MediaStatus(null));
        Assert.Equal("declined", SeerrMapper.RequestStatus(3));
        Assert.Equal(3, SeerrMapper.IssueTypeNumber("subtitles"));
        Assert.Equal(4, SeerrMapper.IssueTypeNumber("anything"));
        Assert.Equal("audio", SeerrMapper.IssueType(2));
    }

    [Fact]
    public void SearchPageSkipsPeopleAndSignsImages()
    {
        var page = SeerrMapper.Page(
            Parse("""
            {"page":1,"totalPages":3,"results":[
              {"id":438631,"mediaType":"movie","title":"Dune","releaseDate":"2021-09-15","posterPath":"/d5NX.jpg","backdropPath":null,"overview":"Spice.","voteAverage":7.8,"mediaInfo":{"status":5,"status4k":1}},
              {"id":17419,"mediaType":"person","name":"Someone"},
              {"id":95396,"mediaType":"tv","name":"Severance","firstAirDate":"2022-02-17","posterPath":"/sev.jpg"}]}
            """),
            Images,
            (type, tmdb, _) => tmdb == 438631 ? "abc" : null);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(2, page.Results.Count);
        var dune = page.Results[0];
        Assert.Equal(("movie", "Dune", 2021, "available", "unknown", "abc"), (dune.MediaType, dune.Title, dune.Year, dune.Status, dune.Status4k, dune.ItemId));
        Assert.StartsWith("Lumara/Img/tmdb?p=%2Fw342%2Fd5NX.jpg&s=", dune.Poster, StringComparison.Ordinal);
        Assert.Null(dune.Backdrop);
        Assert.Equal("Severance", page.Results[1].Title);
        Assert.Equal("unknown", page.Results[1].Status);
    }

    [Fact]
    public void DetailsReadSeasonsRequestsAndCertification()
    {
        var d = SeerrMapper.Details(
            Parse("""
            {"id":95396,"name":"Severance","firstAirDate":"2022-02-17","episodeRunTime":[55],"genres":[{"name":"Drama"}],
             "networks":[{"name":"Apple TV+"}],"externalIds":{"tvdbId":371980},
             "contentRatings":{"results":[{"iso_3166_1":"GB","rating":"15"},{"iso_3166_1":"US","rating":"TV-MA"}]},
             "seasons":[{"seasonNumber":0,"name":"Specials","episodeCount":2},{"seasonNumber":1,"name":"Season 1","episodeCount":9,"airDate":"2022-02-17"},{"seasonNumber":2,"name":"Season 2","episodeCount":10}],
             "credits":{"cast":[{"name":"Adam Scott","character":"Mark","profilePath":"/a.jpg"}]},
             "mediaInfo":{"id":12,"status":4,"seasons":[{"seasonNumber":1,"status":5,"status4k":1}],
               "requests":[{"id":3,"status":1,"seasons":[{"seasonNumber":2}]}],"issues":[{"status":1},{"status":2}],
               "downloadStatus":[{"size":100,"sizeLeft":25,"estimatedCompletionTime":"2026-10-20T10:00:00Z"}]}}
            """),
            "tv",
            Images,
            (type, tmdb, tvdb) => tvdb == 371980 ? "item" : null);
        Assert.Equal("item", d.Item.ItemId);
        Assert.Equal("TV-MA", d.Certification);
        Assert.Equal(55, d.Runtime);
        Assert.Equal(["Apple TV+"], d.Companies);
        Assert.Equal(12, d.MediaId);
        Assert.Equal(1, d.OpenIssues);
        Assert.Equal(0.75, d.Progress);
        Assert.Equal([0, 1, 2], d.Seasons.Select(s => s.Number));
        Assert.Equal("available", d.Seasons[1].Status);
        Assert.True(d.Seasons[2].Requested);
        Assert.False(d.Seasons[1].Requested);
        Assert.Equal("Mark", d.Cast[0].Role);
    }

    [Fact]
    public void FilmCertificationPrefersTheatrical()
    {
        var root = Parse("""{"releases":{"results":[{"iso_3166_1":"US","release_dates":[{"type":4,"certification":"R"},{"type":3,"certification":"PG-13"},{"type":5,"certification":""}]}]}}""");
        Assert.Equal("PG-13", SeerrMapper.Certification(root, "movie"));
        Assert.Null(SeerrMapper.Certification(Parse("{}"), "movie"));
    }

    [Fact]
    public void RequestsKnowWhoMayApproveOrWithdraw()
    {
        var json = """
            {"id":9,"status":1,"is4k":false,"createdAt":"2026-10-19T05:00:00.000Z","seasons":[{"seasonNumber":2},{"seasonNumber":1}],
             "media":{"tmdbId":95396,"mediaType":"tv","status":3},"requestedBy":{"id":4,"displayName":"Sam"}}
            """;
        var mine = SeerrMapper.Request(Parse(json), 4, (long)SeerrAccess.Request);
        Assert.True(mine.Mine);
        Assert.True(mine.CanCancel);
        Assert.False(mine.CanApprove);
        Assert.Equal([1, 2], mine.Seasons);
        Assert.Equal("processing", mine.MediaStatus);
        Assert.Equal("2026-10-19T05:00:00Z", mine.Created);
        var other = SeerrMapper.Request(Parse(json), 7, (long)SeerrAccess.Request);
        Assert.False(other.CanCancel);
        var manager = SeerrMapper.Request(Parse(json), 7, (long)SeerrAccess.ManageRequests);
        Assert.True(manager.CanApprove);
        Assert.True(manager.CanCancel);
    }

    [Fact]
    public void QuotaAndAuditWarnings()
    {
        var q = SeerrMapper.Quota(Parse("""{"movie":{"days":7,"limit":5,"used":5,"remaining":0,"restricted":true},"tv":{"days":0,"limit":0,"used":0,"restricted":false}}"""));
        Assert.True(q.Movie.Restricted);
        Assert.Null(q.Tv.Remaining);
        Assert.Single(SeerrMapper.AuditWarnings(false, false, false, null));
        Assert.Empty(SeerrMapper.AuditWarnings(false, true, false, null));
        Assert.Contains(SeerrMapper.AuditWarnings(false, false, false, 2), w => w.Contains("not a Jellyfin admin", StringComparison.Ordinal));
        Assert.Contains(SeerrMapper.AuditWarnings(false, true, false, 32), w => w.Contains("disabled", StringComparison.Ordinal));
        Assert.Contains(SeerrMapper.AuditWarnings(false, false, true, 32 | 128), w => w.Contains("parental", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Dune 1984", "Dune", 1984)]
    [InlineData("Dune (2021)", "Dune", 2021)]
    [InlineData("Blade Runner 2049", "Blade Runner", 2049)]
    [InlineData("1917", "1917", null)]
    [InlineData("Alien", "Alien", null)]
    public void SearchSplitsTrailingYear(string query, string text, int? year)
    {
        Assert.Equal((text, year), SeerrGateway.SplitYear(query));
    }

    // ----- Watchlist merge -----

    [Fact]
    public void WatchlistMergeFollowsBothSides()
    {
        var doc = new WatchlistDoc();
        doc.Items.Add(new WatchlistEntry { Type = "movie", Tmdb = 1, Title = "Kept", Synced = true });
        doc.Items.Add(new WatchlistEntry { Type = "movie", Tmdb = 2, Title = "Removed in Seerr", Synced = true });
        doc.Items.Add(new WatchlistEntry { Type = "tv", Tmdb = 3, Title = "Added here", Synced = false });
        doc.Removed.Add("movie:9");
        var remote = new List<(string, long, string)> { ("movie", 1, "Kept"), ("movie", 4, "Added in Seerr"), ("movie", 9, "Removed here") };

        var (add, remove) = Watchlist.Merge(doc, remote, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal([1L, 3, 4], doc.Items.Select(i => i.Tmdb).OrderBy(i => i));
        Assert.Equal([3L], add.Select(a => a.Tmdb));
        Assert.Equal([("movie", 9L)], remove);
        Assert.Empty(doc.Removed);
        Assert.True(doc.Items.Single(i => i.Tmdb == 4).Synced);
    }

    [Fact]
    public void WatchlistMergeMarksAlreadyPresentAsSynced()
    {
        var doc = new WatchlistDoc();
        doc.Items.Add(new WatchlistEntry { Type = "movie", Tmdb = 5, Synced = false });
        var (add, _) = Watchlist.Merge(doc, [("movie", 5, "x")], DateTime.UtcNow);
        Assert.Empty(add);
        Assert.True(doc.Items[0].Synced);
    }

    // ----- *arr parsers -----

    [Fact]
    public void SonarrCalendarSkipsUnmonitoredAndPrefersTmdbArt()
    {
        var rows = CalendarService.ParseSonarr(
            Parse("""
            [{"id":1,"title":"TBA","seasonNumber":31,"episodeNumber":115,"airDateUtc":"2026-10-20T03:00:00Z","hasFile":false,"monitored":true,
              "series":{"title":"The Daily Show","tvdbId":71256,"tmdbId":2224,"certification":"TV-14","images":[{"coverType":"poster","remoteUrl":"https://artworks.thetvdb.com/banners/v4/series/71256/posters/65cc.jpg"}]}},
             {"id":2,"title":"Hidden","airDateUtc":"2026-10-21T03:00:00Z","monitored":false,"series":{}}]
            """),
            Images,
            "http://sonarr:8989");
        var (entry, cert, type, tmdb, tvdb) = Assert.Single(rows);
        Assert.Equal(("The Daily Show", 31, 115, "2026-10-20T03:00:00Z"), (entry.Title, entry.Season, entry.Episode, entry.Date));
        Assert.Equal(("TV-14", "tv", 2224L, 71256L), (cert, type, tmdb!.Value, tvdb!.Value));
        Assert.StartsWith("Lumara/Img/tvdb?", entry.Poster, StringComparison.Ordinal);
    }

    [Fact]
    public void RadarrCalendarMakesOneEntryPerReleaseInRange()
    {
        var rows = CalendarService.ParseRadarr(
            Parse("""
                [{"id":2,"title":"Dune: Part Two","year":2024,"tmdbId":693134,"certification":"PG-13","monitored":true,
                  "inCinemas":"2024-02-27T00:00:00Z","digitalRelease":"2024-04-15T00:00:00Z","physicalRelease":"2024-05-14T00:00:00Z",
                  "images":[{"coverType":"poster","url":"/radarr/MediaCover/2/poster.jpg?lastWrite=1","remoteUrl":"https://image.tmdb.org/t/p/original/6izw.jpg"}]}]
                """),
            Images,
            "http://host:7878/radarr",
            new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(["cinema", "digital"], rows.Select(r => r.Entry.Release));
        Assert.All(rows, r => Assert.True(r.Entry.AllDay));
        Assert.StartsWith("Lumara/Img/tmdb?p=%2Fw342%2F6izw.jpg", rows[0].Entry.Poster, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrRelativeStripsUrlBaseAndQuery()
    {
        Assert.Equal("/MediaCover/2/poster.jpg", ImageSigner.ArrRelative("http://host:7878/radarr", "/radarr/MediaCover/2/poster.jpg?lastWrite=1"));
        Assert.Equal("/MediaCover/2/poster.jpg", ImageSigner.ArrRelative("http://host:7878", "/MediaCover/2/poster.jpg"));
        Assert.Null(ImageSigner.ArrRelative("http://host", "/api/v3/system/status"));
    }

    [Fact]
    public void QueueGroupsSeasonPacksAndReadsStates()
    {
        var items = DownloadsService.ParseQueue(
            Parse("""
            {"records":[
              {"id":1,"downloadId":"HASH1","size":1000,"sizeleft":250,"timeleft":"00:10:00","status":"downloading","trackedDownloadState":"downloading","trackedDownloadStatus":"ok",
               "series":{"title":"Severance","tvdbId":371980,"tmdbId":95396},"episode":{"seasonNumber":2,"episodeNumber":1}},
              {"id":2,"downloadId":"HASH1","size":1000,"sizeleft":250,"status":"downloading","series":{"title":"Severance"},"episode":{"seasonNumber":2,"episodeNumber":2}},
              {"id":3,"downloadId":"HASH2","size":10,"sizeleft":0,"status":"completed","trackedDownloadState":"importPending","trackedDownloadStatus":"warning",
               "statusMessages":[{"title":"x","messages":["No files found are eligible for import"]}],"series":{"title":"Bluey"},"episode":{"seasonNumber":1,"episodeNumber":4}}]}
            """),
            "sonarr",
            Images,
            null);
        Assert.Equal(2, items.Count);
        var pack = items[0];
        Assert.Equal(("Severance", "Season 2 · 2 episodes", 0.75, 600L), (pack.Title, pack.Detail, pack.Progress, pack.SecondsLeft!.Value));
        Assert.Equal(("downloading", 95396L, 371980L), (pack.Status, pack.TmdbId!.Value, pack.TvdbId!.Value));
        Assert.Equal(("warning", "No files found are eligible for import"), (items[1].Status, items[1].Message));
        Assert.Equal("S1 E4", items[1].Detail);
    }

    [Fact]
    public void DownloadClientsParse()
    {
        var qb = DownloadsService.ParseQBittorrent(Parse("""[{"hash":"a","name":"A","progress":0.5,"eta":8640000,"state":"pausedDL","size":10,"amount_left":5},{"hash":"b","progress":1,"state":"uploading"}]"""));
        var t = Assert.Single(qb);
        Assert.Equal(("paused", (long?)null), (t.Status, t.SecondsLeft));
        var (sab, speed) = DownloadsService.ParseSabnzbd(Parse("""{"queue":{"kbpersec":"1.5","slots":[{"nzo_id":"n","filename":"F","percentage":"40","mb":"100","mbleft":"60","timeleft":"1:02:03","status":"Downloading","cat":"*"}]}}"""));
        Assert.Equal(1536, speed);
        Assert.Equal(("downloading", 0.4, 3723L, string.Empty), (sab[0].Status, sab[0].Progress, sab[0].SecondsLeft!.Value, sab[0].Detail));
        Assert.Null(DownloadsService.ParseTimeLeft("0:00:00"));
        Assert.Equal(90061, DownloadsService.ParseTimeLeft("1.01:01:01"));
    }

    // ----- Images -----

    [Fact]
    public void SignerOnlyServesWhatItSigned()
    {
        var url = Images.Url("tmdb", "/w342/abc.jpg")!;
        var sig = url[(url.IndexOf("&s=", StringComparison.Ordinal) + 3)..];
        Assert.True(Images.Verify("tmdb", "/w342/abc.jpg", sig));
        Assert.False(Images.Verify("tmdb", "/w342/abd.jpg", sig));
        Assert.False(Images.Verify("radarr", "/w342/abc.jpg", sig));
        Assert.False(new ImageSigner(Enumerable.Repeat((byte)1, 32).ToArray()).Verify("tmdb", "/w342/abc.jpg", sig));
        Assert.Null(Images.Url("tmdb", "/w342/../secret.jpg"));
        Assert.Null(Images.Url("radarr", "/api/v3/system/status"));
        Assert.Null(Images.Url("sonarr", "/MediaCover//1/poster.jpg"));
        Assert.Null(Images.Url("other", "/w342/abc.jpg"));
        Assert.NotNull(Images.Url("shoko", "/api/v3/Image/AniDB/Poster/77"));
        Assert.Null(Images.Url("shoko", "/api/v3/Dashboard/AniDBCalendar"));
    }

    // ----- Parental limits -----

    [Fact]
    public void ParentalLimitUsesUsScores()
    {
        Assert.True(Parental.Allows(null, null, null));
        Assert.True(Parental.Allows(13, "PG-13", null));
        Assert.False(Parental.Allows(13, "R", null));
        Assert.False(Parental.Allows(13, null, null)); // unrated is hidden for limited accounts
        Assert.Equal("PG", Parental.HighestAllowed(10, false, null));
        Assert.Equal("TV-PG", Parental.HighestAllowed(10, true, null));
        Assert.Null(Parental.HighestAllowed(-1, false, null));
    }

    // ----- Auto-request rules -----

    [Fact]
    public void NextSeasonOnlyAfterTheNewestEpisode()
    {
        var lib = new List<(int, int)> { (1, 1), (1, 2), (2, 1), (2, 2), (0, 1) };
        Assert.Equal(3, AutoRequestService.NextSeasonAfter((2, 2), lib));
        Assert.Null(AutoRequestService.NextSeasonAfter((2, 1), lib));
        Assert.Null(AutoRequestService.NextSeasonAfter((1, 2), lib));
        Assert.Null(AutoRequestService.NextSeasonAfter((0, 1), lib));
    }

    [Fact]
    public void NextFilmInCollectionMustBeReleasedAndMissing()
    {
        var today = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        var parts = new List<(long, DateTime?, bool)>
        {
            (1, new DateTime(2021, 9, 15, 0, 0, 0, DateTimeKind.Utc), true),
            (2, new DateTime(2024, 2, 27, 0, 0, 0, DateTimeKind.Utc), false),
            (3, new DateTime(2027, 12, 18, 0, 0, 0, DateTimeKind.Utc), false),
        };
        Assert.Equal(2, AutoRequestService.NextInCollection(parts, 1, today));
        Assert.Null(AutoRequestService.NextInCollection(parts, 2, today)); // next one isn't out yet
        Assert.Null(AutoRequestService.NextInCollection(parts, 3, today));
        parts[1] = (2, parts[1].Item2, true);
        Assert.Null(AutoRequestService.NextInCollection(parts, 1, today));
    }

    // ----- Tags -----

    [Fact]
    public void TagCopyOnlyTouchesPrefixedTags()
    {
        var result = TagSync.Desired(["Favourite", "arr: old", "ARR: Family"], ["family", "4k", " "], "arr: ");
        Assert.Equal(["Favourite", "arr: family", "arr: 4k"], result);
        Assert.Equal(["mine"], TagSync.Desired(["mine", "arr: gone"], [], "arr: "));
    }
}
