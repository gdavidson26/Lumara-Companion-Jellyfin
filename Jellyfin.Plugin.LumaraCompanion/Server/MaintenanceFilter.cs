// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.LumaraCompanion.Server;

/// <summary>
/// Keeps non-admins out while a maintenance notice with "only admins" is active.
/// </summary>
/// <remarks>
/// An MVC action filter rather than middleware: it runs after Jellyfin has
/// authenticated the request, so the user's admin role is known without the
/// plugin parsing tokens itself. Admins, API keys and anonymous requests (the
/// sign-in page, public server info) always pass, so an admin can never lock
/// themselves out. Playback reports also pass, so a viewer's resume point is
/// saved when they're stopped. Everyone else gets 503 with the notice, which
/// Lumara apps show as a maintenance screen.
/// </remarks>
public sealed class MaintenanceFilter : IAsyncActionFilter
{
    /// <summary>Path prefixes that are never blocked.</summary>
    private static readonly string[] Exempt =
    [
        "/Lumara/Info",
        "/Lumara/Branding/Logo",
        "/Lumara/Web",
        "/Sessions/Playing",
        "/Sessions/Logout",
        "/System/Info/Public",
        "/Branding",
        "/Users/AuthenticateByName",
        "/QuickConnect",
    ];

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var settings = Plugin.Instance?.Configuration.Maintenance;
        if (settings is null || !settings.LockOut || !settings.Enabled)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var status = Maintenance.Evaluate(settings, DateTime.UtcNow);
        if (!status.LockOut || !ShouldBlock(context.HttpContext))
        {
            await next().ConfigureAwait(false);
            return;
        }

        context.Result = new ObjectResult(new { Maintenance = status })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        };
    }

    /// <summary>
    /// Whether a request should be refused during a lockout.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <returns><c>true</c> for a signed-in non-admin calling a non-exempt route.</returns>
    public static bool ShouldBlock(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true || user.IsInRole("Administrator"))
        {
            return false;
        }

        // API keys belong to the server owner.
        if (string.Equals(user.FindFirst("Jellyfin-IsApiKey")?.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = http.Request.Path.Value ?? string.Empty;
        foreach (var prefix in Exempt)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
