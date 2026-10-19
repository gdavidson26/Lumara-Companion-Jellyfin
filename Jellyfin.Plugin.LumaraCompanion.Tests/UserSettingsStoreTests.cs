// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using Jellyfin.Plugin.LumaraCompanion.Settings;
using Xunit;

namespace Jellyfin.Plugin.LumaraCompanion.Tests;

public sealed class UserSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumara-settings-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"appearance.theme\":\"dark\",\"home.rows\":[\"resume\",\"next-up\"]}")]
    public void AcceptsJsonObjects(string json)
    {
        Assert.True(UserSettingsStore.Validate(json, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{\"a\":")]
    [InlineData("{} {}")]
    public void RejectsAnythingElse(string json)
    {
        Assert.False(UserSettingsStore.Validate(json, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void RejectsDeepNesting()
    {
        var json = string.Concat(System.Linq.Enumerable.Repeat("{\"a\":", 40)) + "1" + new string('}', 40);
        Assert.False(UserSettingsStore.Validate(json, out _));
    }

    [Fact]
    public void RejectsOversizedDocuments()
    {
        var json = "{\"a\":\"" + new string('x', UserSettingsStore.MaxBytes) + "\"}";
        Assert.False(UserSettingsStore.Validate(json, out _));
    }

    [Fact]
    public void EachUserGetsTheirOwnDocument()
    {
        var store = new UserSettingsStore(_dir);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        Assert.Equal("{}", store.Read(alice));
        store.Write(alice, "{\"theme\":\"oled\"}");
        Assert.Equal("{\"theme\":\"oled\"}", store.Read(alice));
        Assert.Equal("{}", store.Read(bob));

        store.Delete(alice);
        Assert.Equal("{}", store.Read(alice));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void InvalidWritesAreRefused()
    {
        var store = new UserSettingsStore(_dir);
        Assert.Throws<ArgumentException>(() => store.Write(Guid.NewGuid(), "[1,2]"));
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
