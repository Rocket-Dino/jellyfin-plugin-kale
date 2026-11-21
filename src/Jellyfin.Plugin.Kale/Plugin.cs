using System;
using System.IO;
using Jellyfin.Plugin.Kale.Configuration;
using Jellyfin.Plugin.Kale.Share;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Kale;

/// <summary>
/// The server half of Kale's library repairs (ADR-019 amendment). Metadata-only edits to
/// one named file at a time, asked for by an admin from the Kale app.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    public static readonly Guid PluginId = new("6e519877-6d61-4016-957f-10a897d9d7ce");

    private readonly IServerConfigurationManager? _serverConfig;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IServerConfigurationManager? serverConfig = null)
        : base(applicationPaths, xmlSerializer)
    {
        _serverConfig = serverConfig;
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Kale";

    public override Guid Id => PluginId;

    public override string Description =>
        "Optional extras for the Kale app: requests through Seerr without signing in on each device, and fixing a "
        + "file's default soundtrack in place. Kale works without it.";

    /// <summary>
    /// Uninstalling takes the configuration with it: it holds Seerr's API key and the invitation
    /// sign-ins' hashes, which must not outlive the plugin that guarded them. Jellyfin deletes the
    /// plugin's folder but leaves <c>plugins/configurations/Jellyfin.Plugin.Kale.xml</c> behind.
    /// Undo records for soundtrack fixes are kept (they hold no secret, and a reinstall can still
    /// undo a fix). Before that, the Known Proxies entries Kale added for its doorway are taken out of
    /// Jellyfin's list (the owner's own entries stay), because the record of which ones are Kale's is in
    /// that configuration.
    /// </summary>
    public override void OnUninstalling()
    {
        if (_serverConfig is not null)
        {
            try
            {
                Doorway.ReleaseKnownProxies(_serverConfig, Configuration);
            }
            catch (Exception)
            {
                // Uninstalling must not fail on it; the entry left behind is a loopback address.
            }
        }

        try
        {
            File.Delete(ConfigurationFilePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        base.OnUninstalling();
    }
}
