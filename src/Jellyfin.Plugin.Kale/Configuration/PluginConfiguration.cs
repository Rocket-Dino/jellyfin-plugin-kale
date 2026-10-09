using System;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Jellyfin.Plugin.Kale.Requests;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Kale.Configuration;

/// <summary>
/// Repairs need no configuration (each is asked for one item at a time by an admin). The request
/// broker (kale#308) needs Seerr's address and API key, pushed here by an admin's Kale app.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Seerr's address. Not a secret.</summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Seerr's API key. Persisted to the plugin's XML on the server, and NEVER serialised to JSON:
    /// Jellyfin's own <c>GET /Plugins/{id}/Configuration</c> would otherwise hand it to anyone with an
    /// admin token, and nothing in Kale needs it back. Set only through <c>POST /Kale/Requests/Configuration</c>.
    /// </summary>
    [JsonIgnore]
    public string SeerrApiKey { get; set; } = string.Empty;

    /// <summary>Both, when both are usable.</summary>
    [JsonIgnore]
    [XmlIgnore]
    public SeerrSettings? Seerr =>
        !string.IsNullOrWhiteSpace(SeerrApiKey) && Uri.TryCreate(SeerrUrl, UriKind.Absolute, out var uri)
            ? new SeerrSettings(uri, SeerrApiKey)
            : null;

    /// <summary>Read-merge-write: set the two Seerr fields on THIS configuration, keep everything else.</summary>
    public PluginConfiguration WithSeerr(string url, string apiKey)
    {
        SeerrUrl = url.Trim().TrimEnd('/');
        SeerrApiKey = apiKey.Trim();
        return this;
    }
}
