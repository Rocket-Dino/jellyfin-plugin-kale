using System;
using System.IO;
using Jellyfin.Plugin.Kale.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Kale;

/// <summary>
/// The server half of Kale's library repairs (ADR-019 amendment). Metadata-only edits to
/// one named file at a time, asked for by an admin from the Kale app.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    public static readonly Guid PluginId = new("6e519877-6d61-4016-957f-10a897d9d7ce");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
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
    /// undo a fix).
    /// </summary>
    public override void OnUninstalling()
    {
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
