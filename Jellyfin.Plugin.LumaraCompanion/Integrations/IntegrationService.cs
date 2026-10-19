// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LumaraCompanion.Configuration;
using Jellyfin.Plugin.LumaraCompanion.Security;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations;

/// <summary>
/// Services the plugin can connect to on the server's behalf.
/// </summary>
public enum IntegrationKind
{
    /// <summary>Seerr (requests).</summary>
    Seerr,

    /// <summary>Radarr (movies).</summary>
    Radarr,

    /// <summary>Sonarr (TV).</summary>
    Sonarr,

    /// <summary>Lidarr (music).</summary>
    Lidarr,

    /// <summary>Bazarr (subtitles).</summary>
    Bazarr,

    /// <summary>Prowlarr (indexers).</summary>
    Prowlarr,

    /// <summary>Shoko (anime).</summary>
    Shoko,

    /// <summary>Tautulli-style statistics (Jellystat).</summary>
    Jellystat,

    /// <summary>TMDB (ratings, reviews, availability). Needs only an API key.</summary>
    Tmdb,

    /// <summary>MDBList (extra ratings, lists). Needs only an API key.</summary>
    MdbList,

    /// <summary>qBittorrent (downloads). Signs in with a user name and password.</summary>
    QBittorrent,

    /// <summary>SABnzbd (downloads).</summary>
    Sabnzbd,
}

/// <summary>
/// What clients and the dashboard may know about an integration: whether it's set
/// up, never the values themselves.
/// </summary>
/// <param name="Kind">Integration name.</param>
/// <param name="UrlConfigured">A URL is stored.</param>
/// <param name="ApiKeyConfigured">An API key is stored.</param>
/// <param name="NeedsUrl">Whether this integration needs a URL at all.</param>
/// <param name="NeedsUsername">Whether it signs in with a user name (the key field is then the password).</param>
/// <param name="UsernameConfigured">A user name is stored.</param>
/// <param name="LinkUrl">The browser address for admin links (not secret; admins only).</param>
public sealed record IntegrationStatus(string Kind, bool UrlConfigured, bool ApiKeyConfigured, bool NeedsUrl, bool NeedsUsername = false, bool UsernameConfigured = false, string LinkUrl = "");

/// <summary>
/// Result of a connection test. The message never contains the URL or key.
/// </summary>
/// <param name="Ok">The service answered with a success status.</param>
/// <param name="Message">Plain-English result for the admin.</param>
public sealed record IntegrationTestResult(bool Ok, string Message);

/// <summary>
/// Stores, reports and tests integration settings. Secrets are encrypted at rest
/// and only ever decrypted inside this class for server-side calls.
/// </summary>
public sealed class IntegrationService
{
    private readonly SecretProtector _protector;
    private readonly Func<PluginConfiguration> _config;
    private readonly Action _save;
    private readonly HttpClient? _http;
    private int _revision;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationService"/> class.
    /// </summary>
    /// <param name="protector">Encrypts and decrypts secrets.</param>
    /// <param name="config">Returns the live plugin configuration.</param>
    /// <param name="save">Persists the plugin configuration.</param>
    /// <param name="http">
    /// HTTP client for connection tests (optional in unit tests). Deliberately a
    /// plain client, not one from IHttpClientFactory: factory clients log every
    /// request URL, which would put integration addresses (and MDBList's key,
    /// which that API requires in the URL) into the server log.
    /// </param>
    public IntegrationService(SecretProtector protector, Func<PluginConfiguration> config, Action save, HttpClient? http = null)
    {
        _protector = protector;
        _config = config;
        _save = save;
        _http = http;
    }

    /// <summary>
    /// Gets a number that changes whenever an integration is saved or removed.
    /// Caches of service replies include it in their keys, so a new address,
    /// key or password takes effect at once instead of after the cache expires.
    /// </summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// Whether an integration needs a base URL (API-key-only services don't).
    /// </summary>
    /// <param name="kind">The integration.</param>
    /// <returns><c>true</c> if a URL is required.</returns>
    public static bool NeedsUrl(IntegrationKind kind) => kind is not (IntegrationKind.Tmdb or IntegrationKind.MdbList);

    /// <summary>
    /// Whether an integration signs in with a user name and password instead of an API key.
    /// </summary>
    /// <param name="kind">The integration.</param>
    /// <returns><c>true</c> for qBittorrent.</returns>
    public static bool NeedsUsername(IntegrationKind kind) => kind is IntegrationKind.QBittorrent;

    /// <summary>
    /// Whether an integration has what it needs to be called (URL and key, plus a
    /// user name where one is required).
    /// </summary>
    /// <param name="kind">The integration.</param>
    /// <returns><c>true</c> when it can be used.</returns>
    public bool IsConfigured(IntegrationKind kind)
    {
        var (url, key, user) = RevealAll(kind);
        return !string.IsNullOrEmpty(key)
            && (!NeedsUrl(kind) || !string.IsNullOrEmpty(url))
            && (!NeedsUsername(kind) || !string.IsNullOrEmpty(user));
    }

    /// <summary>
    /// The admin-only browser address for links to the app, or empty.
    /// </summary>
    /// <param name="kind">The integration.</param>
    /// <returns>The link address without a trailing slash, or empty.</returns>
    public string LinkUrl(IntegrationKind kind) => Find(kind)?.LinkUrl ?? string.Empty;

    /// <summary>
    /// Status of every integration. Safe to return to the dashboard.
    /// </summary>
    /// <returns>One entry per <see cref="IntegrationKind"/>.</returns>
    public IReadOnlyList<IntegrationStatus> Statuses()
    {
        var stored = _config().Integrations;
        return Enum.GetValues<IntegrationKind>()
            .Select(k =>
            {
                var s = stored.FirstOrDefault(x => string.Equals(x.Kind, k.ToString(), StringComparison.OrdinalIgnoreCase));
                return new IntegrationStatus(
                    k.ToString(),
                    !string.IsNullOrEmpty(s?.ProtectedUrl),
                    !string.IsNullOrEmpty(s?.ProtectedApiKey),
                    NeedsUrl(k),
                    NeedsUsername(k),
                    !string.IsNullOrEmpty(s?.ProtectedUsername),
                    s?.LinkUrl ?? string.Empty);
            })
            .ToList();
    }

    /// <summary>
    /// Saves new values. A <c>null</c> argument leaves that value unchanged, so the
    /// dashboard can update a key without re-entering the URL.
    /// </summary>
    /// <param name="kind">The integration.</param>
    /// <param name="url">New URL, or <c>null</c> to keep the current one.</param>
    /// <param name="apiKey">New API key, or <c>null</c> to keep the current one.</param>
    /// <param name="username">New user name (qBittorrent), or <c>null</c> to keep it.</param>
    /// <param name="linkUrl">New admin link address, or <c>null</c> to keep it (empty removes it).</param>
    public void Set(IntegrationKind kind, string? url, string? apiKey, string? username = null, string? linkUrl = null)
    {
        if (url is not null && url.Length > 0 && !IsValidUrl(url))
        {
            throw new ArgumentException("The address must start with http:// or https://.", nameof(url));
        }

        if (linkUrl is not null && linkUrl.Length > 0 && !IsValidUrl(linkUrl))
        {
            throw new ArgumentException("The link address must start with http:// or https://.", nameof(linkUrl));
        }

        var entry = GetOrCreate(kind);
        if (url is not null)
        {
            entry.ProtectedUrl = _protector.Protect(url.Trim().TrimEnd('/'));
        }

        if (apiKey is not null)
        {
            entry.ProtectedApiKey = _protector.Protect(apiKey.Trim());
        }

        if (username is not null)
        {
            entry.ProtectedUsername = _protector.Protect(username.Trim());
        }

        if (linkUrl is not null)
        {
            entry.LinkUrl = linkUrl.Trim().TrimEnd('/');
        }

        Interlocked.Increment(ref _revision);
        _save();
    }

    /// <summary>
    /// Removes an integration's stored values.
    /// </summary>
    /// <param name="kind">The integration.</param>
    public void Clear(IntegrationKind kind)
    {
        var list = _config().Integrations;
        var existing = list.Where(x => string.Equals(x.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var e in existing)
        {
            list.Remove(e);
        }

        Interlocked.Increment(ref _revision);
        _save();
    }

    /// <summary>
    /// Decrypted values for server-side calls. Internal on purpose: nothing that
    /// builds an HTTP response may call this.
    /// </summary>
    /// <param name="kind">The integration.</param>
    /// <returns>The URL and key, either of which may be <c>null</c>.</returns>
    internal (string? Url, string? ApiKey) Reveal(IntegrationKind kind)
    {
        var (url, key, _) = RevealAll(kind);
        return (url, key);
    }

    /// <summary>
    /// Decrypted URL, key and user name. Internal for the same reason as <see cref="Reveal"/>.
    /// </summary>
    /// <param name="kind">The integration.</param>
    /// <returns>The three values, any of which may be <c>null</c>.</returns>
    internal (string? Url, string? ApiKey, string? Username) RevealAll(IntegrationKind kind)
    {
        var s = Find(kind);
        return (_protector.Unprotect(s?.ProtectedUrl), _protector.Unprotect(s?.ProtectedApiKey), _protector.Unprotect(s?.ProtectedUsername));
    }

    private StoredIntegration? Find(IntegrationKind kind) =>
        _config().Integrations.FirstOrDefault(x => string.Equals(x.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Calls the service's status endpoint with the stored key.
    /// </summary>
    /// <param name="kind">The integration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A result whose message never includes the URL or key.</returns>
    public async Task<IntegrationTestResult> TestAsync(IntegrationKind kind, CancellationToken cancellationToken)
    {
        if (!IsConfigured(kind))
        {
            return new IntegrationTestResult(false, "Not set up yet.");
        }

        if (_http is null)
        {
            return new IntegrationTestResult(false, "Connection tests aren't available here.");
        }

        // Each service's cheapest call that needs a valid key. Seerr's
        // /status answers without one, so its main settings are asked for.
        var path = kind switch
        {
            IntegrationKind.Seerr => "/api/v1/settings/main",
            IntegrationKind.Radarr or IntegrationKind.Sonarr => "/api/v3/system/status",
            IntegrationKind.Lidarr or IntegrationKind.Prowlarr => "/api/v1/system/status",
            IntegrationKind.Bazarr => "/api/system/status",
            IntegrationKind.Shoko => "/api/v3/Init/Version",
            IntegrationKind.Jellystat => "/api/getconfig",
            IntegrationKind.Sabnzbd => "/api?mode=queue&limit=1&output=json",
            IntegrationKind.QBittorrent => "/api/v2/app/version",
            IntegrationKind.Tmdb => "https://api.themoviedb.org/3/configuration",
            IntegrationKind.MdbList => "https://api.mdblist.com/user",
            _ => string.Empty,
        };

        var response = await new ServiceClient(this, _http).SendAsync(kind, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return response.Status switch
        {
            ServiceResponse.TimedOut => new IntegrationTestResult(false, "The service didn't answer within 10 seconds."),
            ServiceResponse.Unreachable => new IntegrationTestResult(false, "Couldn't reach the service from this server. Check the address."),
            401 or 403 => new IntegrationTestResult(false, NeedsUsername(kind) ? "Reached the service, but it rejected the user name or password." : "Reached the service, but it rejected the API key."),
            // SABnzbd answers 200 with {"status":false,"error":"API Key Incorrect"}.
            >= 200 and < 300 when kind == IntegrationKind.Sabnzbd && response.Body.Contains("\"error\"", StringComparison.Ordinal) => new IntegrationTestResult(false, "Reached the service, but it rejected the API key."),
            >= 200 and < 300 => new IntegrationTestResult(true, "Connected."),
            var code => new IntegrationTestResult(false, $"The service answered with an error ({code})."),
        };
    }

    private static bool IsValidUrl(string url)
    {
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
    }

    private StoredIntegration GetOrCreate(IntegrationKind kind)
    {
        var list = _config().Integrations;
        var entry = list.FirstOrDefault(x => string.Equals(x.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new StoredIntegration { Kind = kind.ToString() };
            list.Add(entry);
        }

        return entry;
    }
}
