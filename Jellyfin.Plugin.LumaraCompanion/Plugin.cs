// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LumaraCompanion.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LumaraCompanion;

/// <summary>
/// The Lumara Companion plugin.
/// </summary>
/// <remarks>
/// What it does:
/// <list type="bullet">
/// <item>Hosts Lumara Web and decides which web UI opens (Off / Side-by-side / Replace).</item>
/// <item>Stores integration secrets (Seerr, the *arr apps, TMDB, MDBList...) encrypted on this server only.</item>
/// <item>Keeps each user's Lumara settings so they follow them across devices, plus admin defaults.</item>
/// <item>Gateway to Seerr and the *arr apps: requests, discovery, watchlist, calendar, downloads, admin tools.</item>
/// </list>
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Stable plugin id. Never change it: Jellyfin uses it to match updates.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("f71150e9-169e-45da-87d0-68dee97fd2e3");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server paths.</param>
    /// <param name="xmlSerializer">XML serializer for the plugin configuration.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Lumara Companion";

    /// <inheritdoc />
    public override string Description => "Hosts Lumara Web, syncs Lumara settings, brings Seerr requests, the *arr calendar and downloads to Lumara apps, and keeps every integration key on your server.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Gets the running plugin instance (set by Jellyfin when it loads the plugin).
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "LumaraCompanion",
                DisplayName = "Lumara Companion",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            },
        ];
    }
}
