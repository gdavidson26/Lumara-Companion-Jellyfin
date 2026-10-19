// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LumaraCompanion.Configuration;
using Jellyfin.Plugin.LumaraCompanion.Integrations;
using Jellyfin.Plugin.LumaraCompanion.Security;
using Jellyfin.Plugin.LumaraCompanion.Server;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.LumaraCompanion.Tests;

/// <summary>Maintenance notices, branding and the ratings gateway.</summary>
public sealed class ServerFeaturesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc);

    // Built at runtime so secret scanners don't flag test values as real keys.
    private static readonly string MdbKey = string.Concat(Enumerable.Repeat("ab12", 6));
    private static readonly string TmdbV3 = string.Concat(Enumerable.Repeat("cd34", 8));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumara-server-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    // ----- Maintenance -----

    [Fact]
    public void MaintenanceOffWhenDisabledOrFinished()
    {
        Assert.Equal("None", Maintenance.Evaluate(new MaintenanceSettings { LockOut = true }, Now).State);
        var done = new MaintenanceSettings { Enabled = true, EndUtc = Now.AddMinutes(-1) };
        Assert.Equal("None", Maintenance.Evaluate(done, Now).State);
    }

    [Fact]
    public void MaintenanceCountsDownThenActivates()
    {
        var s = new MaintenanceSettings { Enabled = true, StartUtc = Now.AddMinutes(90), EndUtc = Now.AddHours(3), AnnounceMinutes = 60, LockOut = true };
        Assert.Equal("None", Maintenance.Evaluate(s, Now).State);
        var soon = Maintenance.Evaluate(s, Now.AddMinutes(40));
        Assert.Equal("Scheduled", soon.State);
        Assert.False(soon.LockOut); // no lockout before it starts
        var on = Maintenance.Evaluate(s, Now.AddMinutes(95));
        Assert.Equal("Active", on.State);
        Assert.True(on.LockOut);
        Assert.Equal("None", Maintenance.Evaluate(s, Now.AddHours(4)).State);
    }

    [Fact]
    public void MaintenanceWithoutStartIsActiveNow()
    {
        var s = new MaintenanceSettings { Enabled = true, Message = "Upgrading" };
        var st = Maintenance.Evaluate(s, Now);
        Assert.Equal("Active", st.State);
        Assert.Equal("Upgrading", st.Message);
        Assert.False(st.LockOut);
    }

    [Fact]
    public void MaintenanceValidation()
    {
        Assert.True(Maintenance.Validate(new MaintenanceSettings { Message = "  ok  " }, out _));
        Assert.False(Maintenance.Validate(new MaintenanceSettings { Message = new string('a', 301) }, out _));
        Assert.False(Maintenance.Validate(new MaintenanceSettings { StartUtc = Now, EndUtc = Now }, out _));
        Assert.False(Maintenance.Validate(new MaintenanceSettings { AnnounceMinutes = -1 }, out _));
        Assert.False(Maintenance.Validate(new MaintenanceSettings { AnnounceMinutes = Maintenance.MaxAnnounceMinutes + 1 }, out _));
    }

    [Theory]
    [InlineData("/Items", false, true, true)]
    [InlineData("/Items", true, true, false)]   // admin
    [InlineData("/Items", false, false, false)] // signed out
    [InlineData("/Lumara/Info", false, true, false)]
    [InlineData("/Sessions/Playing/Stopped", false, true, false)]
    [InlineData("/Users/AuthenticateByName", false, true, false)]
    [InlineData("/Videos/abc/stream", false, true, true)]
    public void LockoutBlocksOnlySignedInNonAdmins(string path, bool admin, bool signedIn, bool blocked)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        if (signedIn)
        {
            var claims = new List<Claim> { new("Jellyfin-UserId", Guid.NewGuid().ToString()) };
            claims.Add(new Claim(ClaimTypes.Role, admin ? "Administrator" : "User"));
            http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Custom"));
        }

        Assert.Equal(blocked, MaintenanceFilter.ShouldBlock(http));
    }

    [Fact]
    public void ApiKeysAreNeverLockedOut()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/Items";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-IsApiKey", "true") }, "Custom"));
        Assert.False(MaintenanceFilter.ShouldBlock(http));
    }

    // ----- Branding -----

    [Fact]
    public void BrandingValidation()
    {
        var b = new BrandingSettings { Name = "  Home  ", Accent = "#f2b66d" };
        Assert.True(BrandingService.Validate(b, out _));
        Assert.Equal("Home", b.Name);
        Assert.Equal("#F2B66D", b.Accent);
        Assert.False(BrandingService.Validate(new BrandingSettings { Name = new string('n', 41) }, out _));
        Assert.False(BrandingService.Validate(new BrandingSettings { Accent = "red" }, out _));
        Assert.False(BrandingService.Validate(new BrandingSettings { Accent = "#12345" }, out _));
        Assert.False(BrandingService.Validate(new BrandingSettings { Message = new string('m', 301) }, out _));
    }

    [Fact]
    public void LogoAcceptsOnlyRealImages()
    {
        var store = new BrandingService(_dir);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();
        var svg = "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray();

        Assert.False(store.SaveLogo(svg, out var err));
        Assert.NotEmpty(err);
        Assert.False(store.SaveLogo(new byte[BrandingService.MaxLogoBytes + 1], out _));
        Assert.Null(store.Logo());

        Assert.True(store.SaveLogo(png, out _));
        Assert.Equal("image/png", store.Logo()!.Value.ContentType);
        Assert.True(store.SaveLogo(webp, out _));
        Assert.Equal("image/webp", store.Logo()!.Value.ContentType);
        Assert.Single(Directory.GetFiles(_dir)); // the old PNG was replaced
        store.DeleteLogo();
        Assert.Null(store.Logo());
    }

    // ----- Ratings -----

    [Fact]
    public void MdbListParsingIsLenientAndOrdered()
    {
        const string json = """
        {"ratings":[
          {"source":"metacritic","value":81,"score":81,"votes":50},
          {"source":"imdb","value":"7.4","score":74,"votes":"123456"},
          {"source":"tomatoes","value":92,"score":92,"votes":null},
          {"source":"letterboxd","value":3.9,"score":78},
          {"source":"rogerebert","value":3.5,"score":88},
          {"source":"trakt","value":null,"score":null}
        ]}
        """;
        var list = RatingsService.ParseMdbList(json);
        Assert.Equal(new[] { "imdb", "metacritic", "letterboxd" }.Concat(new[] { "tomatoes" }).OrderBy(x => x), list.Select(r => r.Source).OrderBy(x => x));
        Assert.Equal("7.4", list.First(r => r.Source == "imdb").Value);
        Assert.Equal(123456, list.First(r => r.Source == "imdb").Votes);
        Assert.Equal("92%", list.First(r => r.Source == "tomatoes").Value);
        Assert.Equal("81", list.First(r => r.Source == "metacritic").Value);
        Assert.Empty(RatingsService.ParseMdbList("not json"));
        Assert.Empty(RatingsService.ParseMdbList("{}"));
    }

    [Fact]
    public void TmdbParsing()
    {
        Assert.Equal("7.6", RatingsService.ParseTmdbScore("""{"vote_average":7.634,"vote_count":900}""")!.Value);
        Assert.Null(RatingsService.ParseTmdbScore("""{"vote_average":0,"vote_count":0}"""));
        var w = RatingsService.ParseProviders("""
        {"results":{"US":{"flatrate":[{"provider_name":"Netflix"},{"provider_name":"Netflix"}],"rent":[{"provider_name":"Apple TV"}]},
                    "GB":{"buy":[{"provider_name":"Sky Store"}]}}}
        """, "US");
        Assert.NotNull(w);
        Assert.Equal(new[] { "Netflix" }, w!.Stream);
        Assert.Equal(new[] { "Apple TV" }, w.Rent);
        Assert.Empty(w.Buy);
        Assert.Null(RatingsService.ParseProviders("""{"results":{}}""", "US"));
    }

    [Fact]
    public async Task NothingIsFetchedWithoutKeys()
    {
        var (service, handler) = Ratings(mdb: null, tmdb: null);
        var r = await service.GetAsync("603", false, "US", CancellationToken.None);
        Assert.False(r.Configured);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FetchesBothServicesAndCaches()
    {
        var (service, handler) = Ratings(MdbKey, TmdbV3);
        handler.Reply = uri => uri.Contains("mdblist.test", StringComparison.Ordinal)
            ? """{"ratings":[{"source":"imdb","value":8.7,"score":87,"votes":2000000}]}"""
            : uri.Contains("watch/providers", StringComparison.Ordinal)
                ? """{"results":{"US":{"flatrate":[{"provider_name":"Max"}]}}}"""
                : """{"vote_average":8.2,"vote_count":20000}""";

        var r = await service.GetAsync("603", false, "us", CancellationToken.None);
        Assert.True(r.Configured);
        Assert.Equal(new[] { "imdb", "tmdb" }, r.Ratings.Select(x => x.Source));
        Assert.Equal("Max", r.Providers!.Stream.Single());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains(handler.Requests, u => u.StartsWith("https://mdblist.test/tmdb/movie/603?apikey=", StringComparison.Ordinal));
        // A v3 TMDB key goes in the query, not a Bearer header.
        Assert.Contains(handler.Requests, u => u.StartsWith("https://tmdb.test/movie/603?api_key=", StringComparison.Ordinal));
        Assert.Empty(handler.AuthHeaders);

        await service.GetAsync("603", false, "US", CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count); // served from the cache
    }

    [Fact]
    public async Task ShowsUseShowRoutesAndBearerTokens()
    {
        var bearer = "eyJ" + string.Concat(Enumerable.Repeat("x9", 10));
        var (service, handler) = Ratings(null, bearer);
        handler.Reply = _ => "{}";
        await service.GetAsync("1396", true, "US", CancellationToken.None);
        Assert.All(handler.Requests, u => Assert.StartsWith("https://tmdb.test/tv/1396", u, StringComparison.Ordinal));
        Assert.All(handler.Requests, u => Assert.DoesNotContain("api_key", u, StringComparison.Ordinal));
        Assert.Equal(2, handler.AuthHeaders.Count);
    }

    [Fact]
    public async Task OddIdsAreIgnored()
    {
        var (service, handler) = Ratings(MdbKey, null);
        var r = await service.GetAsync("603/../../x", false, "US", CancellationToken.None);
        Assert.True(r.Configured);
        Assert.Empty(r.Ratings);
        Assert.Empty(handler.Requests);
    }

    private (RatingsService Service, FakeHandler Handler) Ratings(string? mdb, string? tmdb)
    {
        var config = new PluginConfiguration();
        var integrations = new IntegrationService(new SecretProtector(Path.Combine(_dir, "keys")), () => config, () => { });
        if (mdb is not null)
        {
            integrations.Set(IntegrationKind.MdbList, null, mdb);
        }

        if (tmdb is not null)
        {
            integrations.Set(IntegrationKind.Tmdb, null, tmdb);
        }

        var handler = new FakeHandler();
        return (new RatingsService(integrations, new HttpClient(handler), () => Now, "https://mdblist.test", "https://tmdb.test"), handler);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string> AuthHeaders { get; } = [];

        public Func<string, string> Reply { get; set; } = _ => "{}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);
            if (request.Headers.Authorization is not null)
            {
                AuthHeaders.Add(request.Headers.Authorization.Scheme);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply(uri)) });
        }
    }
}
