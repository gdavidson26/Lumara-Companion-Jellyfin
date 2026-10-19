// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.LumaraCompanion.Web;

/// <summary>
/// Registers <see cref="WebModeMiddleware"/> ahead of Jellyfin's own pipeline.
/// </summary>
/// <remarks>
/// Jellyfin registers plugin services before it builds the web host, so a startup
/// filter from a plugin runs before Jellyfin's <c>Configure</c>: our middleware sees
/// the entry-page request before the static-file handler for <c>/web</c> does.
/// </remarks>
public sealed class WebModeStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<WebModeMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Applies the web mode to requests for the web app's entry page. Everything else
/// passes through untouched.
/// </summary>
public sealed class WebModeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LumaraWebFiles _files;
    private readonly IConfigurationManager _serverConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebModeMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="files">Lumara Web files.</param>
    /// <param name="serverConfig">Jellyfin's configuration, for the base URL.</param>
    public WebModeMiddleware(RequestDelegate next, LumaraWebFiles files, IConfigurationManager serverConfig)
    {
        _next = next;
        _files = files;
        _serverConfig = serverConfig;
    }

    /// <summary>
    /// Handles one request.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var config = Plugin.Instance?.Configuration;
        var path = context.Request.Path.Value ?? string.Empty;
        if (config is null
            || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            || !WebModeDecider.IsEntryPage(path, _serverConfig.GetNetworkConfiguration().BaseUrl, out var basePath))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var decision = WebModeDecider.Decide(
            config.WebMode,
            config.ShowStockButton,
            _files.IsAvailable,
            string.Equals(context.Request.Query["stock"], "1", StringComparison.Ordinal),
            context.Request.Cookies[WebModeDecider.ChoiceCookie]);

        switch (decision)
        {
            case WebDecision.ServeLumara:
                await ServeLumara(context, basePath).ConfigureAwait(false);
                return;
            case WebDecision.StockWithOpenButton:
                await ServeStockWithButton(context, basePath, "open").ConfigureAwait(false);
                return;
            case WebDecision.StockWithBackButton:
                await ServeStockWithButton(context, basePath, "back").ConfigureAwait(false);
                return;
            default:
                await _next(context).ConfigureAwait(false);
                return;
        }
    }

    private async Task ServeLumara(HttpContext context, string basePath)
    {
        var html = _files.IndexHtml(basePath);
        if (html is null)
        {
            // Files vanished between the check and now: fall back to the standard app.
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        LumaraWebHeaders.Apply(context.Response);
        await context.Response.WriteAsync(html, Encoding.UTF8).ConfigureAwait(false);
    }

    /// <summary>
    /// Lets Jellyfin serve its own index.html, then adds the button script.
    /// </summary>
    private async Task ServeStockWithButton(HttpContext context, string basePath, string mode)
    {
        // Ask for an uncompressed, non-cached copy so we can edit it: compression
        // runs further down the pipeline, and a 304 would let the browser reuse a
        // copy made under a different mode.
        context.Request.Headers.AcceptEncoding = string.Empty;
        context.Request.Headers.IfNoneMatch = string.Empty;
        context.Request.Headers.IfModifiedSince = string.Empty;

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType ?? string.Empty).Contains("text/html", StringComparison.OrdinalIgnoreCase);
        if (!isHtml)
        {
            await buffer.CopyToAsync(original).ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(buffer, Encoding.UTF8);
        var html = await reader.ReadToEndAsync().ConfigureAwait(false);
        var edited = LumaraWebFiles.InjectStockButton(html, basePath, mode);
        var bytes = Encoding.UTF8.GetBytes(edited);
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes).ConfigureAwait(false);
    }
}

/// <summary>
/// Security headers for pages Lumara serves.
/// </summary>
public static class LumaraWebHeaders
{
    /// <summary>
    /// Content-Security-Policy for Lumara Web. Hosted Lumara Web only talks to the
    /// server it came from, so everything is limited to 'self'. 'wasm-unsafe-eval'
    /// allows compiling the WebAssembly app; no remote scripts can ever load.
    /// </summary>
    public const string Csp = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'self'; form-action 'self'";

    /// <summary>Adds the headers.</summary>
    /// <param name="response">The response.</param>
    public static void Apply(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = Csp;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
