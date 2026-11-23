using System;
using System.Net.Http.Headers;
using Jellyfin.Plugin.YouTubeMusic.Channel;
using Jellyfin.Plugin.YouTubeMusic.Search;
using Jellyfin.Plugin.YouTubeMusic.Sidecar;
using Jellyfin.Plugin.YouTubeMusic.Users;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.YouTubeMusic;

/// <summary>
/// Registers the plugin's services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(SidecarClient.HttpClientName, client =>
        {
            // Per-request timeouts are applied by callers (downloads can take minutes).
            client.Timeout = TimeSpan.FromMinutes(15);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-YouTubeMusic", "1.0"));
        });

        serviceCollection.AddHttpContextAccessor();
        serviceCollection.AddSingleton<SidecarClient>();
        serviceCollection.AddSingleton<LibraryScanScheduler>();
        serviceCollection.AddSingleton<UserLibraryService>();
        serviceCollection.AddSingleton<LikesSyncService>();
        serviceCollection.AddSingleton<IScheduledTask, ImportLikesTask>();

        // Jellyfin resolves channels from DI (IEnumerable<IChannel>); register ours explicitly.
        serviceCollection.AddSingleton<YouTubeMusicChannel>();
        serviceCollection.AddSingleton<IChannel>(sp => sp.GetRequiredService<YouTubeMusicChannel>());

        // Adds YouTube matches to the normal search box results (GET /Items?searchTerm=...).
        serviceCollection.AddScoped<SearchInjectionFilter>();
        serviceCollection.AddScoped<FileEndpointFilter>();
        serviceCollection.Configure<MvcOptions>(o =>
        {
            o.Filters.AddService<SearchInjectionFilter>();

            // Finamp direct-plays via GET /Items/{id}/File; serve YouTube items from the sidecar.
            o.Filters.AddService<FileEndpointFilter>();
        });
        serviceCollection.AddHostedService<FavoriteDownloadService>();
    }
}
