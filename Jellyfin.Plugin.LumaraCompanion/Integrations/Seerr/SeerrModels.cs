// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System.Collections.Generic;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations.Seerr;

// The shapes Lumara apps receive. They are the plugin's own, not Seerr's: Seerr's
// JSON is reshaped here so apps don't break when Seerr changes, image paths become
// signed proxy addresses, and nothing internal (service URLs, other users' emails)
// is passed through. The Rust side reads them in lumara-core::requests.

/// <summary>What the signed-in person can do with requests and the *arr tools.</summary>
public sealed class RequestFeatures
{
    /// <summary>Gets or sets a value indicating whether Seerr is set up on the server.</summary>
    public bool Seerr { get; set; }

    /// <summary>Gets or sets a value indicating whether this person has a Seerr account linked to their Jellyfin account.</summary>
    public bool Linked { get; set; }

    /// <summary>Gets or sets a value indicating whether they can request films.</summary>
    public bool RequestMovies { get; set; }

    /// <summary>Gets or sets a value indicating whether they can request shows.</summary>
    public bool RequestTv { get; set; }

    /// <summary>Gets or sets a value indicating whether they can request films in 4K.</summary>
    public bool Request4kMovies { get; set; }

    /// <summary>Gets or sets a value indicating whether they can request shows in 4K.</summary>
    public bool Request4kTv { get; set; }

    /// <summary>Gets or sets a value indicating whether they may pick the quality profile and folder.</summary>
    public bool Advanced { get; set; }

    /// <summary>Gets or sets a value indicating whether they can approve and decline others' requests.</summary>
    public bool ManageRequests { get; set; }

    /// <summary>Gets or sets a value indicating whether they can report problems.</summary>
    public bool CreateIssues { get; set; }

    /// <summary>Gets or sets a value indicating whether they can resolve others' problem reports.</summary>
    public bool ManageIssues { get; set; }

    /// <summary>Gets or sets a value indicating whether the calendar has a source (Sonarr, Radarr, Lidarr or Shoko) they may see.</summary>
    public bool Calendar { get; set; }

    /// <summary>Gets or sets a value indicating whether there's a download list to show them.</summary>
    public bool Downloads { get; set; }

    /// <summary>Gets or sets a value indicating whether they're a Jellyfin admin (the *arr tools).</summary>
    public bool Admin { get; set; }

    /// <summary>Gets or sets a value indicating whether Bazarr is set up (admins only).</summary>
    public bool Bazarr { get; set; }

    /// <summary>Gets or sets a value indicating whether a parental limit applies (discovery is filtered, search is library-only).</summary>
    public bool ParentalLimit { get; set; }

    /// <summary>Gets or sets a value indicating whether the server auto-requests next seasons.</summary>
    public bool AutoRequestSeasons { get; set; }

    /// <summary>Gets or sets a value indicating whether the server auto-requests the next film in a collection.</summary>
    public bool AutoRequestMovies { get; set; }

    /// <summary>Gets or sets a value indicating whether this person turned auto-requests off for themselves.</summary>
    public bool AutoRequestOptOut { get; set; }
}

/// <summary>One title in a discovery row or search result.</summary>
/// <param name="MediaType">"movie" or "tv".</param>
/// <param name="TmdbId">TMDB id.</param>
/// <param name="Title">Title.</param>
/// <param name="Year">Release or first-air year.</param>
/// <param name="Overview">Synopsis.</param>
/// <param name="Poster">Signed poster address, or null.</param>
/// <param name="Backdrop">Signed backdrop address, or null.</param>
/// <param name="Status">Library status: "unknown", "pending", "processing", "partial", "available", "blocked" or "deleted".</param>
/// <param name="Status4k">The same for the 4K copy.</param>
/// <param name="ItemId">Jellyfin item id when it's in the library and visible to this person.</param>
/// <param name="Vote">TMDB vote average (0-10).</param>
public sealed record DiscoverItem(
    string MediaType,
    long TmdbId,
    string Title,
    int? Year,
    string Overview,
    string? Poster,
    string? Backdrop,
    string Status,
    string Status4k,
    string? ItemId,
    double? Vote);

/// <summary>A page of discovery results.</summary>
/// <param name="Page">Page number (1-based).</param>
/// <param name="TotalPages">Total pages.</param>
/// <param name="Results">The titles.</param>
public sealed record DiscoverPage(int Page, int TotalPages, IReadOnlyList<DiscoverItem> Results);

/// <summary>A genre or network to browse by.</summary>
/// <param name="Id">TMDB id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Backdrop">Signed backdrop of a popular title in it, or null.</param>
public sealed record BrowseOption(long Id, string Name, string? Backdrop);

/// <summary>A season of a show, with what Seerr knows about it.</summary>
/// <param name="Number">Season number (0 = specials).</param>
/// <param name="Name">Season name.</param>
/// <param name="Episodes">Episode count.</param>
/// <param name="AirDate">First air date (yyyy-MM-dd) or null.</param>
/// <param name="Status">Library status (see <see cref="DiscoverItem.Status"/>).</param>
/// <param name="Status4k">4K status.</param>
/// <param name="Requested">An open request covers it.</param>
public sealed record SeasonInfo(int Number, string Name, int Episodes, string? AirDate, string Status, string Status4k, bool Requested);

/// <summary>Someone in the cast.</summary>
/// <param name="Name">Name.</param>
/// <param name="Role">Character.</param>
/// <param name="Photo">Signed photo address, or null.</param>
public sealed record CastMember(string Name, string Role, string? Photo);

/// <summary>Everything the title page shows for a film or show from Seerr.</summary>
public sealed class TitleDetails
{
    /// <summary>Gets or sets the summary fields shared with discovery cards.</summary>
    public required DiscoverItem Item { get; set; }

    /// <summary>Gets or sets the tagline.</summary>
    public string Tagline { get; set; } = string.Empty;

    /// <summary>Gets or sets the genre names.</summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>Gets or sets the runtime in minutes (episode runtime for shows).</summary>
    public int? Runtime { get; set; }

    /// <summary>Gets or sets the release or first-air date (yyyy-MM-dd).</summary>
    public string? Date { get; set; }

    /// <summary>Gets or sets the US certification.</summary>
    public string? Certification { get; set; }

    /// <summary>Gets or sets the studios (films) or networks (shows).</summary>
    public IReadOnlyList<string> Companies { get; set; } = [];

    /// <summary>Gets or sets the seasons (shows only).</summary>
    public IReadOnlyList<SeasonInfo> Seasons { get; set; } = [];

    /// <summary>Gets or sets the top-billed cast.</summary>
    public IReadOnlyList<CastMember> Cast { get; set; } = [];

    /// <summary>Gets or sets the TMDB collection id (films), or null.</summary>
    public long? CollectionId { get; set; }

    /// <summary>Gets or sets the collection name.</summary>
    public string? CollectionName { get; set; }

    /// <summary>Gets or sets a value indicating whether this person can request it now (permission, and not already fully requested).</summary>
    public bool CanRequest { get; set; }

    /// <summary>Gets or sets a value indicating whether they can request a 4K copy.</summary>
    public bool CanRequest4k { get; set; }

    /// <summary>Gets or sets the open requests for this title.</summary>
    public IReadOnlyList<RequestInfo> Requests { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether it's on their watchlist.</summary>
    public bool OnWatchlist { get; set; }

    /// <summary>Gets or sets the Seerr media id (needed to report a problem), or null when Seerr doesn't track it yet.</summary>
    public long? MediaId { get; set; }

    /// <summary>Gets or sets the number of open problem reports.</summary>
    public int OpenIssues { get; set; }

    /// <summary>Gets or sets the download progress (0-1) while it's being downloaded, or null.</summary>
    public double? Progress { get; set; }
}

/// <summary>A Seerr request.</summary>
public sealed class RequestInfo
{
    /// <summary>Gets or sets the Seerr request id.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the request status: "pending", "approved", "declined", "failed" or "completed".</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Gets or sets the title's library status (see <see cref="DiscoverItem.Status"/>).</summary>
    public string MediaStatus { get; set; } = "unknown";

    /// <summary>Gets or sets a value indicating whether it's a 4K request.</summary>
    public bool Is4k { get; set; }

    /// <summary>Gets or sets "movie" or "tv".</summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id.</summary>
    public long TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the signed poster address.</summary>
    public string? Poster { get; set; }

    /// <summary>Gets or sets the requested season numbers (shows).</summary>
    public IReadOnlyList<int> Seasons { get; set; } = [];

    /// <summary>Gets or sets who asked for it (display name).</summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the signed-in person asked for it.</summary>
    public bool Mine { get; set; }

    /// <summary>Gets or sets when it was asked for (ISO 8601 UTC).</summary>
    public string Created { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the signed-in person may approve or decline it.</summary>
    public bool CanApprove { get; set; }

    /// <summary>Gets or sets a value indicating whether the signed-in person may withdraw it.</summary>
    public bool CanCancel { get; set; }

    /// <summary>Gets or sets the Jellyfin item id once it's in the library.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the download progress (0-1) while downloading.</summary>
    public double? Progress { get; set; }

    /// <summary>Gets or sets the estimated completion (ISO 8601 UTC) while downloading.</summary>
    public string? Eta { get; set; }
}

/// <summary>A page of requests.</summary>
/// <param name="Page">Page (1-based).</param>
/// <param name="TotalPages">Total pages.</param>
/// <param name="Results">The requests.</param>
public sealed record RequestPage(int Page, int TotalPages, IReadOnlyList<RequestInfo> Results);

/// <summary>One half of a request quota.</summary>
/// <param name="Limit">Requests allowed per period (0 = unlimited).</param>
/// <param name="Days">Period length in days.</param>
/// <param name="Used">Requests used in the current period.</param>
/// <param name="Remaining">Requests left, or null when unlimited.</param>
/// <param name="Restricted">No requests left.</param>
public sealed record QuotaPart(int Limit, int Days, int Used, int? Remaining, bool Restricted);

/// <summary>Request quotas for films and shows.</summary>
/// <param name="Movie">Film quota.</param>
/// <param name="Tv">Show quota (counted in seasons).</param>
public sealed record QuotaInfo(QuotaPart Movie, QuotaPart Tv);

/// <summary>A quality profile.</summary>
/// <param name="Id">Profile id.</param>
/// <param name="Name">Name.</param>
public sealed record ProfileOption(long Id, string Name);

/// <summary>A Radarr or Sonarr server the request can go to (advanced requests).</summary>
/// <param name="Id">Seerr's id for the server.</param>
/// <param name="Name">Name.</param>
/// <param name="Is4k">The 4K server.</param>
/// <param name="IsDefault">Seerr's default for this kind.</param>
/// <param name="Profiles">Quality profiles.</param>
/// <param name="RootFolders">Folder paths.</param>
/// <param name="DefaultProfileId">Profile Seerr picks by default.</param>
/// <param name="DefaultRootFolder">Folder Seerr picks by default.</param>
public sealed record ServerOption(long Id, string Name, bool Is4k, bool IsDefault, IReadOnlyList<ProfileOption> Profiles, IReadOnlyList<string> RootFolders, long? DefaultProfileId, string? DefaultRootFolder);

/// <summary>A comment on a problem report.</summary>
/// <param name="User">Who wrote it.</param>
/// <param name="Message">Text.</param>
/// <param name="Created">When (ISO 8601 UTC).</param>
public sealed record IssueComment(string User, string Message, string Created);

/// <summary>A problem report (Seerr issue).</summary>
public sealed class IssueInfo
{
    /// <summary>Gets or sets the Seerr issue id.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets "video", "audio", "subtitles" or "other".</summary>
    public string Type { get; set; } = "other";

    /// <summary>Gets or sets "open" or "resolved".</summary>
    public string Status { get; set; } = "open";

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets "movie" or "tv".</summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id.</summary>
    public long TmdbId { get; set; }

    /// <summary>Gets or sets the signed poster address.</summary>
    public string? Poster { get; set; }

    /// <summary>Gets or sets the season with the problem (0 = whole show).</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the episode with the problem (0 = whole season).</summary>
    public int Episode { get; set; }

    /// <summary>Gets or sets who reported it.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>Gets or sets when (ISO 8601 UTC).</summary>
    public string Created { get; set; } = string.Empty;

    /// <summary>Gets or sets the comments, oldest first (the first is the report itself).</summary>
    public IReadOnlyList<IssueComment> Comments { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the signed-in person can resolve or reopen it.</summary>
    public bool CanManage { get; set; }

    /// <summary>Gets or sets the Jellyfin item id, when it's in the library.</summary>
    public string? ItemId { get; set; }
}

/// <summary>A page of problem reports.</summary>
/// <param name="Page">Page (1-based).</param>
/// <param name="TotalPages">Total pages.</param>
/// <param name="Results">The reports.</param>
public sealed record IssuePage(int Page, int TotalPages, IReadOnlyList<IssueInfo> Results);

/// <summary>A Jellyfin user and their Seerr account, for the admin permission check.</summary>
public sealed class SeerrUserAudit
{
    /// <summary>Gets or sets the Jellyfin user id (32 hex digits).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin user name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether they're a Jellyfin admin.</summary>
    public bool JellyfinAdmin { get; set; }

    /// <summary>Gets or sets a value indicating whether their Jellyfin account is disabled.</summary>
    public bool Disabled { get; set; }

    /// <summary>Gets or sets a value indicating whether they have a parental limit.</summary>
    public bool ParentalLimit { get; set; }

    /// <summary>Gets or sets a value indicating whether a Seerr account is linked.</summary>
    public bool Linked { get; set; }

    /// <summary>Gets or sets their Seerr display name.</summary>
    public string? SeerrName { get; set; }

    /// <summary>Gets or sets their number of Seerr requests.</summary>
    public int Requests { get; set; }

    /// <summary>Gets or sets plain-English labels for their Seerr permissions ("Request", "Auto-approve"...).</summary>
    public IReadOnlyList<string> Permissions { get; set; } = [];

    /// <summary>Gets or sets things an admin should look at, in plain English.</summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];
}

/// <summary>Body of a new request.</summary>
public sealed class NewRequest
{
    /// <summary>Gets or sets "movie" or "tv".</summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id.</summary>
    public long TmdbId { get; set; }

    /// <summary>Gets or sets the seasons to request (empty: all of them).</summary>
    public IReadOnlyList<int> Seasons { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether to request the 4K copy.</summary>
    public bool Is4k { get; set; }

    /// <summary>Gets or sets the Radarr/Sonarr server (advanced), or null for Seerr's default.</summary>
    public long? ServerId { get; set; }

    /// <summary>Gets or sets the quality profile (advanced).</summary>
    public long? ProfileId { get; set; }

    /// <summary>Gets or sets the folder (advanced).</summary>
    public string? RootFolder { get; set; }
}

/// <summary>Body of a new problem report.</summary>
public sealed class NewIssue
{
    /// <summary>Gets or sets the Jellyfin item (film, show, season or episode), if reporting from the library.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets "movie" or "tv" (when there's no item id).</summary>
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the TMDB id (when there's no item id).</summary>
    public long? TmdbId { get; set; }

    /// <summary>Gets or sets "video", "audio", "subtitles" or "other".</summary>
    public string Type { get; set; } = "other";

    /// <summary>Gets or sets what's wrong (required, up to 1000 characters).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the season (0 = all).</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the episode (0 = all).</summary>
    public int Episode { get; set; }
}

/// <summary>The result of a write, with a plain-English message on failure.</summary>
/// <param name="Ok">It worked.</param>
/// <param name="Message">What happened, for the person.</param>
public sealed record WriteResult(bool Ok, string Message);
