// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LumaraCompanion.Configuration;

namespace Jellyfin.Plugin.LumaraCompanion.Server;

/// <summary>
/// Validates branding text and stores the logo file.
/// </summary>
/// <remarks>
/// The logo is served to anyone (the sign-in screen shows it before sign-in),
/// so only PNG, JPEG and WebP are accepted, recognised by their first bytes
/// rather than by name or declared type. SVG is refused on purpose: it can
/// carry script, and the file is served from the server's own origin.
/// </remarks>
public sealed partial class BrandingService
{
    /// <summary>Largest logo, in bytes.</summary>
    public const int MaxLogoBytes = 512 * 1024;

    /// <summary>Longest display name.</summary>
    public const int MaxName = 40;

    /// <summary>Longest sign-in message.</summary>
    public const int MaxMessage = 300;

    private static readonly string[] Extensions = ["png", "jpg", "webp"];

    private readonly string _dir;

    /// <summary>
    /// Initializes a new instance of the <see cref="BrandingService"/> class.
    /// </summary>
    /// <param name="directory">Folder for the logo file.</param>
    public BrandingService(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// Checks branding text from the dashboard, trimming it in place.
    /// </summary>
    /// <param name="b">The settings.</param>
    /// <param name="error">Plain-English reason when invalid.</param>
    /// <returns><c>true</c> if valid.</returns>
    public static bool Validate(BrandingSettings b, out string error)
    {
        ArgumentNullException.ThrowIfNull(b);
        b.Name = (b.Name ?? string.Empty).Trim();
        b.Message = (b.Message ?? string.Empty).Trim();
        b.Accent = (b.Accent ?? string.Empty).Trim().ToUpperInvariant();
        if (b.Name.Length > MaxName)
        {
            error = $"The name can be up to {MaxName} characters.";
            return false;
        }

        if (b.Message.Length > MaxMessage)
        {
            error = $"The message can be up to {MaxMessage} characters.";
            return false;
        }

        if (b.Accent.Length > 0 && !HexColour().IsMatch(b.Accent))
        {
            error = "The accent must be a colour like #F2B66D.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Recognises an image by its first bytes.
    /// </summary>
    /// <param name="data">File contents.</param>
    /// <returns>"png", "jpg" or "webp", or <c>null</c> for anything else.</returns>
    public static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "jpg";
        }

        // RIFF....WEBP
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        return null;
    }

    /// <summary>
    /// Saves a new logo, replacing any old one.
    /// </summary>
    /// <param name="data">File contents.</param>
    /// <param name="error">Plain-English reason when refused.</param>
    /// <returns><c>true</c> if saved.</returns>
    public bool SaveLogo(byte[] data, out string error)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || data.Length > MaxLogoBytes)
        {
            error = "The logo must be an image up to 512 KB.";
            return false;
        }

        var ext = Sniff(data);
        if (ext is null)
        {
            error = "The logo must be a PNG, JPEG or WebP image.";
            return false;
        }

        DeleteLogo();
        var path = Path.Combine(_dir, "logo." + ext);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The stored logo, if any.
    /// </summary>
    /// <returns>Its path and content type, or <c>null</c>.</returns>
    public (string Path, string ContentType)? Logo()
    {
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(_dir, "logo." + ext);
            if (File.Exists(path))
            {
                return (path, ext switch { "png" => "image/png", "jpg" => "image/jpeg", _ => "image/webp" });
            }
        }

        return null;
    }

    /// <summary>
    /// Removes the logo.
    /// </summary>
    public void DeleteLogo()
    {
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(_dir, "logo." + ext);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [GeneratedRegex("^#[0-9A-F]{6}$")]
    private static partial Regex HexColour();
}
