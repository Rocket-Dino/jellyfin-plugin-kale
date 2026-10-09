using System;
using System.Net.Http;
using Jellyfin.Plugin.Kale.Repair;
using Jellyfin.Plugin.Kale.Requests;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kale;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<DefaultAudioRepair>();
        serviceCollection.AddSingleton<MemberRulesSource>();
        // The broker's own connection pool: Seerr is one host on the LAN, and recycling connections
        // keeps a changed address (DNS) from pinning a dead one.
        serviceCollection.AddSingleton(sp => new SeerrBroker(
            new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) },
            () => Plugin.Instance?.Configuration.Seerr,
            sp.GetRequiredService<ILogger<SeerrBroker>>()));
    }
}
