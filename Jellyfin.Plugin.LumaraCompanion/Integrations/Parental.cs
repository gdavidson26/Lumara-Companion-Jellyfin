// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Linq;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations;

/// <summary>
/// Applies a Jellyfin user's parental limit to titles that aren't in the library
/// (Seerr discovery, the calendar, downloads), where Jellyfin's own checks don't
/// reach.
/// </summary>
/// <remarks>
/// Jellyfin stores the limit as a rating score (<c>User.MaxParentalRatingScore</c>,
/// 10.11 and 12). Titles from TMDB and the *arrs carry a US certification ("PG-13",
/// "TV-14"), which Jellyfin's localization turns into the same scale. A title with
/// no certification is treated as above the limit: for a child's account it's
/// better to hide an unrated title than to show an adult one.
/// </remarks>
public static class Parental
{
    /// <summary>US film certifications, lowest first (TMDB's order for certification.lte).</summary>
    public static readonly string[] MovieCertifications = ["G", "PG", "PG-13", "R", "NC-17"];

    /// <summary>US TV ratings, lowest first.</summary>
    public static readonly string[] TvCertifications = ["TV-Y", "TV-Y7", "TV-G", "TV-PG", "TV-14", "TV-MA"];

    // Fallback scores when the localization manager isn't available (tests) or
    // doesn't know a rating. These match Jellyfin's us.json (score = age).
    private static readonly (string Rating, int Score)[] Fallback =
    [
        ("G", 0), ("PG", 10), ("PG-13", 13), ("R", 17), ("NC-17", 18),
        ("TV-Y", 0), ("TV-Y7", 7), ("TV-G", 0), ("TV-PG", 10), ("TV-14", 14), ("TV-MA", 17),
    ];

    /// <summary>
    /// The user's limit, or <c>null</c> when they have none.
    /// </summary>
    /// <param name="user">Jellyfin user.</param>
    /// <returns>The highest allowed score.</returns>
    public static int? Limit(Jellyfin.Database.Implementations.Entities.User? user) => user?.MaxParentalRatingScore;

    /// <summary>
    /// The score of a US certification.
    /// </summary>
    /// <param name="certification">"PG-13", "TV-MA"...</param>
    /// <param name="localization">Jellyfin's localization (optional).</param>
    /// <returns>The score, or <c>null</c> when unknown.</returns>
    public static int? Score(string? certification, ILocalizationManager? localization)
    {
        if (string.IsNullOrWhiteSpace(certification))
        {
            return null;
        }

        var c = certification.Trim();
        if (localization?.GetRatingScore(c, "US") is { } s)
        {
            return s.Score;
        }

        var hit = Fallback.FirstOrDefault(f => string.Equals(f.Rating, c, StringComparison.OrdinalIgnoreCase));
        return hit.Rating is null ? null : hit.Score;
    }

    /// <summary>
    /// Whether a title with this certification may be shown.
    /// </summary>
    /// <param name="limit">The user's limit (null: no limit).</param>
    /// <param name="certification">The title's US certification.</param>
    /// <param name="localization">Jellyfin's localization (optional).</param>
    /// <returns><c>true</c> when allowed.</returns>
    public static bool Allows(int? limit, string? certification, ILocalizationManager? localization)
    {
        if (limit is null)
        {
            return true;
        }

        return Score(certification, localization) is { } s && s <= limit.Value;
    }

    /// <summary>
    /// The highest US certification within the limit, for TMDB's certification.lte
    /// filter (so discovery rows only list suitable titles in the first place).
    /// </summary>
    /// <param name="limit">The user's limit.</param>
    /// <param name="tv">TV ratings instead of film ones.</param>
    /// <param name="localization">Jellyfin's localization (optional).</param>
    /// <returns>The certification, or <c>null</c> when even the lowest is above the limit.</returns>
    public static string? HighestAllowed(int limit, bool tv, ILocalizationManager? localization)
    {
        var list = tv ? TvCertifications : MovieCertifications;
        string? best = null;
        foreach (var c in list)
        {
            if (Score(c, localization) is { } s && s <= limit)
            {
                best = c;
            }
        }

        return best;
    }
}
