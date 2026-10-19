// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using Jellyfin.Plugin.LumaraCompanion.Configuration;

namespace Jellyfin.Plugin.LumaraCompanion.Web;

/// <summary>
/// What to do with a request for the web app's entry page.
/// </summary>
public enum WebDecision
{
    /// <summary>Leave the request alone (the standard Jellyfin web app, unmodified).</summary>
    PassThrough,

    /// <summary>Serve Lumara Web instead of the standard app.</summary>
    ServeLumara,

    /// <summary>Serve the standard app with an "Open Lumara" button added.</summary>
    StockWithOpenButton,

    /// <summary>Serve the standard app with a "Back to Lumara" button added.</summary>
    StockWithBackButton,
}

/// <summary>
/// The rules for which web UI opens. Pure and side-effect free so every rule is
/// unit-tested (see the Tests project).
/// </summary>
/// <remarks>
/// Safety rules that must always hold:
/// <list type="number">
/// <item>The standard app is always reachable at <c>/web/?stock=1</c>, whatever the mode.</item>
/// <item>If the Lumara Web files are missing, Replace behaves like Off, so a broken
/// install can never lock anyone out of the server.</item>
/// <item>Only the entry page is ever touched; every other path passes through.</item>
/// </list>
/// The per-user choice is a cookie (<c>lumara-ui=stock|lumara</c>) because the entry
/// page loads before sign-in: the server can't know who is asking yet. Lumara Web
/// sets it when someone picks "Use the standard Jellyfin app", and the "Back to
/// Lumara" button clears it.
/// </remarks>
public static class WebModeDecider
{
    /// <summary>Cookie that remembers a browser's choice in Replace mode.</summary>
    public const string ChoiceCookie = "lumara-ui";

    /// <summary>
    /// Whether <paramref name="path"/> is the web app's entry page (<c>/web</c>,
    /// <c>/web/</c> or <c>/web/index.html</c>) under the server's base URL.
    /// </summary>
    /// <remarks>
    /// The prefix must equal the configured base URL. Taking it from the request
    /// instead would let a crafted path like <c>/"&gt;&lt;script&gt;/web/</c> end up
    /// inside the page we serve.
    /// </remarks>
    /// <param name="path">Request path.</param>
    /// <param name="configuredBaseUrl">The server's base URL setting (may be empty, or lack the leading slash).</param>
    /// <param name="basePath">The normalized base URL ("" when none), for building links.</param>
    /// <returns><c>true</c> for an entry-page request.</returns>
    public static bool IsEntryPage(string path, string? configuredBaseUrl, out string basePath)
    {
        basePath = NormalizeBaseUrl(configuredBaseUrl);
        if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = path[basePath.Length..];
        return rest.Equals("/web", StringComparison.OrdinalIgnoreCase)
            || rest.Equals("/web/", StringComparison.OrdinalIgnoreCase)
            || rest.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns Jellyfin's base URL setting into "" or "/name" form.
    /// </summary>
    /// <param name="baseUrl">The setting as stored.</param>
    /// <returns>The normalized prefix.</returns>
    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var b = (baseUrl ?? string.Empty).Trim().Trim('/');
        return b.Length == 0 ? string.Empty : "/" + b;
    }

    /// <summary>
    /// Decides what to serve for an entry-page request.
    /// </summary>
    /// <param name="mode">Configured web mode.</param>
    /// <param name="showStockButton">Whether the standard app gets a Lumara button.</param>
    /// <param name="lumaraAvailable">Whether the Lumara Web files are installed.</param>
    /// <param name="stockRequested">The URL contains <c>stock=1</c>.</param>
    /// <param name="choiceCookie">Value of the <see cref="ChoiceCookie"/> cookie, if any.</param>
    /// <returns>The decision.</returns>
    public static WebDecision Decide(WebMode mode, bool showStockButton, bool lumaraAvailable, bool stockRequested, string? choiceCookie)
    {
        if (!lumaraAvailable)
        {
            return WebDecision.PassThrough;
        }

        switch (mode)
        {
            case WebMode.SideBySide:
                return showStockButton ? WebDecision.StockWithOpenButton : WebDecision.PassThrough;
            case WebMode.Replace:
                var wantsStock = stockRequested || string.Equals(choiceCookie, "stock", StringComparison.Ordinal);
                if (wantsStock)
                {
                    return showStockButton ? WebDecision.StockWithBackButton : WebDecision.PassThrough;
                }

                return WebDecision.ServeLumara;
            default:
                return WebDecision.PassThrough;
        }
    }
}
