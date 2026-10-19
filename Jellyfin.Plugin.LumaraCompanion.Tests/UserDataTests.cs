// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using Jellyfin.Plugin.LumaraCompanion.UserData;
using Xunit;

namespace Jellyfin.Plugin.LumaraCompanion.Tests;

public sealed class UserDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumara-userdata-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Theory]
    [InlineData("bookmarks", true)]
    [InlineData("hidden", true)]
    [InlineData("spoiler", true)]
    [InlineData("settings", false)]
    [InlineData("../bookmarks", false)]
    [InlineData("Bookmarks", false)]
    public void OnlyKnownListsAreAccepted(string kind, bool ok)
    {
        Assert.Equal(ok, UserDataStore.IsKind(kind));
    }

    [Fact]
    public void ListsArePerUserAndPerKind()
    {
        var store = new UserDataStore(_dir);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.Write("hidden", a, "{\"x\":1}");
        store.Write("bookmarks", a, "{\"y\":2}");

        Assert.Equal("{\"x\":1}", store.Read("hidden", a));
        Assert.Equal("{\"y\":2}", store.Read("bookmarks", a));
        Assert.Equal("{}", store.Read("hidden", b));
        Assert.Equal("{}", store.Read("spoiler", a));
    }

    [Fact]
    public void UnknownListThrows()
    {
        var store = new UserDataStore(_dir);
        Assert.Throws<ArgumentException>(() => store.Read("..", Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => store.Write("users", Guid.NewGuid(), "{}"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("nope")]
    [InlineData("")]
    public void InvalidListsAreRejected(string json)
    {
        Assert.False(UserDataStore.Validate(json, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void OversizedListIsRejected()
    {
        var json = "{\"a\":\"" + new string('x', UserDataStore.MaxBytes) + "\"}";
        Assert.False(UserDataStore.Validate(json, out _));
    }

    [Theory]
    [InlineData(0, "fine", false)]
    [InlineData(11, "fine", false)]
    [InlineData(1, null, true)]
    [InlineData(10, "  great  ", true)]
    public void ReviewRatingAndTextAreChecked(int rating, string? text, bool ok)
    {
        Assert.Equal(ok, ReviewStore.Validate(rating, text, out _, out _));
    }

    [Fact]
    public void OverlongReviewIsRejected()
    {
        Assert.False(ReviewStore.Validate(5, new string('a', ReviewStore.MaxText + 1), out _, out _));
    }

    [Fact]
    public void OneReviewPerPersonPerTitle()
    {
        var store = new ReviewStore(_dir);
        var item = Guid.NewGuid();
        var me = Guid.NewGuid();
        var you = Guid.NewGuid();

        store.Upsert(item, me, "me", 6, "ok");
        store.Upsert(item, you, "you", 9, null);
        var mine = store.Upsert(item, me, "me", 8, "  better on rewatch ");

        var list = store.List(item);
        Assert.Equal(2, list.Count);
        Assert.Equal("better on rewatch", mine.Text);
        Assert.Equal(me.ToString("N"), list[0].UserId); // newest first
        Assert.Equal(8, list[0].Rating);
        Assert.Empty(store.List(Guid.NewGuid()));
    }

    [Fact]
    public void RemovingTheLastReviewRemovesTheFile()
    {
        var store = new ReviewStore(_dir);
        var item = Guid.NewGuid();
        var me = Guid.NewGuid();
        store.Upsert(item, me, "me", 7, null);

        Assert.False(store.Remove(item, Guid.NewGuid()));
        Assert.True(store.Remove(item, me));
        Assert.Empty(store.List(item));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void DamagedReviewFileReadsAsEmpty()
    {
        var store = new ReviewStore(_dir);
        var item = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, item.ToString("N") + ".json"), "{broken");
        Assert.Empty(store.List(item));
    }
}
