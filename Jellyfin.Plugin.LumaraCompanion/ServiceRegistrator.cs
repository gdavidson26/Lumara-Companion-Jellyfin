// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.LumaraCompanion.Integrations;
using Jellyfin.Plugin.LumaraCompanion.Integrations.Arr;
using Jellyfin.Plugin.LumaraCompanion.Integrations.Seerr;
using Jellyfin.Plugin.LumaraCompanion.Security;
using Jellyfin.Plugin.LumaraCompanion.Server;
using Jellyfin.Plugin.LumaraCompanion.Settings;
using Jellyfin.Plugin.LumaraCompanion.UserData;
using Jellyfin.Plugin.LumaraCompanion.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LumaraCompanion;

/// <summary>
/// Registers the plugin's services with Jellyfin's dependency injection.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>Folder name under the server's plugin configurations path.</summary>
    public const string DataFolder = "LumaraCompanion";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp => new SecretProtector(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, DataFolder, "keys")));

        serviceCollection.AddSingleton(sp => new UserSettingsStore(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, DataFolder, "users")));

        serviceCollection.AddSingleton(sp => new UserDataStore(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, DataFolder, "lists")));

        serviceCollection.AddSingleton(sp => new ReviewStore(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, DataFolder, "reviews")));

        // One plain, long-lived client with no logging handler: factory clients log
        // request URLs, and MDBList wants its key in the URL (see IntegrationService).
        // Cookies off: services must never share a cookie jar (a Seerr session
        // cookie must not be sent to Sonarr). qBittorrent's session cookie is
        // handled explicitly in ServiceClient.
        var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        serviceCollection.AddSingleton(sp => new IntegrationService(
            sp.GetRequiredService<SecretProtector>(),
            () => Plugin.Instance!.Configuration,
            () => Plugin.Instance!.SaveConfiguration(),
            http));

        serviceCollection.AddSingleton(sp => new RatingsService(sp.GetRequiredService<IntegrationService>(), http));

        // Requests, calendar, downloads and the *arr tools. All of them
        // reach the services through ServiceClient, the only place keys are
        // decrypted for a call.
        serviceCollection.AddSingleton(sp => new ServiceClient(sp.GetRequiredService<IntegrationService>(), http));
        serviceCollection.AddSingleton(_ => new ImageSigner());
        serviceCollection.AddSingleton(sp => new LibraryIndex(sp.GetRequiredService<ILibraryManager>()));
        serviceCollection.AddSingleton(sp => new SeerrGateway(
            sp.GetRequiredService<ServiceClient>(),
            sp.GetRequiredService<LibraryIndex>(),
            sp.GetRequiredService<ImageSigner>(),
            sp.GetRequiredService<ILocalizationManager>()));
        serviceCollection.AddSingleton(sp => new UserJsonStore<WatchlistDoc>(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, DataFolder, "watchlist")));
        serviceCollection.AddSingleton(sp => new UserJsonStore<RequestPrefs>(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, DataFolder, "request-prefs")));
        serviceCollection.AddSingleton(sp => new Watchlist(
            sp.GetRequiredService<UserJsonStore<WatchlistDoc>>(),
            sp.GetRequiredService<SeerrGateway>(),
            sp.GetRequiredService<LibraryIndex>(),
            sp.GetRequiredService<ImageSigner>()));
        serviceCollection.AddSingleton(sp => new CalendarService(
            sp.GetRequiredService<ServiceClient>(),
            sp.GetRequiredService<LibraryIndex>(),
            sp.GetRequiredService<ImageSigner>(),
            sp.GetRequiredService<ILocalizationManager>()));
        serviceCollection.AddSingleton(sp => new DownloadsService(
            sp.GetRequiredService<ServiceClient>(),
            sp.GetRequiredService<LibraryIndex>(),
            sp.GetRequiredService<ImageSigner>()));
        serviceCollection.AddSingleton(sp => new ArrTools(sp.GetRequiredService<ServiceClient>()));
        serviceCollection.AddSingleton(sp => new TagSync(
            sp.GetRequiredService<ServiceClient>(),
            sp.GetRequiredService<ILibraryManager>(),
            () => Plugin.Instance?.Configuration.Requests ?? new Configuration.RequestSettings()));
        serviceCollection.AddSingleton<IScheduledTask, TagSyncTask>();
        serviceCollection.AddHostedService<AutoRequestService>();

        serviceCollection.AddSingleton(sp => new BrandingService(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, DataFolder, "branding")));

        // Maintenance lockout runs as an MVC filter, after Jellyfin's authentication.
        serviceCollection.Configure<MvcOptions>(o => o.Filters.Add<MaintenanceFilter>());

        serviceCollection.AddSingleton(_ => LumaraWebFiles.Default());
        serviceCollection.AddTransient<IStartupFilter, WebModeStartupFilter>();
    }
}
