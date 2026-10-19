// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations.Seerr;

/// <summary>
/// Seerr permission bits (seerr server/lib/permissions.ts). ADMIN is 2, and an
/// admin passes every check, exactly as Seerr's own <c>hasPermission</c> does.
/// </summary>
[Flags]
public enum SeerrAccess : long
{
    /// <summary>No permissions.</summary>
    None = 0,

    /// <summary>Administrator (implies everything).</summary>
    Admin = 2,

    /// <summary>Manage settings.</summary>
    ManageSettings = 4,

    /// <summary>Manage users.</summary>
    ManageUsers = 8,

    /// <summary>Approve, decline and remove requests.</summary>
    ManageRequests = 16,

    /// <summary>Request films and shows.</summary>
    Request = 32,

    /// <summary>Requests are approved straight away.</summary>
    AutoApprove = 128,

    /// <summary>Film requests are approved straight away.</summary>
    AutoApproveMovie = 256,

    /// <summary>Show requests are approved straight away.</summary>
    AutoApproveTv = 512,

    /// <summary>Request 4K copies.</summary>
    Request4k = 1024,

    /// <summary>Request 4K films.</summary>
    Request4kMovie = 2048,

    /// <summary>Request 4K shows.</summary>
    Request4kTv = 4096,

    /// <summary>Choose quality profile and folder.</summary>
    RequestAdvanced = 8192,

    /// <summary>See everyone's requests.</summary>
    RequestView = 16384,

    /// <summary>4K requests are approved straight away.</summary>
    AutoApprove4k = 32768,

    /// <summary>Request films.</summary>
    RequestMovie = 262144,

    /// <summary>Request shows.</summary>
    RequestTv = 524288,

    /// <summary>Resolve and reopen anyone's issues.</summary>
    ManageIssues = 1048576,

    /// <summary>See everyone's issues.</summary>
    ViewIssues = 2097152,

    /// <summary>Report issues.</summary>
    CreateIssues = 4194304,
}

/// <summary>
/// Turns Seerr's JSON into Lumara's shapes (SeerrModels.cs). Pure
/// functions over parsed JSON, so they're unit-tested against captured replies.
/// </summary>
public static class SeerrMapper
{
    /// <summary>Whether a permission set allows any of <paramref name="any"/> (admins always pass).</summary>
    /// <param name="permissions">The user's bits.</param>
    /// <param name="any">Permissions, any one of which is enough.</param>
    /// <returns><c>true</c> if allowed.</returns>
    public static bool Has(long permissions, params SeerrAccess[] any)
    {
        var p = (SeerrAccess)permissions;
        return p.HasFlag(SeerrAccess.Admin) || any.Any(a => (p & a) == a);
    }

    /// <summary>Lumara's name for a Seerr media status number.</summary>
    /// <param name="status">MediaStatus (1-7).</param>
    /// <returns>"unknown", "pending", "processing", "partial", "available", "blocked" or "deleted".</returns>
    public static string MediaStatus(long? status) => status switch
    {
        2 => "pending",
        3 => "processing",
        4 => "partial",
        5 => "available",
        6 => "blocked",
        7 => "deleted",
        _ => "unknown",
    };

    /// <summary>Lumara's name for a Seerr request status number.</summary>
    /// <param name="status">MediaRequestStatus (1-5).</param>
    /// <returns>"pending", "approved", "declined", "failed" or "completed".</returns>
    public static string RequestStatus(long? status) => status switch
    {
        2 => "approved",
        3 => "declined",
        4 => "failed",
        5 => "completed",
        _ => "pending",
    };

    /// <summary>Seerr's issue type number for a Lumara name.</summary>
    /// <param name="type">"video", "audio", "subtitles" or "other".</param>
    /// <returns>1-4.</returns>
    public static int IssueTypeNumber(string? type) => type switch
    {
        "video" => 1,
        "audio" => 2,
        "subtitles" => 3,
        _ => 4,
    };

    /// <summary>Lumara's name for a Seerr issue type number.</summary>
    /// <param name="type">IssueType (1-4).</param>
    /// <returns>The name.</returns>
    public static string IssueType(long? type) => type switch
    {
        1 => "video",
        2 => "audio",
        3 => "subtitles",
        _ => "other",
    };

    /// <summary>
    /// One search or discovery result; <c>null</c> for people and anything that
    /// isn't a film or show.
    /// </summary>
    /// <param name="r">Result element.</param>
    /// <param name="images">Signs image addresses.</param>
    /// <param name="itemId">Finds the visible library item: (mediaType, tmdbId, tvdbId) → id.</param>
    /// <param name="forcedType">The media type when the endpoint implies it (discover/movies lacks it on older Seerr).</param>
    /// <returns>The card.</returns>
    public static DiscoverItem? Item(JsonElement r, ImageSigner images, Func<string, long, long?, string?> itemId, string? forcedType = null)
    {
        var type = forcedType ?? r.Str("mediaType");
        if (type is not ("movie" or "tv"))
        {
            return null;
        }

        var tmdb = r.Long("id") ?? 0;
        if (tmdb <= 0)
        {
            return null;
        }

        var media = r.Get("mediaInfo");
        var title = type == "movie" ? r.Str("title") ?? r.Str("originalTitle") : r.Str("name") ?? r.Str("originalName");
        var date = type == "movie" ? r.Str("releaseDate") : r.Str("firstAirDate");
        return new DiscoverItem(
            type,
            tmdb,
            title ?? string.Empty,
            Json.Year(date),
            r.Str("overview") ?? string.Empty,
            images.Tmdb(r.Str("posterPath"), "w342"),
            images.Tmdb(r.Str("backdropPath"), "w1280"),
            MediaStatus(media.Long("status")),
            MediaStatus(media.Long("status4k")),
            itemId(type, tmdb, media.Long("tvdbId")),
            r.Num("voteAverage"));
    }

    /// <summary>A page of results.</summary>
    /// <param name="root">Response root ({page, totalPages, results}).</param>
    /// <param name="images">Signs image addresses.</param>
    /// <param name="itemId">Library lookup.</param>
    /// <param name="forcedType">Media type implied by the endpoint.</param>
    /// <param name="keep">Extra filter (parental limits, year), or <c>null</c>.</param>
    /// <returns>The page.</returns>
    public static DiscoverPage Page(JsonElement root, ImageSigner images, Func<string, long, long?, string?> itemId, string? forcedType = null, Func<JsonElement, DiscoverItem, bool>? keep = null)
    {
        var list = new List<DiscoverItem>();
        foreach (var r in root.Arr("results"))
        {
            if (Item(r, images, itemId, forcedType) is { } item && (keep is null || keep(r, item)))
            {
                list.Add(item);
            }
        }

        return new DiscoverPage(root.Int("page") ?? 1, Math.Max(1, root.Int("totalPages") ?? 1), list);
    }

    /// <summary>The US certification from a film or show details reply.</summary>
    /// <param name="root">Details root.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <returns>The certification, or <c>null</c>.</returns>
    public static string? Certification(JsonElement root, string mediaType)
    {
        if (mediaType == "tv")
        {
            return root.Get("contentRatings").Arr("results")
                .Where(r => r.Str("iso_3166_1") == "US")
                .Select(r => r.Str("rating"))
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        }

        // Theatrical (3) first, then any other release type with a certification.
        var dates = root.Get("releases").Arr("results")
            .Where(r => r.Str("iso_3166_1") == "US")
            .SelectMany(r => r.Arr("release_dates"))
            .Where(d => !string.IsNullOrWhiteSpace(d.Str("certification")))
            .OrderBy(d => d.Long("type") == 3 ? 0 : 1)
            .Select(d => d.Str("certification"));
        return dates.FirstOrDefault();
    }

    /// <summary>
    /// A film or show's details. Requests and permissions are filled in by the gateway.
    /// </summary>
    /// <param name="root">Details root (/api/v1/movie/{id} or /tv/{id}).</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="images">Signs image addresses.</param>
    /// <param name="itemId">Library lookup.</param>
    /// <returns>The details.</returns>
    public static TitleDetails Details(JsonElement root, string mediaType, ImageSigner images, Func<string, long, long?, string?> itemId)
    {
        var tvdb = root.Get("externalIds").Long("tvdbId") ?? root.Get("mediaInfo").Long("tvdbId");
        var item = Item(root, images, (t, id, _) => itemId(t, id, tvdb), mediaType)
            ?? new DiscoverItem(mediaType, root.Long("id") ?? 0, string.Empty, null, string.Empty, null, null, "unknown", "unknown", null, null);
        var media = root.Get("mediaInfo");
        var tv = mediaType == "tv";
        var seasonStatus = media.Arr("seasons").ToDictionary(s => s.Int("seasonNumber") ?? -1, s => (MediaStatus(s.Long("status")), MediaStatus(s.Long("status4k"))));
        var requested = new HashSet<int>(
            media.Arr("requests")
                .Where(r => r.Long("status") is 1 or 2)
                .SelectMany(r => r.Arr("seasons"))
                .Select(s => s.Int("seasonNumber") ?? -1));
        var seasons = tv
            ? root.Arr("seasons")
                .Select(s =>
                {
                    var n = s.Int("seasonNumber") ?? 0;
                    var st = seasonStatus.TryGetValue(n, out var v) ? v : ("unknown", "unknown");
                    return new SeasonInfo(n, s.Str("name") ?? string.Empty, s.Int("episodeCount") ?? 0, s.Str("airDate"), st.Item1, st.Item2, requested.Contains(n));
                })
                .OrderBy(s => s.Number)
                .ToList()
            : new List<SeasonInfo>();
        var runtime = tv ? root.Arr("episodeRunTime").Select(e => (int?)e.GetInt32()).FirstOrDefault() : root.Int("runtime");
        var companies = (tv ? root.Arr("networks") : root.Arr("productionCompanies")).Select(c => c.Str("name")).OfType<string>().Take(4).ToList();
        var progress = Progress(media.Get("downloadStatus"));
        return new TitleDetails
        {
            Item = item,
            Tagline = root.Str("tagline") ?? string.Empty,
            Genres = root.Arr("genres").Select(g => g.Str("name")).OfType<string>().ToList(),
            Runtime = runtime is > 0 ? runtime : null,
            Date = tv ? root.Str("firstAirDate") : root.Str("releaseDate"),
            Certification = Certification(root, mediaType),
            Companies = companies,
            Seasons = seasons,
            Cast = root.Get("credits").Arr("cast").Take(12)
                .Select(c => new CastMember(c.Str("name") ?? string.Empty, c.Str("character") ?? string.Empty, images.Tmdb(c.Str("profilePath"), "w185")))
                .ToList(),
            CollectionId = root.Get("collection").Long("id"),
            CollectionName = root.Get("collection").Str("name"),
            MediaId = media.Long("id"),
            OpenIssues = media.Arr("issues").Count(i => i.Long("status") == 1),
            Progress = progress.Progress,
        };
    }

    /// <summary>
    /// Download progress from Seerr's <c>downloadStatus</c> list (Seerr tracks the
    /// *arr queues itself; this sums every download for the title).
    /// </summary>
    /// <param name="downloads">The downloadStatus array.</param>
    /// <returns>Progress 0-1 and the latest estimated completion, or nulls when nothing is downloading.</returns>
    public static (double? Progress, string? Eta) Progress(JsonElement downloads)
    {
        double size = 0, left = 0;
        DateTime? eta = null;
        foreach (var d in downloads.Arr())
        {
            size += d.Num("size") ?? 0;
            left += d.Num("sizeLeft") ?? 0;
            if (d.Date("estimatedCompletionTime") is { } t && (eta is null || t > eta))
            {
                eta = t;
            }
        }

        if (size <= 0)
        {
            return (null, null);
        }

        return (Math.Clamp(1 - (left / size), 0, 1), eta is { } e ? Json.Iso(e) : null);
    }

    /// <summary>
    /// A request, without its title (Seerr's request list has only ids; the gateway
    /// adds title and poster from its details cache).
    /// </summary>
    /// <param name="r">Request element.</param>
    /// <param name="seerrUserId">The signed-in person's Seerr id.</param>
    /// <param name="permissions">Their permission bits.</param>
    /// <returns>The request.</returns>
    public static RequestInfo Request(JsonElement r, long seerrUserId, long permissions)
    {
        var media = r.Get("media");
        var by = r.Get("requestedBy");
        var status = RequestStatus(r.Long("status"));
        var mine = by.Long("id") == seerrUserId;
        var manage = Has(permissions, SeerrAccess.ManageRequests);
        var progress = Progress(media.Get(r.Bool("is4k") ? "downloadStatus4k" : "downloadStatus"));
        return new RequestInfo
        {
            Id = r.Long("id") ?? 0,
            Status = status,
            MediaStatus = MediaStatus(media.Long(r.Bool("is4k") ? "status4k" : "status")),
            Is4k = r.Bool("is4k"),
            MediaType = media.Str("mediaType") ?? r.Str("type") ?? "movie",
            TmdbId = media.Long("tmdbId") ?? 0,
            Seasons = r.Arr("seasons").Select(s => s.Int("seasonNumber") ?? 0).OrderBy(n => n).ToList(),
            RequestedBy = by.Str("displayName") ?? by.Str("jellyfinUsername") ?? by.Str("username") ?? string.Empty,
            Mine = mine,
            Created = r.Date("createdAt") is { } c ? Json.Iso(c) : string.Empty,
            CanApprove = manage && status == "pending",
            // Seerr lets requesters withdraw their own pending requests; managers can remove any.
            CanCancel = manage || (mine && status == "pending"),
            Progress = progress.Progress,
            Eta = progress.Eta,
        };
    }

    /// <summary>A problem report (title filled in by the gateway).</summary>
    /// <param name="i">Issue element.</param>
    /// <param name="permissions">The signed-in person's permission bits.</param>
    /// <param name="seerrUserId">Their Seerr id.</param>
    /// <returns>The report.</returns>
    public static IssueInfo Issue(JsonElement i, long permissions, long seerrUserId)
    {
        var media = i.Get("media");
        var by = i.Get("createdBy");
        return new IssueInfo
        {
            Id = i.Long("id") ?? 0,
            Type = IssueType(i.Long("issueType")),
            Status = i.Long("status") == 2 ? "resolved" : "open",
            MediaType = media.Str("mediaType") ?? "movie",
            TmdbId = media.Long("tmdbId") ?? 0,
            Season = i.Int("problemSeason") ?? 0,
            Episode = i.Int("problemEpisode") ?? 0,
            CreatedBy = by.Str("displayName") ?? by.Str("jellyfinUsername") ?? string.Empty,
            Created = i.Date("createdAt") is { } c ? Json.Iso(c) : string.Empty,
            Comments = i.Arr("comments")
                .Select((c, n) => new IssueComment(
                    // The first comment is the report itself; Seerr's list
                    // replies leave out comment authors, so fall back to the reporter.
                    c.Get("user").Str("displayName") ?? c.Get("user").Str("jellyfinUsername") ?? c.Get("user").Str("username")
                        ?? (n == 0 ? by.Str("displayName") ?? by.Str("jellyfinUsername") : null) ?? string.Empty,
                    c.Str("message") ?? string.Empty,
                    c.Date("createdAt") is { } d ? Json.Iso(d) : string.Empty))
                .ToList(),
            CanManage = Has(permissions, SeerrAccess.ManageIssues) || by.Long("id") == seerrUserId,
        };
    }

    /// <summary>Request quotas.</summary>
    /// <param name="root">/api/v1/user/{id}/quota reply.</param>
    /// <returns>The quotas.</returns>
    public static QuotaInfo Quota(JsonElement root)
    {
        static QuotaPart Part(JsonElement q) => new(
            q.Int("limit") ?? 0,
            q.Int("days") ?? 0,
            q.Int("used") ?? 0,
            q.Int("remaining"),
            q.Bool("restricted"));
        return new QuotaInfo(Part(root.Get("movie")), Part(root.Get("tv")));
    }

    /// <summary>Plain-English labels for the permissions that matter to an admin reviewing access.</summary>
    /// <param name="permissions">Permission bits.</param>
    /// <returns>Labels.</returns>
    public static IReadOnlyList<string> PermissionLabels(long permissions)
    {
        var p = (SeerrAccess)permissions;
        if (p.HasFlag(SeerrAccess.Admin))
        {
            return ["Seerr admin"];
        }

        var list = new List<string>();
        if (Has(permissions, SeerrAccess.Request) || Has(permissions, SeerrAccess.RequestMovie) || Has(permissions, SeerrAccess.RequestTv))
        {
            list.Add("Request");
        }

        if (Has(permissions, SeerrAccess.Request4k) || Has(permissions, SeerrAccess.Request4kMovie) || Has(permissions, SeerrAccess.Request4kTv))
        {
            list.Add("Request 4K");
        }

        if (Has(permissions, SeerrAccess.AutoApprove) || Has(permissions, SeerrAccess.AutoApproveMovie) || Has(permissions, SeerrAccess.AutoApproveTv))
        {
            list.Add("Auto-approve");
        }

        if (Has(permissions, SeerrAccess.RequestAdvanced))
        {
            list.Add("Advanced requests");
        }

        if (Has(permissions, SeerrAccess.ManageRequests))
        {
            list.Add("Manage requests");
        }

        if (Has(permissions, SeerrAccess.ManageIssues))
        {
            list.Add("Manage issues");
        }

        if (Has(permissions, SeerrAccess.ManageUsers))
        {
            list.Add("Manage users");
        }

        return list;
    }

    /// <summary>
    /// Things worth an admin's attention about one person's access.
    /// </summary>
    /// <param name="jellyfinAdmin">Jellyfin admin.</param>
    /// <param name="disabled">Jellyfin account disabled.</param>
    /// <param name="parentalLimit">Has a parental limit.</param>
    /// <param name="permissions">Seerr permission bits, or null when not linked.</param>
    /// <returns>Warnings in plain English.</returns>
    public static IReadOnlyList<string> AuditWarnings(bool jellyfinAdmin, bool disabled, bool parentalLimit, long? permissions)
    {
        var list = new List<string>();
        if (permissions is null)
        {
            if (!disabled)
            {
                list.Add("No Seerr account yet, so requests from Lumara won't work for them.");
            }

            return list;
        }

        var p = permissions.Value;
        if (Has(p, SeerrAccess.Admin) && !jellyfinAdmin)
        {
            list.Add("Seerr admin, but not a Jellyfin admin.");
        }

        if (disabled && (Has(p, SeerrAccess.Request) || Has(p, SeerrAccess.RequestMovie) || Has(p, SeerrAccess.RequestTv)))
        {
            list.Add("Their Jellyfin account is disabled, but Seerr still lets them request.");
        }

        if (parentalLimit && (Has(p, SeerrAccess.AutoApprove) || Has(p, SeerrAccess.AutoApproveMovie) || Has(p, SeerrAccess.AutoApproveTv)))
        {
            list.Add("Has a parental limit, but their requests are approved without review.");
        }

        return list;
    }
}
