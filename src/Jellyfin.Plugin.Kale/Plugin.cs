using System;
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
        "Lets the Kale app fix a file's default soundtrack in place, and lets everyone in the household ask for films and shows through Seerr without signing in on each device.";
}
