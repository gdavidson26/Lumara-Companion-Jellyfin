// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations;

/// <summary>
/// What a service answered. <see cref="Status"/> is the HTTP status, or one of the
/// negative constants when there was no answer at all.
/// </summary>
/// <param name="Status">HTTP status, or <see cref="Unreachable"/>, <see cref="TimedOut"/> or <see cref="NotConfigured"/>.</param>
/// <param name="Body">Response text (empty when there was none).</param>
public sealed record ServiceResponse(int Status, string Body)
{
    /// <summary>The connection failed (wrong address, service down).</summary>
    public const int Unreachable = -1;

    /// <summary>No answer within the time limit.</summary>
    public const int TimedOut = -2;

    /// <summary>The integration isn't set up.</summary>
    public const int NotConfigured = -3;

    /// <summary>Gets a value indicating whether the status is 2xx.</summary>
    public bool Ok => Status is >= 200 and < 300;

    /// <summary>
    /// Parses the body as JSON.
    /// </summary>
    /// <returns>The document, or <c>null</c> for a failed call or a body that isn't JSON. Dispose it.</returns>
    public JsonDocument? Json()
    {
        if (!Ok || Body.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(Body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The one place the plugin calls integrations from. It adds each service's
/// credentials the way that service expects them, applies a time limit, and turns
/// every failure into a <see cref="ServiceResponse"/> instead of an exception.
/// </summary>
/// <remarks>
/// Rules that keep secrets on the server:
/// <list type="bullet">
/// <item>Credentials are decrypted here, per call, and never stored elsewhere.</item>
/// <item>Nothing here logs. Several services want the key in the URL (SABnzbd,
/// MDBList, TMDB v3), so a logged URL would leak it. That's also why the
/// <see cref="HttpClient"/> is a plain one rather than an IHttpClientFactory
/// client, which logs every request URL.</item>
/// <item>Responses are returned to callers inside the plugin, which reshape them
/// before anything reaches a client; raw bodies are never passed through.</item>
/// </list>
/// </remarks>
public sealed class ServiceClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // qBittorrent session cookies, keyed by base URL + user name. qBittorrent has
    // no API keys: the plugin signs in once and reuses the SID until it expires.
    private static readonly ConcurrentDictionary<string, string> QbSessions = new();

    private readonly IntegrationService _integrations;
    private readonly HttpClient? _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceClient"/> class.
    /// </summary>
    /// <param name="integrations">Holds the encrypted addresses and keys.</param>
    /// <param name="http">HTTP client without cookie handling (null in tests that don't call out).</param>
    public ServiceClient(IntegrationService integrations, HttpClient? http)
    {
        _integrations = integrations;
        _http = http;
    }

    /// <summary>Gets the integration settings (for checks like <see cref="IntegrationService.IsConfigured"/>).</summary>
    public IntegrationService Integrations => _integrations;

    /// <summary>
    /// Calls a service.
    /// </summary>
    /// <param name="kind">Which service.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Path and query under the service's address, or an absolute URL for TMDB/MDBList.</param>
    /// <param name="jsonBody">JSON request body, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="headers">Extra headers (e.g. Seerr's X-Api-User).</param>
    /// <returns>The answer; never throws for network problems.</returns>
    public async Task<ServiceResponse> SendAsync(
        IntegrationKind kind,
        HttpMethod method,
        string path,
        string? jsonBody,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        if (_http is null || !_integrations.IsConfigured(kind))
        {
            return new ServiceResponse(ServiceResponse.NotConfigured, string.Empty);
        }

        var (url, key, user) = _integrations.RevealAll(kind);
        var first = await SendOnce(kind, method, url ?? string.Empty, key!, user, path, jsonBody, headers, cancellationToken).ConfigureAwait(false);
        if (kind == IntegrationKind.QBittorrent && first.Status == 403)
        {
            // The saved session expired: sign in again and retry once.
            QbSessions.TryRemove(QbSessionKey(url!, user!, _integrations.Revision), out _);
            return await SendOnce(kind, method, url!, key!, user, path, jsonBody, headers, cancellationToken).ConfigureAwait(false);
        }

        return first;
    }

    /// <summary>
    /// Fetches binary content (images) from a service.
    /// </summary>
    /// <param name="kind">Which service, or <c>null</c> for a public URL (TMDB's image CDN).</param>
    /// <param name="pathOrUrl">Path under the service, or the absolute public URL.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The bytes and content type, or <c>null</c> on any failure.</returns>
    public async Task<(byte[] Bytes, string ContentType)?> GetBytesAsync(IntegrationKind? kind, string pathOrUrl, CancellationToken cancellationToken)
    {
        if (_http is null)
        {
            return null;
        }

        string target;
        string? header = null;
        string? key = null;
        if (kind is { } k)
        {
            if (!_integrations.IsConfigured(k))
            {
                return null;
            }

            var (url, apiKey, _) = _integrations.RevealAll(k);
            target = url + pathOrUrl;
            header = KeyHeader(k);
            key = apiKey;
        }
        else
        {
            target = pathOrUrl;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (header is not null && key is not null)
            {
                request.Headers.TryAddWithoutValidation(header, key);
            }

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!response.IsSuccessStatusCode || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            return (bytes, type);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The header a service reads its API key from, or <c>null</c> when the key
    /// goes elsewhere (query string, bearer token, session cookie).
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <returns>Header name.</returns>
    public static string? KeyHeader(IntegrationKind kind) => kind switch
    {
        IntegrationKind.Seerr or IntegrationKind.Radarr or IntegrationKind.Sonarr
            or IntegrationKind.Lidarr or IntegrationKind.Prowlarr => "X-Api-Key",
        IntegrationKind.Bazarr => "X-API-KEY",
        IntegrationKind.Shoko => "apikey",
        IntegrationKind.Jellystat => "x-api-token",
        _ => null,
    };

    // The settings revision is part of the key: a new password (or address)
    // signs in again instead of reusing the old session.
    private static string QbSessionKey(string url, string user, int revision) => url + "\n" + user + "\n" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string AppendQuery(string target, string name, string value) =>
        target + (target.Contains('?', StringComparison.Ordinal) ? "&" : "?") + name + "=" + Uri.EscapeDataString(value);

    private async Task<ServiceResponse> SendOnce(
        IntegrationKind kind,
        HttpMethod method,
        string url,
        string key,
        string? user,
        string path,
        string? jsonBody,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var target = path.StartsWith("http", StringComparison.Ordinal) ? path : url + path;
        string? cookie = null;
        switch (kind)
        {
            case IntegrationKind.Sabnzbd:
                target = AppendQuery(target, "apikey", key);
                break;
            case IntegrationKind.MdbList:
                target = AppendQuery(target, "apikey", key);
                break;
            case IntegrationKind.Tmdb when !key.StartsWith("eyJ", StringComparison.Ordinal):
                // A v3 key goes in the query; a v4 read token ("eyJ...") is a bearer header.
                target = AppendQuery(target, "api_key", key);
                break;
            case IntegrationKind.QBittorrent:
                cookie = await QbSession(url, user ?? string.Empty, key, cancellationToken).ConfigureAwait(false);
                if (cookie is null)
                {
                    return new ServiceResponse(403, string.Empty);
                }

                break;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(method, target);
            if (KeyHeader(kind) is { } header)
            {
                request.Headers.TryAddWithoutValidation(header, key);
            }
            else if (kind == IntegrationKind.Tmdb)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            }

            if (cookie is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", "SID=" + cookie);
                // qBittorrent's CSRF check compares Referer/Origin with its own host.
                request.Headers.TryAddWithoutValidation("Referer", url);
            }

            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await _http!.SendAsync(request, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return new ServiceResponse((int)response.StatusCode, body);
        }
        catch (HttpRequestException)
        {
            return new ServiceResponse(ServiceResponse.Unreachable, string.Empty);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServiceResponse(ServiceResponse.TimedOut, string.Empty);
        }
        catch (InvalidOperationException)
        {
            // A malformed stored address (shouldn't pass validation, but never crash a request).
            return new ServiceResponse(ServiceResponse.Unreachable, string.Empty);
        }
    }

    // Signs in to qBittorrent (POST /api/v2/auth/login, form fields). A wrong
    // password still answers 200, with the body "Fails.", so the cookie is the test.
    private async Task<string?> QbSession(string url, string user, string password, CancellationToken cancellationToken)
    {
        var cacheKey = QbSessionKey(url, user, _integrations.Revision);
        if (QbSessions.TryGetValue(cacheKey, out var sid))
        {
            return sid;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, url + "/api/v2/auth/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user, ["password"] = password }),
            };
            request.Headers.TryAddWithoutValidation("Referer", url);
            using var response = await _http!.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                return null;
            }

            var value = cookies
                .Select(c => c.Split(';')[0].Trim())
                .Where(c => c.StartsWith("SID=", StringComparison.Ordinal))
                .Select(c => c[4..])
                .FirstOrDefault();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            QbSessions[cacheKey] = value;
            return value;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }
}
