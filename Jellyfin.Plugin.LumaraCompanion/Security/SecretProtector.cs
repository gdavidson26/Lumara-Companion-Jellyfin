// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.LumaraCompanion.Security;

/// <summary>
/// Encrypts integration secrets (URLs and API keys) before they're saved.
/// </summary>
/// <remarks>
/// Uses ASP.NET Core Data Protection with its own key ring in the plugin's data
/// folder (<c>.../plugins/configurations/LumaraCompanion/keys</c>), separate from the
/// plugin's XML config. That way a copied or backed-up config file, a support
/// bundle, or a screenshot of the dashboard never reveals a key. On Windows the
/// key ring itself is additionally protected with DPAPI; on Linux and in Docker it
/// relies on the data folder's file permissions, like any server-side secret.
/// Keys never leave this server, and no API endpoint ever returns a decrypted value.
/// </remarks>
public sealed class SecretProtector
{
    private const string Purpose = "Lumara.Companion.IntegrationSecrets.v1";
    private readonly IDataProtector _protector;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretProtector"/> class.
    /// </summary>
    /// <param name="keysDirectory">Folder that holds the Data Protection key ring.</param>
    public SecretProtector(string keysDirectory)
    {
        Directory.CreateDirectory(keysDirectory);
        var provider = DataProtectionProvider.Create(
            new DirectoryInfo(keysDirectory),
            builder => builder.SetApplicationName("Lumara.Companion"));
        _protector = provider.CreateProtector(Purpose);
    }

    /// <summary>
    /// Encrypts a value. Empty input stays empty (meaning "not configured").
    /// </summary>
    /// <param name="plaintext">The secret.</param>
    /// <returns>Ciphertext safe to store in the config file.</returns>
    public string Protect(string? plaintext)
    {
        return string.IsNullOrEmpty(plaintext) ? string.Empty : _protector.Protect(plaintext);
    }

    /// <summary>
    /// Decrypts a value for server-side use only (never return it to a client).
    /// </summary>
    /// <param name="ciphertext">A value from <see cref="Protect"/>.</param>
    /// <returns>The secret, or <c>null</c> if empty or unreadable (e.g. the key ring was deleted).</returns>
    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            // Key ring lost or value tampered with: treat as not configured, so the
            // admin re-enters it, rather than crashing the request.
            return null;
        }
    }
}
