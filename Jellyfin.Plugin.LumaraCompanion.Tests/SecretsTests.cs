// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Plugin.LumaraCompanion.Configuration;
using Jellyfin.Plugin.LumaraCompanion.Integrations;
using Jellyfin.Plugin.LumaraCompanion.Security;
using Xunit;

namespace Jellyfin.Plugin.LumaraCompanion.Tests;

/// <summary>
/// The promise these tests guard: integration URLs and keys are encrypted on disk
/// and never come back out of the plugin, not in statuses, test results or the
/// saved configuration file.
/// </summary>
public sealed class SecretsTests : IDisposable
{
    // Built at runtime so secret scanners don't flag test values as real keys.
    private static readonly string Key = string.Concat(Enumerable.Repeat("f00d", 8));
    private const string Url = "http://radarr.internal:7878";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumara-secrets-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new();
    private int _saves;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private SecretProtector Protector(string name = "keys") => new(Path.Combine(_dir, name));

    private IntegrationService Service(HttpMessageHandler? handler = null) =>
        new(Protector(), () => _config, () => _saves++, handler is null ? null : new HttpClient(handler));

    [Fact]
    public void ProtectRoundTripsAndHidesThePlaintext()
    {
        var p = Protector();
        var sealedValue = p.Protect(Key);
        Assert.DoesNotContain(Key, sealedValue, StringComparison.Ordinal);
        Assert.Equal(Key, p.Unprotect(sealedValue));
    }

    [Fact]
    public void EmptyValuesStayEmpty()
    {
        var p = Protector();
        Assert.Equal(string.Empty, p.Protect(null));
        Assert.Equal(string.Empty, p.Protect(string.Empty));
        Assert.Null(p.Unprotect(null));
        Assert.Null(p.Unprotect(string.Empty));
    }

    [Fact]
    public void TamperedOrForeignCiphertextReadsAsNotSetUp()
    {
        var sealedValue = Protector().Protect(Key);
        Assert.Null(Protector().Unprotect(sealedValue[..^4] + "AAAA"));
        Assert.Null(Protector("other-server").Unprotect(sealedValue));
    }

    [Fact]
    public void SavedConfigurationNeverHoldsPlaintext()
    {
        Service().Set(IntegrationKind.Radarr, Url, Key);
        Assert.Equal(1, _saves);

        // Jellyfin saves plugin configuration as XML; check what would hit the disk.
        using var writer = new StringWriter();
        new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, _config);
        var xml = writer.ToString();
        Assert.DoesNotContain(Key, xml, StringComparison.Ordinal);
        Assert.DoesNotContain("radarr.internal", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusesSayConfiguredWithoutTheValues()
    {
        var service = Service();
        service.Set(IntegrationKind.Radarr, Url, Key);
        service.Set(IntegrationKind.Tmdb, null, Key);

        var statuses = service.Statuses();
        var json = JsonSerializer.Serialize(statuses);
        Assert.DoesNotContain(Key, json, StringComparison.Ordinal);
        Assert.DoesNotContain("radarr.internal", json, StringComparison.Ordinal);

        var radarr = statuses.Single(s => s.Kind == "Radarr");
        Assert.True(radarr.UrlConfigured && radarr.ApiKeyConfigured && radarr.NeedsUrl);
        var tmdb = statuses.Single(s => s.Kind == "Tmdb");
        Assert.True(tmdb.ApiKeyConfigured);
        Assert.False(tmdb.NeedsUrl);
        Assert.Equal(Enum.GetValues<IntegrationKind>().Length, statuses.Count);
    }

    [Fact]
    public void NullKeepsTheStoredValueAndClearRemovesIt()
    {
        var service = Service();
        service.Set(IntegrationKind.Sonarr, Url, Key);
        service.Set(IntegrationKind.Sonarr, null, null);
        Assert.Equal((Url, Key), service.Reveal(IntegrationKind.Sonarr));

        service.Clear(IntegrationKind.Sonarr);
        Assert.Equal(((string?)null, (string?)null), service.Reveal(IntegrationKind.Sonarr));
        Assert.False(service.Statuses().Single(s => s.Kind == "Sonarr").ApiKeyConfigured);
    }

    [Theory]
    [InlineData("radarr:7878")]
    [InlineData("ftp://radarr")]
    [InlineData("javascript:alert(1)")]
    public void NonHttpAddressesAreRejected(string url)
    {
        Assert.Throws<ArgumentException>(() => Service().Set(IntegrationKind.Radarr, url, Key));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task ConnectionTestSendsTheKeyButNeverEchoesIt()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var service = Service(handler);
        service.Set(IntegrationKind.Radarr, Url + "/", Key);

        var result = await service.TestAsync(IntegrationKind.Radarr, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(Url + "/api/v3/system/status", handler.Request!.RequestUri!.ToString());
        Assert.Equal(Key, handler.Request.Headers.GetValues("X-Api-Key").Single());
        Assert.DoesNotContain(Key, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FailedTestsDescribeTheProblemWithoutSecrets(HttpStatusCode code)
    {
        var service = Service(new RecordingHandler(code));
        service.Set(IntegrationKind.Radarr, Url, Key);

        var result = await service.TestAsync(IntegrationKind.Radarr, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.DoesNotContain(Key, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("radarr.internal", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableServiceMessageHasNoAddress()
    {
        var service = Service(new RecordingHandler(new HttpRequestException("Connection refused (radarr.internal:7878)")));
        service.Set(IntegrationKind.Radarr, Url, Key);

        var result = await service.TestAsync(IntegrationKind.Radarr, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.DoesNotContain("radarr.internal", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotSetUpSkipsTheNetwork()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var result = await Service(handler).TestAsync(IntegrationKind.Seerr, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Null(handler.Request);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly Exception? _throw;

        public RecordingHandler(HttpStatusCode code) => _code = code;

        public RecordingHandler(Exception toThrow) => _throw = toThrow;

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (_throw is not null)
            {
                throw _throw;
            }

            return Task.FromResult(new HttpResponseMessage(_code));
        }
    }
}
