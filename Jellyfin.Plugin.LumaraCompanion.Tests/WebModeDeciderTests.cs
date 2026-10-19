// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using Jellyfin.Plugin.LumaraCompanion.Configuration;
using Jellyfin.Plugin.LumaraCompanion.Web;
using Xunit;

namespace Jellyfin.Plugin.LumaraCompanion.Tests;

public class WebModeDeciderTests
{
    [Theory]
    [InlineData("/web", "", "")]
    [InlineData("/web/", "", "")]
    [InlineData("/web/index.html", "", "")]
    [InlineData("/WEB/", "", "")]
    [InlineData("/jellyfin/web/", "jellyfin/", "/jellyfin")]
    [InlineData("/Jellyfin/web/index.html", "/jellyfin", "/jellyfin")]
    public void EntryPagesUnderTheConfiguredBaseMatch(string path, string baseUrl, string expectedBase)
    {
        Assert.True(WebModeDecider.IsEntryPage(path, baseUrl, out var basePath));
        Assert.Equal(expectedBase, basePath);
    }

    [Theory]
    [InlineData("/web/main.bundle.js", "")]
    [InlineData("/web/index.html.map", "")]
    [InlineData("/Items", "")]
    [InlineData("/other/web/", "")]
    [InlineData("/\"><script>alert(1)</script>/web/", "")]
    [InlineData("/web/", "/jellyfin")]
    [InlineData("/jellyfinx/web/", "/jellyfin")]
    public void AnythingElsePassesThrough(string path, string baseUrl)
    {
        Assert.False(WebModeDecider.IsEntryPage(path, baseUrl, out _));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("jellyfin", "/jellyfin")]
    [InlineData("/jellyfin/", "/jellyfin")]
    public void BaseUrlIsNormalized(string? input, string expected)
    {
        Assert.Equal(expected, WebModeDecider.NormalizeBaseUrl(input));
    }

    [Theory]
    [InlineData(WebMode.Off)]
    [InlineData(WebMode.SideBySide)]
    [InlineData(WebMode.Replace)]
    public void MissingBundleAlwaysFallsBackToTheStandardApp(WebMode mode)
    {
        Assert.Equal(WebDecision.PassThrough, WebModeDecider.Decide(mode, true, false, false, null));
    }

    [Fact]
    public void OffNeverTouchesTheStandardApp()
    {
        Assert.Equal(WebDecision.PassThrough, WebModeDecider.Decide(WebMode.Off, true, true, false, "lumara"));
    }

    [Fact]
    public void SideBySideAddsTheOpenButtonOnlyWhenEnabled()
    {
        Assert.Equal(WebDecision.StockWithOpenButton, WebModeDecider.Decide(WebMode.SideBySide, true, true, false, null));
        Assert.Equal(WebDecision.PassThrough, WebModeDecider.Decide(WebMode.SideBySide, false, true, false, null));
    }

    [Fact]
    public void ReplaceServesLumaraByDefault()
    {
        Assert.Equal(WebDecision.ServeLumara, WebModeDecider.Decide(WebMode.Replace, true, true, false, null));
        Assert.Equal(WebDecision.ServeLumara, WebModeDecider.Decide(WebMode.Replace, true, true, false, "lumara"));
    }

    // ?stock=1 wins over a "lumara" cookie, so the fallback URL works no matter what.
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "stock")]
    [InlineData(true, "lumara")]
    public void TheStandardAppIsAlwaysReachableInReplaceMode(bool stockQuery, string? cookie)
    {
        Assert.Equal(WebDecision.StockWithBackButton, WebModeDecider.Decide(WebMode.Replace, true, true, stockQuery, cookie));
        Assert.Equal(WebDecision.PassThrough, WebModeDecider.Decide(WebMode.Replace, false, true, stockQuery, cookie));
    }
}
