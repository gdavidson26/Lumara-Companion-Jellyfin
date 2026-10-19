// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using Jellyfin.Plugin.LumaraCompanion.Configuration;

namespace Jellyfin.Plugin.LumaraCompanion.Server;

/// <summary>
/// What a maintenance notice means right now.
/// </summary>
/// <param name="State">"None", "Scheduled" (countdown shown) or "Active".</param>
/// <param name="Message">The admin's message.</param>
/// <param name="StartsUtc">Start time, if set.</param>
/// <param name="EndsUtc">End time, if set.</param>
/// <param name="LockOut">Non-admins are kept out (only ever true while active).</param>
public sealed record MaintenanceStatus(string State, string Message, DateTime? StartsUtc, DateTime? EndsUtc, bool LockOut);

/// <summary>
/// Pure rules for maintenance notices, kept apart from the controller and the
/// lockout filter so both agree and the rules are easy to test.
/// </summary>
public static class Maintenance
{
    /// <summary>Longest message, in characters.</summary>
    public const int MaxMessage = 300;

    /// <summary>Longest countdown lead time: one week.</summary>
    public const int MaxAnnounceMinutes = 7 * 24 * 60;

    private static readonly MaintenanceStatus Off = new("None", string.Empty, null, null, false);

    /// <summary>
    /// Works out the notice's state at <paramref name="nowUtc"/>.
    /// </summary>
    /// <param name="s">The admin's settings.</param>
    /// <param name="nowUtc">Current time (UTC).</param>
    /// <returns>The state to show.</returns>
    public static MaintenanceStatus Evaluate(MaintenanceSettings s, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.Enabled)
        {
            return Off;
        }

        var start = s.StartUtc is { } st ? DateTime.SpecifyKind(st, DateTimeKind.Utc) : (DateTime?)null;
        var end = s.EndUtc is { } en ? DateTime.SpecifyKind(en, DateTimeKind.Utc) : (DateTime?)null;
        if (end is { } e && nowUtc >= e)
        {
            // Finished: nothing to show, even though it's still switched on.
            return Off;
        }

        if (start is { } b && nowUtc < b)
        {
            return b - nowUtc <= TimeSpan.FromMinutes(s.AnnounceMinutes)
                ? new MaintenanceStatus("Scheduled", s.Message, start, end, false)
                : Off;
        }

        return new MaintenanceStatus("Active", s.Message, start, end, s.LockOut);
    }

    /// <summary>
    /// Checks settings from the dashboard and trims the message.
    /// </summary>
    /// <param name="s">The settings (message is trimmed in place).</param>
    /// <param name="error">Plain-English reason when invalid.</param>
    /// <returns><c>true</c> if valid.</returns>
    public static bool Validate(MaintenanceSettings s, out string error)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Message = (s.Message ?? string.Empty).Trim();
        if (s.Message.Length > MaxMessage)
        {
            error = $"The message can be up to {MaxMessage} characters.";
            return false;
        }

        if (s.StartUtc is { } a && s.EndUtc is { } b && b <= a)
        {
            error = "The end has to be after the start.";
            return false;
        }

        if (s.AnnounceMinutes < 0 || s.AnnounceMinutes > MaxAnnounceMinutes)
        {
            error = "The countdown can start up to a week ahead.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
