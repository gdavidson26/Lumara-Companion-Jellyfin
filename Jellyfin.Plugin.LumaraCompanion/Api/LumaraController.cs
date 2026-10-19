// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LumaraCompanion.Configuration;
using Jellyfin.Plugin.LumaraCompanion.Integrations;
using Jellyfin.Plugin.LumaraCompanion.Settings;
using Jellyfin.Plugin.LumaraCompanion.Web;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace Jellyfin.Plugin.LumaraCompanion.Api;

/// <summary>
/// Lumara Companion API, under <c>/Lumara</c>.
/// </summary>
/// <remarks>
/// Rules: signed-in users can read their own settings and the admin defaults;
/// only admins can change web mode, defaults and integrations. No endpoint ever
/// returns a decrypted integration URL or key; the integrations endpoints report
/// only whether each one is set up.
/// </remarks>
[ApiController]
[Route("Lumara")]
public class LumaraController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";
    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateContentTypes();

    private readonly IntegrationService _integrations;
    private readonly UserSettingsStore _settings;
    private readonly LumaraWebFiles _files;

    /// <summary>
    /// Initializes a new instance of the <see cref="LumaraController"/> class.
    /// </summary>
    /// <param name="integrations">Integration settings.</param>
    /// <param name="settings">Per-user settings.</param>
    /// <param name="files">Lumara Web files.</param>
    public LumaraController(IntegrationService integrations, UserSettingsStore settings, LumaraWebFiles files)
    {
        _integrations = integrations;
        _settings = settings;
        _files = files;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Serves Lumara Web's files (anonymous: the app loads before sign-in).
    /// </summary>
    /// <param name="path">File path under /Lumara/Web/.</param>
    /// <returns>The file, or 404 when Lumara Web is off or the file doesn't exist.</returns>
    [HttpGet("Web/{**path}")]
    [HttpGet("Web")]
    [AllowAnonymous]
    public IActionResult WebFile(string? path)
    {
        if (Config.WebMode == WebMode.Off)
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(path) || path == "index.html")
        {
            var basePath = Request.PathBase.Value ?? string.Empty;
            var html = _files.IndexHtml(basePath);
            if (html is null)
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "no-cache";
            LumaraWebHeaders.Apply(Response);
            return Content(html, "text/html; charset=utf-8");
        }

        var full = _files.Resolve(path);
        if (full is null)
        {
            return NotFound();
        }

        if (!ContentTypes.TryGetContentType(full, out var type))
        {
            type = "application/octet-stream";
        }

        // dx gives built assets content-hashed names, so they can be cached for a year.
        Response.Headers.CacheControl = path.Contains("-dxh", StringComparison.Ordinal) ? "public, max-age=31536000, immutable" : "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        return PhysicalFile(full, type);
    }

    /// <summary>
    /// The button script added to the standard web app (part of this GPL plugin).
    /// </summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("StockButton.js")]
    [AllowAnonymous]
    public IActionResult StockButton()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.stock-button.js");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "text/javascript; charset=utf-8");
    }

    /// <summary>
    /// The signed-in user's synced settings and the admin defaults.
    /// </summary>
    /// <returns>{ defaults, user } as JSON objects.</returns>
    [HttpGet("Settings")]
    [Authorize]
    public IActionResult GetSettings()
    {
        if (!TryUserId(out var userId))
        {
            return Unauthorized();
        }

        var body = "{\"defaults\":" + Config.DefaultSettingsJson + ",\"user\":" + _settings.Read(userId) + "}";
        return Content(body, "application/json");
    }

    /// <summary>
    /// Saves the signed-in user's settings.
    /// </summary>
    /// <returns>204 on success, 400 for invalid JSON, 413 if too large.</returns>
    [HttpPut("Settings")]
    [Authorize]
    public async Task<IActionResult> PutSettings()
    {
        if (!TryUserId(out var userId))
        {
            return Unauthorized();
        }

        var (json, tooLarge) = await ReadBody().ConfigureAwait(false);
        if (tooLarge)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!UserSettingsStore.Validate(json, out var error))
        {
            return BadRequest(error);
        }

        _settings.Write(userId, json);
        return NoContent();
    }

    /// <summary>
    /// Deletes the signed-in user's saved settings.
    /// </summary>
    /// <returns>204.</returns>
    [HttpDelete("Settings")]
    [Authorize]
    public IActionResult DeleteSettings()
    {
        if (!TryUserId(out var userId))
        {
            return Unauthorized();
        }

        _settings.Delete(userId);
        return NoContent();
    }

    /// <summary>
    /// Sets the server-wide default Lumara settings (admins).
    /// </summary>
    /// <returns>204, or 400/413 for bad input.</returns>
    [HttpPut("Defaults")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> PutDefaults()
    {
        var (json, tooLarge) = await ReadBody().ConfigureAwait(false);
        if (tooLarge)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!UserSettingsStore.Validate(json, out var error))
        {
            return BadRequest(error);
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        plugin.Configuration.DefaultSettingsJson = json;
        plugin.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Web mode settings (admins).
    /// </summary>
    /// <param name="request">New values.</param>
    /// <returns>204.</returns>
    [HttpPut("Web/Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult PutWebSettings([FromBody] WebSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        plugin.Configuration.WebMode = request.WebMode;
        plugin.Configuration.ShowStockButton = request.ShowStockButton;
        plugin.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Which integrations are set up (admins). Never includes URLs or keys.
    /// </summary>
    /// <returns>Status list.</returns>
    [HttpGet("Integrations")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<IntegrationStatus>> GetIntegrations()
    {
        return Ok(_integrations.Statuses());
    }

    /// <summary>
    /// Saves an integration's URL and/or key (admins). Omitted fields are kept.
    /// </summary>
    /// <param name="kind">Integration name.</param>
    /// <param name="request">New values.</param>
    /// <returns>The updated status (still without the values).</returns>
    [HttpPut("Integrations/{kind}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IntegrationStatus> PutIntegration([FromRoute] IntegrationKind kind, [FromBody] IntegrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            _integrations.Set(kind, request.Url, request.ApiKey, request.Username, request.LinkUrl);
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }

        return Ok(_integrations.Statuses().Find(kind.ToString()));
    }

    /// <summary>
    /// Removes an integration's settings (admins).
    /// </summary>
    /// <param name="kind">Integration name.</param>
    /// <returns>204.</returns>
    [HttpDelete("Integrations/{kind}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult DeleteIntegration([FromRoute] IntegrationKind kind)
    {
        _integrations.Clear(kind);
        return NoContent();
    }

    /// <summary>
    /// Tests an integration's connection from this server (admins).
    /// </summary>
    /// <param name="kind">Integration name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Result without the URL or key.</returns>
    [HttpPost("Integrations/{kind}/Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<IntegrationTestResult>> TestIntegration([FromRoute] IntegrationKind kind, CancellationToken cancellationToken)
    {
        return Ok(await _integrations.TestAsync(kind, cancellationToken).ConfigureAwait(false));
    }

    private static FileExtensionContentTypeProvider CreateContentTypes()
    {
        var p = new FileExtensionContentTypeProvider();
        p.Mappings[".wasm"] = "application/wasm";
        return p;
    }

    private bool TryUserId(out Guid userId)
    {
        var value = User.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out userId) && userId != Guid.Empty;
    }

    private async Task<(string Json, bool TooLarge)> ReadBody()
    {
        using var reader = new StreamReader(Request.Body);
        var buffer = new char[UserSettingsStore.MaxBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory()).ConfigureAwait(false);
        return read > UserSettingsStore.MaxBytes ? (string.Empty, true) : (new string(buffer, 0, read), false);
    }
}

/// <summary>Body of <c>PUT /Lumara/Web/Settings</c>.</summary>
public sealed class WebSettingsRequest
{
    /// <summary>Gets or sets the web mode.</summary>
    public WebMode WebMode { get; set; }

    /// <summary>Gets or sets a value indicating whether the standard app shows a Lumara button.</summary>
    public bool ShowStockButton { get; set; } = true;
}

/// <summary>Body of <c>PUT /Lumara/Integrations/{kind}</c>. Null fields are left unchanged.</summary>
public sealed class IntegrationRequest
{
    /// <summary>Gets or sets the base URL.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the API key (the password, for qBittorrent).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets the user name (qBittorrent).</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the browser address for admin links (empty removes it).</summary>
    public string? LinkUrl { get; set; }
}

/// <summary>Small helpers for status lists.</summary>
internal static class StatusListExtensions
{
    /// <summary>Finds a status by kind name.</summary>
    /// <param name="list">Statuses.</param>
    /// <param name="kind">Kind name.</param>
    /// <returns>The status, or <c>null</c>.</returns>
    public static IntegrationStatus? Find(this IReadOnlyList<IntegrationStatus> list, string kind)
    {
        foreach (var s in list)
        {
            if (string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }

        return null;
    }
}
