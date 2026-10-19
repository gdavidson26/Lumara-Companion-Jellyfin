// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.LumaraCompanion.Settings;

/// <summary>
/// Stores each user's Lumara settings on this server, so they follow the user
/// from device to device. Nothing here is secret (themes, row order...), and
/// nothing is ever sent anywhere else.
/// </summary>
public sealed class UserSettingsStore
{
    /// <summary>Largest accepted settings document, in bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    private readonly string _dir;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSettingsStore"/> class.
    /// </summary>
    /// <param name="directory">Folder for per-user files.</param>
    public UserSettingsStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// Checks that <paramref name="json"/> is a JSON object of acceptable size.
    /// </summary>
    /// <param name="json">The document.</param>
    /// <param name="error">Plain-English reason when invalid.</param>
    /// <returns><c>true</c> if valid.</returns>
    public static bool Validate(string json, out string error)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            error = "Settings are too large.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Settings must be a JSON object.";
                return false;
            }
        }
        catch (JsonException)
        {
            error = "Settings aren't valid JSON.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Reads a user's settings (an empty object if none were saved).
    /// </summary>
    /// <param name="userId">Jellyfin user id.</param>
    /// <returns>JSON object text.</returns>
    public string Read(Guid userId)
    {
        var path = PathFor(userId);
        return File.Exists(path) ? File.ReadAllText(path) : "{}";
    }

    /// <summary>
    /// Saves a user's settings after validating them.
    /// </summary>
    /// <param name="userId">Jellyfin user id.</param>
    /// <param name="json">JSON object text.</param>
    public void Write(Guid userId, string json)
    {
        if (!Validate(json, out var error))
        {
            throw new ArgumentException(error, nameof(json));
        }

        // Write to a temp file and move it into place, so a crash mid-write never
        // leaves a half-written file.
        var path = PathFor(userId);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Deletes a user's saved settings (e.g. "Reset everything" in Lumara).
    /// </summary>
    /// <param name="userId">Jellyfin user id.</param>
    public void Delete(Guid userId)
    {
        var path = PathFor(userId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // "N" format: 32 hex digits, no separators, so the id can't form a path.
    private string PathFor(Guid userId) => Path.Combine(_dir, userId.ToString("N") + ".json");
}
