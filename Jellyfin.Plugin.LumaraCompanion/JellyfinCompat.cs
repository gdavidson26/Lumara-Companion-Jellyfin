// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LumaraCompanion;

/// <summary>
/// Calls into Jellyfin whose shape changed between server releases, looked up
/// at run time so one plugin build loads on every patch release.
/// </summary>
/// <remarks>
/// Listing users: 10.11.5 (the build's reference packages) has the property
/// <c>IUserManager.Users</c>, 10.11.11 and 12.x have the method <c>GetUsers()</c>.
/// A build compiled against one fails with <c>MissingMethodException</c> on the
/// other (found on a real 10.11.11 server), so the call is resolved by name.
/// </remarks>
internal static class JellyfinCompat
{
    private static readonly object Gate = new();
    private static Func<IUserManager, IEnumerable<User>>? _users;

    /// <summary>Every user on the server.</summary>
    /// <param name="manager">Jellyfin's user manager.</param>
    /// <returns>The users (empty when neither API exists).</returns>
    public static IReadOnlyList<User> AllUsers(IUserManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        lock (Gate)
        {
            _users ??= Resolve(manager.GetType());
        }

        return _users(manager).ToList();
    }

    private static Func<IUserManager, IEnumerable<User>> Resolve(Type type)
    {
        // The implementation type, plus the interfaces it implements (explicit
        // interface members aren't public on the class).
        var candidates = new[] { type }.Concat(type.GetInterfaces());
        foreach (var t in candidates)
        {
            var method = t.GetMethod("GetUsers", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (method is not null && typeof(IEnumerable<User>).IsAssignableFrom(method.ReturnType))
            {
                return m => (IEnumerable<User>?)method.Invoke(m, null) ?? [];
            }
        }

        foreach (var t in candidates)
        {
            var property = t.GetProperty("Users", BindingFlags.Public | BindingFlags.Instance);
            if (property is not null && typeof(IEnumerable<User>).IsAssignableFrom(property.PropertyType))
            {
                return m => (IEnumerable<User>?)property.GetValue(m) ?? [];
            }
        }

        return _ => [];
    }
}
