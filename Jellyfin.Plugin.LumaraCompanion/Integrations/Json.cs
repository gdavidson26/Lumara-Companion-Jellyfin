// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations;

/// <summary>
/// Lenient readers for the JSON the integrations return. Every service changes
/// field types between versions now and then (a number becomes a string, an
/// object becomes null), so a missing or odd value reads as "unknown" instead of
/// throwing and breaking the whole response.
/// </summary>
internal static class Json
{
    /// <summary>A property, or <c>default</c> when it's missing or the element isn't an object.</summary>
    /// <param name="e">Element.</param>
    /// <param name="name">Property name (exact case).</param>
    /// <returns>The property value.</returns>
    public static JsonElement Get(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    /// <summary>A string property (numbers are converted), or <c>null</c>.</summary>
    /// <param name="e">Element.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The text.</returns>
    public static string? Str(this JsonElement e, string name)
    {
        var v = e.Get(name);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    /// <summary>An integer property (strings that hold a number count), or <c>null</c>.</summary>
    /// <param name="e">Element.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The number.</returns>
    public static long? Long(this JsonElement e, string name)
    {
        var v = e.Get(name);
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.Number => (long)v.GetDouble(),
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    /// <summary>An integer property as <see cref="int"/>, or <c>null</c>.</summary>
    /// <param name="e">Element.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The number.</returns>
    public static int? Int(this JsonElement e, string name) => e.Long(name) is { } n && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;

    /// <summary>A floating-point property, or <c>null</c>.</summary>
    /// <param name="e">Element.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The number.</returns>
    public static double? Num(this JsonElement e, string name)
    {
        var v = e.Get(name);
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    /// <summary>A boolean property, or <paramref name="fallback"/>.</summary>
    /// <param name="e">Element.</param>
    /// <param name="name">Property name.</param>
    /// <param name="fallback">Value when missing.</param>
    /// <returns>The value.</returns>
    public static bool Bool(this JsonElement e, string name, bool fallback = false)
    {
        var v = e.Get(name);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    /// <summary>The items of an array property (empty when it isn't an array).</summary>
    /// <param name="e">Element.</param>
    /// <param name="name">Property name, or <c>null</c> for <paramref name="e"/> itself.</param>
    /// <returns>The items.</returns>
    public static IEnumerable<JsonElement> Arr(this JsonElement e, string? name = null)
    {
        var v = name is null ? e : e.Get(name);
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    /// <summary>
    /// A date/time property parsed as UTC, or <c>null</c>. Accepts full ISO 8601 and
    /// plain dates ("2026-10-20", read as midnight UTC).
    /// </summary>
    /// <param name="e">Element.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The time.</returns>
    public static DateTime? Date(this JsonElement e, string name) => ParseDate(e.Str(name));

    /// <summary>Parses an ISO 8601 date or date-time as UTC.</summary>
    /// <param name="text">Text, may be null.</param>
    /// <returns>The time, or <c>null</c>.</returns>
    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    /// <summary>The four-digit year at the start of a date string, or <c>null</c>.</summary>
    /// <param name="date">"2014-11-05" style text.</param>
    /// <returns>The year.</returns>
    public static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 1800 ? y : null;

    /// <summary>ISO 8601 UTC text for a time ("2026-10-20T19:00:00Z").</summary>
    /// <param name="d">The time.</param>
    /// <returns>The text.</returns>
    public static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
