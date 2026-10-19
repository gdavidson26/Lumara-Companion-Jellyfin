// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LumaraCompanion.Configuration;
using Jellyfin.Plugin.LumaraCompanion.Integrations;
using Jellyfin.Plugin.LumaraCompanion.Server;
using Jellyfin.Plugin.LumaraCompanion.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LumaraCompanion.Api;

/// <summary>
/// Server-wide things under <c>/Lumara</c>: the public status Lumara apps read
/// first (plugin version, maintenance notice, branding), the admin endpoints that
/// change maintenance and branding, and ratings fetched on viewers' behalf.
/// </summary>
[ApiController]
[Route("Lumara")]
public class LumaraServerController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly LumaraWebFiles _files;
    private readonly BrandingService _branding;
    private readonly RatingsService _ratings;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IServerConfigurationManager _serverConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="LumaraServerController"/> class.
    /// </summary>
    /// <param name="files">Lumara Web files (for the status).</param>
    /// <param name="branding">Branding validation and the logo file.</param>
    /// <param name="ratings">MDBList/TMDB gateway.</param>
    /// <param name="library">Jellyfin's library, to look up a title's ids.</param>
    /// <param name="users">Jellyfin's users, to check a viewer may see a title.</param>
    /// <param name="serverConfig">Server settings (metadata country for availability).</param>
    public LumaraServerController(
        LumaraWebFiles files,
        BrandingService branding,
        RatingsService ratings,
        ILibraryManager library,
        IUserManager users,
        IServerConfigurationManager serverConfig)
    {
        _files = files;
        _branding = branding;
        _ratings = ratings;
        _library = library;
        _users = users;
        _serverConfig = serverConfig;
    }

    private static Plugin? PluginInstance => Plugin.Instance;

    private static PluginConfiguration Config => PluginInstance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Public facts Lumara apps read before and after sign-in: version, web mode,
    /// the maintenance notice and branding. Anonymous on purpose: the sign-in
    /// screen shows the branding and any maintenance notice. Holds no secrets.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Info")]
    [AllowAnonymous]
    public ActionResult<object> Info()
    {
        var c = Config;
        Response.Headers.CacheControl = "no-cache";
        return new
        {
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            WebMode = c.WebMode.ToString(),
            WebAvailable = _files.IsAvailable,
            Maintenance = Maintenance.Evaluate(c.Maintenance, DateTime.UtcNow),
            Branding = new
            {
                c.Branding.Name,
                c.Branding.Message,
                c.Branding.Accent,
                LogoVersion = _branding.Logo() is null ? 0 : Math.Max(1, c.Branding.LogoVersion),
            },
        };
    }

    /// <summary>
    /// Replaces the maintenance notice (admins only).
    /// </summary>
    /// <param name="settings">The new notice.</param>
    /// <returns>The resulting state, or 400 with the reason.</returns>
    [HttpPut("Maintenance")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<MaintenanceStatus> PutMaintenance([FromBody] MaintenanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Maintenance.Validate(settings, out var error))
        {
            return BadRequest(error);
        }

        var plugin = PluginInstance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        plugin.Configuration.Maintenance = settings;
        plugin.SaveConfiguration();
        return Maintenance.Evaluate(settings, DateTime.UtcNow);
    }

    /// <summary>
    /// Replaces the branding text and accent (admins only). The logo has its own endpoint.
    /// </summary>
    /// <param name="branding">Name, message and accent.</param>
    /// <returns>204, or 400 with the reason.</returns>
    [HttpPut("Branding")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult PutBranding([FromBody] BrandingSettings branding)
    {
        ArgumentNullException.ThrowIfNull(branding);
        if (!BrandingService.Validate(branding, out var error))
        {
            return BadRequest(error);
        }

        var plugin = PluginInstance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // The logo version belongs to the logo endpoints; keep it.
        branding.LogoVersion = plugin.Configuration.Branding.LogoVersion;
        plugin.Configuration.Branding = branding;
        plugin.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// The server's logo (anonymous: shown on the sign-in screen).
    /// </summary>
    /// <returns>The image, or 404.</returns>
    [HttpGet("Branding/Logo")]
    [AllowAnonymous]
    public IActionResult GetLogo()
    {
        var logo = _branding.Logo();
        if (logo is null)
        {
            return NotFound();
        }

        // Apps add ?v=<LogoVersion>, so a new upload gets a new address.
        Response.Headers.CacheControl = "public, max-age=86400";
        Response.Headers.XContentTypeOptions = "nosniff";
        return PhysicalFile(logo.Value.Path, logo.Value.ContentType);
    }

    /// <summary>
    /// Uploads a new logo (admins only): the raw PNG, JPEG or WebP bytes, up to 512 KB.
    /// </summary>
    /// <returns>204, 400 for other files, 413 when too large.</returns>
    [HttpPut("Branding/Logo")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> PutLogo()
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > BrandingService.MaxLogoBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
        }

        if (!_branding.SaveLogo(buffer.ToArray(), out var error))
        {
            return BadRequest(error);
        }

        var plugin = PluginInstance;
        if (plugin is not null)
        {
            plugin.Configuration.Branding.LogoVersion++;
            plugin.SaveConfiguration();
        }

        return NoContent();
    }

    /// <summary>
    /// Removes the logo (admins only).
    /// </summary>
    /// <returns>204.</returns>
    [HttpDelete("Branding/Logo")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult DeleteLogo()
    {
        _branding.DeleteLogo();
        return NoContent();
    }

    /// <summary>
    /// Scores (MDBList, TMDB) and where to watch (TMDB) for a film or show the
    /// signed-in user can see. Episodes and seasons use their show.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The ratings, or 404 for items the user can't see.</returns>
    [HttpGet("Ratings/{itemId}")]
    [Authorize]
    public async Task<ActionResult<RatingsResult>> GetRatings([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(UserIdClaim)?.Value;
        var user = Guid.TryParse(userId, out var uid) ? _users.GetUserById(uid) : null;
        var item = _library.GetItemById(itemId);
        if (user is null || item is null || !item.IsVisible(user))
        {
            return NotFound();
        }

        // Ratings exist for whole shows, not single episodes or seasons.
        var title = item switch
        {
            Episode e => (MediaBrowser.Controller.Entities.BaseItem?)e.Series,
            Season s => s.Series,
            _ => item,
        };
        if (title is null)
        {
            return new RatingsResult(false, [], null);
        }

        var tmdb = title.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        var region = _serverConfig.Configuration.MetadataCountryCode ?? "US";
        return await _ratings.GetAsync(tmdb, title is Series, region, cancellationToken).ConfigureAwait(false);
    }
}
