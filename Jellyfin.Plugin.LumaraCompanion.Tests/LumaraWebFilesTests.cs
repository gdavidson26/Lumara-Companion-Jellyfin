// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using Jellyfin.Plugin.LumaraCompanion.Web;
using Xunit;

namespace Jellyfin.Plugin.LumaraCompanion.Tests;

public sealed class LumaraWebFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumara-web-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LumaraWebFiles _files;

    public LumaraWebFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "web", "assets"));
        File.WriteAllText(Path.Combine(_dir, "web", "index.html"), "<html><head></head><body></body></html>");
        File.WriteAllText(Path.Combine(_dir, "web", "assets", "app-dxh1234.js"), "//");
        File.WriteAllText(Path.Combine(_dir, "secret.txt"), "outside the web root");

        // A sibling whose name starts with the root's name: "web" vs "web-private".
        Directory.CreateDirectory(Path.Combine(_dir, "web-private"));
        File.WriteAllText(Path.Combine(_dir, "web-private", "x.txt"), "also outside");
        _files = new LumaraWebFiles(Path.Combine(_dir, "web"));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ServesFilesInsideTheRoot()
    {
        Assert.True(_files.IsAvailable);
        Assert.NotNull(_files.Resolve("assets/app-dxh1234.js"));
        Assert.NotNull(_files.Resolve("/assets/app-dxh1234.js"));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("assets/../../secret.txt")]
    [InlineData("../web-private/x.txt")]
    [InlineData("assets/missing.js")]
    [InlineData("assets")]
    public void RefusesTraversalFoldersAndMissingFiles(string path)
    {
        Assert.Null(_files.Resolve(path));
    }

    [Fact]
    public void AbsolutePathsOutsideTheRootAreRefused()
    {
        Assert.Null(_files.Resolve(Path.Combine(_dir, "secret.txt")));
    }

    [Fact]
    public void MissingFolderMeansNotAvailable()
    {
        Assert.False(new LumaraWebFiles(Path.Combine(_dir, "nope")).IsAvailable);
    }

    [Fact]
    public void IndexGetsTheServerMetaAndBasePrefix()
    {
        var html = "<html><head><script src=\"/Lumara/Web/app.js\"></script></head><body></body></html>";
        var rewritten = LumaraWebFiles.RewriteIndex(html, "/jellyfin");
        Assert.Contains("src=\"/jellyfin/Lumara/Web/app.js\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"lumara-server\" content=\"/jellyfin\"></head>", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexWithoutBaseKeepsPaths()
    {
        var html = "<html><head><script src=\"/Lumara/Web/app.js\"></script></head></html>";
        var rewritten = LumaraWebFiles.RewriteIndex(html, string.Empty);
        Assert.Contains("src=\"/Lumara/Web/app.js\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"lumara-server\" content=\"\">", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void BasePathIsEscapedInTheIndex()
    {
        var rewritten = LumaraWebFiles.RewriteIndex("<head></head>", "/a\"><script>x</script>");
        Assert.DoesNotContain("<script>x", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void StockButtonScriptGoesBeforeBodyEnd()
    {
        var html = LumaraWebFiles.InjectStockButton("<html><body><div></div></body></html>", "/jf/", "open");
        Assert.EndsWith(
            "<script src=\"/jf/Lumara/StockButton.js\" data-lumara-mode=\"open\" data-lumara-base=\"/jf\" defer></script></body></html>",
            html,
            StringComparison.Ordinal);
    }
}
