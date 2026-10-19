// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations;

/// <summary>
/// Signs image addresses the plugin hands to Lumara apps, so posters from TMDB,
/// the *arr apps and Shoko load through this server.
/// </summary>
/// <remarks>
/// Why: Lumara apps only ever talk to the viewer's own server (no third-party
/// requests from the device), and the *arr image URLs need the API key. So every
/// poster a response mentions becomes <c>/Lumara/Img/{source}?p={path}&amp;s={sig}</c>.
/// Images are loaded by &lt;img&gt; tags, which can't send the session header, and a
/// token in an image URL ends up in places it doesn't belong; so the endpoint is
/// anonymous and the HMAC signature is what makes it safe: it only serves paths the
/// plugin itself put in a response. The key is random per server start, so old
/// links simply stop working after a restart (apps refetch their lists anyway).
/// </remarks>
public sealed partial class ImageSigner
{
    private readonly byte[] _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageSigner"/> class.
    /// </summary>
    /// <param name="key">HMAC key; a random one when <c>null</c>.</param>
    public ImageSigner(byte[]? key = null)
    {
        _key = key ?? RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>
    /// The relative address apps load an image from, or <c>null</c> when the path
    /// isn't one this proxy serves.
    /// </summary>
    /// <param name="source">"tmdb", "tvdb", "radarr", "sonarr", "lidarr" or "shoko".</param>
    /// <param name="path">Path at that source (e.g. "/w342/abc.jpg" or "/MediaCover/4/poster.jpg").</param>
    /// <returns>A path like <c>Lumara/Img/tmdb?p=...&amp;s=...</c> (no leading slash; apps add the server address).</returns>
    public string? Url(string source, string? path)
    {
        if (string.IsNullOrEmpty(path) || !IsAllowed(source, path))
        {
            return null;
        }

        return $"Lumara/Img/{source}?p={Uri.EscapeDataString(path)}&s={Sign(source, path)}";
    }

    /// <summary>
    /// A TMDB image at a given width bucket.
    /// </summary>
    /// <param name="tmdbPath">TMDB file path ("/abc.jpg").</param>
    /// <param name="size">TMDB size name ("w342", "w780", "w1280", "original").</param>
    /// <returns>The signed address, or <c>null</c>.</returns>
    public string? Tmdb(string? tmdbPath, string size) =>
        string.IsNullOrEmpty(tmdbPath) ? null : Url("tmdb", "/" + size + (tmdbPath.StartsWith('/') ? tmdbPath : "/" + tmdbPath));

    /// <summary>Checks a signature in constant time.</summary>
    /// <param name="source">Source name.</param>
    /// <param name="path">Path.</param>
    /// <param name="signature">Signature from the query string.</param>
    /// <returns><c>true</c> when the plugin issued this address.</returns>
    public bool Verify(string source, string path, string? signature)
    {
        if (string.IsNullOrEmpty(signature) || !IsAllowed(source, path))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(source, path));
        var given = Encoding.ASCII.GetBytes(signature);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>
    /// Whether a path has the shape that source's images have. A second line of
    /// defence after the signature: even a leaked key can't turn the proxy into a
    /// general fetcher (no "..", no other API routes).
    /// </summary>
    /// <param name="source">Source name.</param>
    /// <param name="path">Path.</param>
    /// <returns><c>true</c> when allowed.</returns>
    public static bool IsAllowed(string source, string path)
    {
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        return source switch
        {
            "tmdb" => TmdbPath().IsMatch(path),
            "tvdb" => TvdbPath().IsMatch(path),
            "radarr" or "sonarr" or "lidarr" => ArrPath().IsMatch(path),
            "shoko" => ShokoPath().IsMatch(path),
            _ => false,
        };
    }

    /// <summary>
    /// Turns an *arr image address into a path relative to the stored service
    /// address. The *arrs report covers as "/MediaCover/12/poster.jpg?lastWrite=…",
    /// with their URL base in front when one is set ("/sonarr/MediaCover/…"),
    /// while the stored address may already end in that base.
    /// </summary>
    /// <param name="serviceUrl">The stored service address.</param>
    /// <param name="imageUrl">The image address from the *arr.</param>
    /// <returns>Path from the service address, without a query, or <c>null</c>.</returns>
    public static string? ArrRelative(string? serviceUrl, string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            return null;
        }

        var path = imageUrl;
        if (TryHttp(imageUrl, out var abs))
        {
            path = abs.AbsolutePath;
        }

        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            path = path[..q];
        }

        var basePath = TryHttp(serviceUrl, out var b) ? b.AbsolutePath.TrimEnd('/') : string.Empty;
        if (basePath.Length > 0 && path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[basePath.Length..];
        }

        var at = path.IndexOf("/MediaCover/", StringComparison.OrdinalIgnoreCase);
        return at >= 0 ? path[at..] : null;
    }

    [GeneratedRegex(@"^/(w\d{2,4}|h\d{2,4}|original)/[A-Za-z0-9_\-]+\.(jpg|jpeg|png|svg|webp)$")]
    private static partial Regex TmdbPath();

    /// <summary>
    /// The best poster (or other cover) an *arr lists for a title, as a signed address.
    /// </summary>
    /// <remarks>
    /// *arr image lists hold a local <c>url</c> ("/MediaCover/…", needs the API key)
    /// and a <c>remoteUrl</c> (TMDB, TheTVDB, fanart.tv). Calendar and queue replies
    /// often carry only the remote one. A TMDB image is preferred (it can be fetched
    /// at card size), then the local copy, then TheTVDB.
    /// </remarks>
    /// <param name="owner">Element with an "images" array.</param>
    /// <param name="source">"radarr", "sonarr" or "lidarr" (for the local copy).</param>
    /// <param name="serviceUrl">Stored service address.</param>
    /// <param name="coverType">"poster", "cover", "fanart"...</param>
    /// <returns>The signed address, or <c>null</c>.</returns>
    public string? ArrImage(System.Text.Json.JsonElement owner, string source, string? serviceUrl, string coverType = "poster")
    {
        var image = Json.Arr(owner, "images").FirstOrDefault(i => string.Equals(Json.Str(i, "coverType"), coverType, StringComparison.OrdinalIgnoreCase));
        if (image.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        var remote = Json.Str(image, "remoteUrl");
        if (TryHttp(remote, out var r))
        {
            // https://image.tmdb.org/t/p/original/abc.jpg -> /abc.jpg at w342.
            if (r.Host == "image.tmdb.org" && r.AbsolutePath.StartsWith("/t/p/", StringComparison.Ordinal))
            {
                var file = r.AbsolutePath[(r.AbsolutePath.LastIndexOf('/') + 1)..];
                if (Tmdb("/" + file, coverType == "fanart" ? "w1280" : "w342") is { } tmdb)
                {
                    return tmdb;
                }
            }
        }

        if (Url(source, ArrRelative(serviceUrl, Json.Str(image, "url"))) is { } local)
        {
            return local;
        }

        return r is not null && r.Host == "artworks.thetvdb.com" ? Url("tvdb", r.AbsolutePath) : null;
    }

    /// <summary>
    /// Parses an http(s) address. Plain <see cref="Uri.TryCreate(string, UriKind, out Uri)"/>
    /// with <see cref="UriKind.Absolute"/> also accepts "/radarr/MediaCover/1.jpg" on Linux (as
    /// a file path), where it then escapes the query; so anything that isn't http(s) is refused.
    /// </summary>
    /// <param name="text">Text to parse.</param>
    /// <param name="uri">The address.</param>
    /// <returns><c>true</c> for an http or https address.</returns>
    private static bool TryHttp(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
        {
            uri = u;
            return true;
        }

        uri = null;
        return false;
    }

    [GeneratedRegex(@"^/banners/[A-Za-z0-9_\-/]+\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex TvdbPath();

    [GeneratedRegex(@"^/MediaCover/[A-Za-z0-9_\-/]+\.(jpg|jpeg|png|gif|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArrPath();

    [GeneratedRegex(@"^/api/v3/Image/[A-Za-z]+/[A-Za-z]+/[0-9]+$")]
    private static partial Regex ShokoPath();

    private string Sign(string source, string path)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(source + "\n" + path));
        // 16 bytes is plenty against guessing and keeps URLs short.
        return Convert.ToHexString(mac, 0, 16).ToLowerInvariant();
    }
}
