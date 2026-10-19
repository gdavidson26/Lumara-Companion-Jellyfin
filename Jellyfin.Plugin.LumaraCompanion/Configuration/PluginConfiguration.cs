// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LumaraCompanion.Configuration;

/// <summary>
/// Which web interface opens at the server address.
/// </summary>
public enum WebMode
{
    /// <summary>Lumara Web isn't served; only server-side features are active.</summary>
    Off = 0,

    /// <summary>Lumara Web at /Lumara/Web/, plus a button in the standard Jellyfin web app.</summary>
    SideBySide = 1,

    /// <summary>Lumara Web opens at the server address; users can switch back to the standard app.</summary>
    Replace = 2,
}

/// <summary>
/// One integration's stored settings. Both values are encrypted with ASP.NET Core
/// Data Protection before they're written here (see <c>SecretProtector</c>), so the
/// plugin's XML config file never contains a readable URL or API key.
/// </summary>
public class StoredIntegration
{
    /// <summary>Gets or sets the integration name (see <c>IntegrationKind</c>).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the encrypted base URL, or empty.</summary>
    public string ProtectedUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the encrypted API key (or password, for qBittorrent), or empty.</summary>
    public string ProtectedApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the encrypted user name (qBittorrent only), or empty.</summary>
    public string ProtectedUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the address admins open the app at in a browser (for "Open in
    /// Sonarr" style links), or empty for no links. Not a secret: the admin enters
    /// it specifically so admins can see it, and it's never shown to anyone else.
    /// It can differ from <see cref="ProtectedUrl"/>, which is often an internal
    /// address only this server can reach.
    /// </summary>
    public string LinkUrl { get; set; } = string.Empty;
}

/// <summary>
/// Plugin configuration (saved by Jellyfin as XML in the plugin configurations folder).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the web mode. Defaults to <see cref="WebMode.Off"/> so installing
    /// the plugin never changes what users see until an admin decides.
    /// </summary>
    public WebMode WebMode { get; set; } = WebMode.Off;

    /// <summary>
    /// Gets or sets a value indicating whether the standard web app shows an
    /// "Open Lumara" button (Side-by-side) or a "Back to Lumara" button (Replace,
    /// for users who switched to the standard app).
    /// </summary>
    public bool ShowStockButton { get; set; } = true;

    /// <summary>
    /// Gets the encrypted integration settings.
    /// </summary>
    public Collection<StoredIntegration> Integrations { get; } = [];

    /// <summary>
    /// Gets or sets the server-wide default Lumara settings as a JSON object, set by
    /// an admin. Lumara apps apply these before each user's own choices.
    /// </summary>
    public string DefaultSettingsJson { get; set; } = "{}";

    /// <summary>
    /// Gets or sets the maintenance notice (see <c>Server.Maintenance</c>).
    /// </summary>
    public MaintenanceSettings Maintenance { get; set; } = new();

    /// <summary>
    /// Gets or sets the server branding shown by Lumara apps.
    /// </summary>
    public BrandingSettings Branding { get; set; } = new();

    /// <summary>
    /// Gets or sets the options for requests, the calendar and the *arr tools.
    /// </summary>
    public RequestSettings Requests { get; set; } = new();
}

/// <summary>
/// Admin choices for Seerr requests and the *arr integrations. None of these are
/// secrets; the keys themselves live in <see cref="PluginConfiguration.Integrations"/>.
/// </summary>
public class RequestSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether finishing the last episode of a
    /// show's newest season requests the next season in Seerr (as that viewer,
    /// so their Seerr permissions and quota apply). Off by default. Each viewer
    /// can still turn it off for themselves.
    /// </summary>
    public bool AutoRequestNextSeason { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether finishing a film that's part of a
    /// collection requests the next released film in that collection.
    /// </summary>
    public bool AutoRequestNextMovie { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the daily task copies Sonarr,
    /// Radarr and Lidarr tags onto the matching titles as Jellyfin tags.
    /// </summary>
    public bool CopyArrTags { get; set; }

    /// <summary>
    /// Gets or sets the prefix put in front of copied tags (so they can be told
    /// apart from tags added by hand, and removed again when the *arr tag goes).
    /// </summary>
    public string TagPrefix { get; set; } = "arr: ";

    /// <summary>
    /// Gets or sets a value indicating whether people without admin rights see
    /// the calendar of upcoming episodes and films.
    /// </summary>
    public bool CalendarForEveryone { get; set; } = true;
}

/// <summary>
/// A maintenance notice set by an admin. Lumara apps show it as a banner (with a
/// countdown before it starts); with <see cref="LockOut"/> on, only admins can
/// use the server while it's active.
/// </summary>
public class MaintenanceSettings
{
    /// <summary>Gets or sets a value indicating whether the notice is switched on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets what people see (plain text, up to 300 characters).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets when maintenance starts (UTC); empty means right away.</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>Gets or sets when maintenance ends (UTC); empty means until switched off.</summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether non-admins are kept out while it's active.</summary>
    public bool LockOut { get; set; }

    /// <summary>Gets or sets how many minutes before the start the countdown banner appears.</summary>
    public int AnnounceMinutes { get; set; } = 60;
}

/// <summary>
/// How Lumara apps present this server: a name, a sign-in message, a logo and an
/// accent colour (one of Lumara's own swatches, so it stays in the design).
/// </summary>
public class BrandingSettings
{
    /// <summary>Gets or sets the display name (up to 40 characters); empty uses the server name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a short message on the sign-in screen (up to 300 characters).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the accent colour as #RRGGBB, or empty for Lumara's default.</summary>
    public string Accent { get; set; } = string.Empty;

    /// <summary>Gets or sets the logo's version; 0 means no logo. Bumped on every upload so apps refetch it.</summary>
    public int LogoVersion { get; set; }
}
