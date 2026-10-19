// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.LumaraCompanion.UserData;

/// <summary>
/// Reviews people on this server write about titles: a rating out of 10 and an
/// optional few lines. Everyone signed in can read a title's reviews; each
/// person writes and deletes only their own (admins can remove any). Stored
/// as one small JSON file per title.
/// </summary>
public sealed class ReviewStore
{
    /// <summary>Longest review text, in characters.</summary>
    public const int MaxText = 2000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _dir;
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ReviewStore"/> class.
    /// </summary>
    /// <param name="directory">Folder for per-title files.</param>
    public ReviewStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// A title's reviews, newest first.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <returns>The reviews.</returns>
    public IReadOnlyList<Review> List(Guid itemId)
    {
        lock (_gate)
        {
            return Load(itemId).OrderByDescending(r => r.Updated).ToList();
        }
    }

    /// <summary>
    /// Checks a rating and text, trimming the text.
    /// </summary>
    /// <param name="rating">Rating, 1 to 10.</param>
    /// <param name="text">Optional text.</param>
    /// <param name="clean">The trimmed text.</param>
    /// <param name="error">Plain-English reason when invalid.</param>
    /// <returns><c>true</c> if valid.</returns>
    public static bool Validate(int rating, string? text, out string clean, out string error)
    {
        clean = (text ?? string.Empty).Trim();
        if (rating < 1 || rating > 10)
        {
            error = "The rating must be from 1 to 10.";
            return false;
        }

        if (clean.Length > MaxText)
        {
            error = $"Reviews can be up to {MaxText} characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Adds or replaces a person's review of a title.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="userId">Reviewer.</param>
    /// <param name="userName">Reviewer's name at the time of writing.</param>
    /// <param name="rating">Rating, 1 to 10.</param>
    /// <param name="text">Optional text.</param>
    /// <returns>The saved review.</returns>
    public Review Upsert(Guid itemId, Guid userId, string userName, int rating, string? text)
    {
        if (!Validate(rating, text, out var clean, out var error))
        {
            throw new ArgumentException(error);
        }

        var review = new Review
        {
            UserId = userId.ToString("N"),
            UserName = userName,
            Rating = rating,
            Text = clean,
            Updated = DateTime.UtcNow,
        };
        lock (_gate)
        {
            var list = Load(itemId).Where(r => r.UserId != review.UserId).ToList();
            list.Add(review);
            Save(itemId, list);
        }

        return review;
    }

    /// <summary>
    /// Removes a person's review of a title.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="userId">Reviewer.</param>
    /// <returns><c>true</c> if there was one to remove.</returns>
    public bool Remove(Guid itemId, Guid userId)
    {
        var id = userId.ToString("N");
        lock (_gate)
        {
            var list = Load(itemId);
            var kept = list.Where(r => r.UserId != id).ToList();
            if (kept.Count == list.Count)
            {
                return false;
            }

            Save(itemId, kept);
            return true;
        }
    }

    private List<Review> Load(Guid itemId)
    {
        var path = PathFor(itemId);
        if (!File.Exists(path))
        {
            return new List<Review>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<Review>>(File.ReadAllText(path), Json) ?? new List<Review>();
        }
        catch (JsonException)
        {
            // A damaged file shouldn't break the title's page; start over.
            return new List<Review>();
        }
    }

    private void Save(Guid itemId, List<Review> list)
    {
        var path = PathFor(itemId);
        if (list.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private string PathFor(Guid itemId) => Path.Combine(_dir, itemId.ToString("N") + ".json");
}

/// <summary>One person's review of a title.</summary>
public sealed class Review
{
    /// <summary>Gets or sets the reviewer's user id (32 hex digits).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the reviewer's name when they wrote it.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the rating, 1 to 10.</summary>
    public int Rating { get; set; }

    /// <summary>Gets or sets the text (may be empty).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was last saved (UTC).</summary>
    public DateTime Updated { get; set; }
}
