// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.LumaraCompanion.UserData;

/// <summary>
/// One small JSON file per user for data the plugin itself manages (the synced
/// watchlist, request preferences). Unlike <see cref="UserDataStore"/>, clients
/// never write these files directly; they go through endpoints that validate each
/// change.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public sealed class UserJsonStore<T>
    where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _dir;
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="UserJsonStore{T}"/> class.
    /// </summary>
    /// <param name="directory">Folder for the files.</param>
    public UserJsonStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// Reads a user's document; a missing or unreadable file gives a new one.
    /// </summary>
    /// <param name="userId">Jellyfin user id.</param>
    /// <returns>The document.</returns>
    public T Read(Guid userId)
    {
        var path = PathFor(userId);
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return new T();
            }

            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
            }
            catch (JsonException)
            {
                return new T();
            }
        }
    }

    /// <summary>
    /// Changes a user's document under a lock, so two requests can't lose each
    /// other's change.
    /// </summary>
    /// <param name="userId">Jellyfin user id.</param>
    /// <param name="change">Edits the document; returns <c>false</c> to skip saving.</param>
    /// <returns>The document after the change.</returns>
    public T Update(Guid userId, Func<T, bool> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var path = PathFor(userId);
        lock (_gate)
        {
            T doc;
            try
            {
                doc = File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T() : new T();
            }
            catch (JsonException)
            {
                doc = new T();
            }

            if (change(doc))
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Options));
                File.Move(tmp, path, overwrite: true);
            }

            return doc;
        }
    }

    // "N": 32 hex digits, so the name can't form a path.
    private string PathFor(Guid userId) => Path.Combine(_dir, userId.ToString("N") + ".json");
}
