// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using System.Net;

namespace Jellyfin.Plugin.LumaraCompanion.Web;

/// <summary>
/// Locates and prepares the Lumara Web files shipped next to the plugin DLL.
/// </summary>
/// <remarks>
/// Lumara Web is a separate, proprietary program. It's distributed as plain files
/// in a <c>lumara-web</c> folder beside this DLL (never compiled into it), with its
/// own license file, and this plugin only serves those files over HTTP.
/// </remarks>
public sealed class LumaraWebFiles
{
    /// <summary>Path prefix Lumara Web is built for (dx <c>--base-path</c>).</summary>
    public const string MountPath = "/Lumara/Web/";

    /// <summary>
    /// Initializes a new instance of the <see cref="LumaraWebFiles"/> class.
    /// </summary>
    /// <param name="root">Folder containing Lumara Web's <c>index.html</c>.</param>
    public LumaraWebFiles(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>Gets the folder with the Lumara Web files.</summary>
    public string Root { get; }

    /// <summary>Gets a value indicating whether Lumara Web is installed.</summary>
    public bool IsAvailable => File.Exists(Path.Combine(Root, "index.html"));

    /// <summary>
    /// Default location: the <c>lumara-web</c> folder beside the plugin DLL.
    /// </summary>
    /// <returns>The files helper.</returns>
    public static LumaraWebFiles Default()
    {
        var dir = Path.GetDirectoryName(typeof(LumaraWebFiles).Assembly.Location) ?? AppContext.BaseDirectory;
        return new LumaraWebFiles(Path.Combine(dir, "lumara-web"));
    }

    /// <summary>
    /// Resolves a request path under <see cref="MountPath"/> to a file, refusing
    /// anything that escapes the folder (<c>..</c>, absolute paths).
    /// </summary>
    /// <param name="relative">Path after <c>/Lumara/Web/</c>.</param>
    /// <returns>Full file path, or <c>null</c> if invalid or missing.</returns>
    public string? Resolve(string relative)
    {
        var clean = string.IsNullOrEmpty(relative) ? "index.html" : relative.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(Root, clean));
        var rootWithSep = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal) || !File.Exists(full))
        {
            return null;
        }

        return full;
    }

    /// <summary>
    /// Returns Lumara Web's <c>index.html</c> adjusted for this server: asset URLs
    /// get the server's base URL, and a meta tag tells the app which server it's
    /// hosted by, so it can skip the "Connect a server" step.
    /// </summary>
    /// <param name="basePath">Server base URL prefix ("" or e.g. "/jellyfin").</param>
    /// <returns>The HTML, or <c>null</c> if Lumara Web isn't installed.</returns>
    public string? IndexHtml(string basePath)
    {
        var path = Path.Combine(Root, "index.html");
        if (!File.Exists(path))
        {
            return null;
        }

        var html = File.ReadAllText(path);
        return RewriteIndex(html, basePath);
    }

    /// <summary>
    /// Pure rewrite used by <see cref="IndexHtml"/> (unit-tested).
    /// </summary>
    /// <param name="html">Original index.html.</param>
    /// <param name="basePath">Server base URL prefix.</param>
    /// <returns>Adjusted HTML.</returns>
    public static string RewriteIndex(string html, string basePath)
    {
        // Encoded even though callers pass the configured base URL, never request text.
        var b = WebUtility.HtmlEncode(basePath.TrimEnd('/'));
        if (b.Length > 0)
        {
            html = html.Replace("\"" + MountPath, "\"" + b + MountPath, StringComparison.Ordinal)
                       .Replace("'" + MountPath, "'" + b + MountPath, StringComparison.Ordinal);
        }

        var meta = "<meta name=\"lumara-server\" content=\"" + b + "\">";
        var i = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? html.Insert(i, meta) : meta + html;
    }

    /// <summary>
    /// Adds the Lumara button script to the standard web app's index.html.
    /// </summary>
    /// <param name="html">Original standard index.html.</param>
    /// <param name="basePath">Server base URL prefix.</param>
    /// <param name="mode">"open" (Side-by-side) or "back" (Replace, stock chosen).</param>
    /// <returns>Adjusted HTML.</returns>
    public static string InjectStockButton(string html, string basePath, string mode)
    {
        var b = WebUtility.HtmlEncode(basePath.TrimEnd('/'));
        var tag = "<script src=\"" + b + "/Lumara/StockButton.js\" data-lumara-mode=\"" + mode + "\" data-lumara-base=\"" + b + "\" defer></script>";
        var i = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? html.Insert(i, tag) : html + tag;
    }
}
