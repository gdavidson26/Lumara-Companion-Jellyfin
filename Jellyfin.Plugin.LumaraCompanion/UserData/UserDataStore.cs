// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.LumaraCompanion.UserData;

/// <summary>
/// Per-user lists Lumara keeps on this server so they follow each person from
/// device to device: timeline bookmarks, hidden titles and the shows under
/// Spoiler Guard. Each list is one small JSON document per user. Nothing here
/// is secret and nothing is sent anywhere else.
/// </summary>
public sealed class UserDataStore
{
    /// <summary>Largest accepted document, in bytes.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>The lists a client may read and write.</summary>
    public static readonly IReadOnlyCollection<string> Kinds = new[] { "bookmarks", "hidden", "spoiler" };

    private readonly string _dir;
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDataStore"/> class.
    /// </summary>
    /// <param name="directory">Root folder; each list gets a sub-folder.</param>
    public UserDataStore(string directory)
    {
        _dir = directory;
        foreach (var kind in Kinds)
        {
            Directory.CreateDirectory(Path.Combine(_dir, kind));
        }
    }

    /// <summary>Whether <paramref name="kind"/> names a known list.</summary>
    /// <param name="kind">List name from the request.</param>
    /// <returns><c>true</c> for bookmarks, hidden or spoiler.</returns>
    public static bool IsKind(string kind) => ((ICollection<string>)Kinds).Contains(kind);

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
            error = "This list is too large.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The list must be a JSON object.";
                return false;
            }
        }
        catch (JsonException)
        {
            error = "The list isn't valid JSON.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Reads a user's list (an empty object if they have none yet).
    /// </summary>
    /// <param name="kind">List name (see <see cref="Kinds"/>).</param>
    /// <param name="userId">Jellyfin user id.</param>
    /// <returns>JSON object text.</returns>
    public string Read(string kind, Guid userId)
    {
        var path = PathFor(kind, userId);
        lock (_gate)
        {
            return File.Exists(path) ? File.ReadAllText(path) : "{}";
        }
    }

    /// <summary>
    /// Replaces a user's list after validating it.
    /// </summary>
    /// <param name="kind">List name.</param>
    /// <param name="userId">Jellyfin user id.</param>
    /// <param name="json">JSON object text.</param>
    public void Write(string kind, Guid userId, string json)
    {
        if (!Validate(json, out var error))
        {
            throw new ArgumentException(error, nameof(json));
        }

        var path = PathFor(kind, userId);
        lock (_gate)
        {
            // Temp file then move, so a crash never leaves half a file.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
    }

    private string PathFor(string kind, Guid userId)
    {
        if (!IsKind(kind))
        {
            throw new ArgumentException("Unknown list.", nameof(kind));
        }

        // "N": 32 hex digits, so neither part can form a path.
        return Path.Combine(_dir, kind, userId.ToString("N") + ".json");
    }
}
