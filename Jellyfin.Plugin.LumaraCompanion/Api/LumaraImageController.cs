// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LumaraCompanion.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LumaraCompanion.Api;

/// <summary>
/// Serves posters for titles that aren't in the library (TMDB, the *arrs, Shoko)
/// through this server, so Lumara apps never contact those services themselves.
/// Anonymous like Jellyfin's own image routes (an img element can't send the
/// session header); only addresses the plugin signed are served (see
/// <see cref="ImageSigner"/>).
/// </summary>
[ApiController]
[Route("Lumara/Img")]
public class LumaraImageController : ControllerBase
{
    private const int MaxCached = 400;
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, (byte[] Bytes, string Type)> Cache = new();

    private readonly ImageSigner _signer;
    private readonly ServiceClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="LumaraImageController"/> class.
    /// </summary>
    /// <param name="signer">Checks signatures.</param>
    /// <param name="client">Fetches from the services.</param>
    public LumaraImageController(ImageSigner signer, ServiceClient client)
    {
        _signer = signer;
        _client = client;
    }

    /// <summary>
    /// One image.
    /// </summary>
    /// <param name="source">"tmdb", "tvdb", "radarr", "sonarr", "lidarr" or "shoko".</param>
    /// <param name="p">Path at the source.</param>
    /// <param name="s">Signature.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The image, 404 for an unsigned or missing one.</returns>
    [HttpGet("{source}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get([FromRoute] string source, [FromQuery] string p, [FromQuery] string s, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(p) || !_signer.Verify(source, p, s))
        {
            return NotFound();
        }

        var key = source + "\n" + p;
        if (!Cache.TryGetValue(key, out var hit))
        {
            var fetched = source switch
            {
                "tmdb" => await _client.GetBytesAsync(null, "https://image.tmdb.org/t/p" + p, cancellationToken).ConfigureAwait(false),
                "tvdb" => await _client.GetBytesAsync(null, "https://artworks.thetvdb.com" + p, cancellationToken).ConfigureAwait(false),
                "radarr" => await _client.GetBytesAsync(IntegrationKind.Radarr, p, cancellationToken).ConfigureAwait(false),
                "sonarr" => await _client.GetBytesAsync(IntegrationKind.Sonarr, p, cancellationToken).ConfigureAwait(false),
                "lidarr" => await _client.GetBytesAsync(IntegrationKind.Lidarr, p, cancellationToken).ConfigureAwait(false),
                "shoko" => await _client.GetBytesAsync(IntegrationKind.Shoko, p, cancellationToken).ConfigureAwait(false),
                _ => null,
            };
            if (fetched is not { } f || f.Bytes.Length > MaxBytes)
            {
                return NotFound();
            }

            hit = f;
            if (Cache.Count >= MaxCached)
            {
                Cache.Clear();
            }

            Cache[key] = hit;
        }

        // Signed addresses change on restart, so a day of browser caching is safe.
        Response.Headers.CacheControl = "public, max-age=86400";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(hit.Bytes, hit.Type);
    }
}
