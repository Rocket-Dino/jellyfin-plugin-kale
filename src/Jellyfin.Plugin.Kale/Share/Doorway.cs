using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.Kale.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Kale.Share;

/// <summary>
/// The addresses the doorway (kale#373, ADR-020 §1c) works with. Guests get one address each from a
/// documentation range, set as <c>X-Forwarded-For</c> by <c>kale-doorway</c>; the doorway itself reaches
/// Jellyfin on loopback, and that loopback address is what goes in Known Proxies.
/// </summary>
public static class DoorwayAddresses
{
    /// <summary>TEST-NET-3 (RFC 5737). Must match <c>kale-doorway --guest-net</c>'s default.</summary>
    public static readonly IPAddress GuestNetwork = IPAddress.Parse("203.0.113.0");

    public const int GuestPrefixLength = 24;

    public static string GuestNetworkText => $"{GuestNetwork}/{GuestPrefixLength}";

    /// <summary>Every address the doorway can give a guest: .1 to .254.</summary>
    public static IEnumerable<IPAddress> GuestHosts()
    {
        var b = GuestNetwork.GetAddressBytes();
        for (var host = 1; host < 255; host++)
        {
            yield return new IPAddress(new[] { b[0], b[1], b[2], (byte)host });
        }
    }

    /// <summary>
    /// The address the doorway connects to Jellyfin FROM: 127.0.0.1, or ::1 on a server with IPv4 turned
    /// off (Jellyfin skips an IPv4 Known Proxies entry then). The supervisor must point the doorway's
    /// <c>--upstream</c> at the same loopback.
    /// </summary>
    public static IPAddress Upstream(NetworkConfiguration network) =>
        network.EnableIPv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
}

/// <summary>Editing Jellyfin's Known Proxies: the owner's list is appended to, never replaced.</summary>
public static class KnownProxyList
{
    /// <summary>Does an entry already name this exact address? A subnet entry counts only if it is the address's own /32 (/128).</summary>
    public static bool Lists(IEnumerable<string> entries, IPAddress address)
    {
        address = Normal(address);
        return entries.Any(e => Parse(e) is { } a && a.Equals(address));
    }

    /// <summary>
    /// The owner's list with <paramref name="address"/> appended, and the entry that was added (null when
    /// the list already named it, in which case the entry is the owner's and is never removed by Kale).
    /// </summary>
    public static (string[] List, string? Added) Append(string[] existing, IPAddress address)
    {
        if (Lists(existing, address))
        {
            return (existing, null);
        }

        var entry = Normal(address).ToString();
        return (existing.Append(entry).ToArray(), entry);
    }

    /// <summary>The list without the entries Kale added (one occurrence each); every other entry kept, in order.</summary>
    public static string[] Remove(string[] existing, IEnumerable<string> ours)
    {
        var list = existing.ToList();
        foreach (var entry in ours)
        {
            var i = list.FindLastIndex(e => string.Equals(e.Trim(), entry, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                list.RemoveAt(i);
            }
        }

        return list.ToArray();
    }

    private static IPAddress? Parse(string entry)
    {
        var e = entry.Trim();
        if (e.EndsWith("/32", StringComparison.Ordinal) || e.EndsWith("/128", StringComparison.Ordinal))
        {
            e = e[..e.LastIndexOf('/')];
        }

        return IPAddress.TryParse(e, out var a) ? Normal(a) : null;
    }

    private static IPAddress Normal(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}

/// <summary>
/// Whether the RUNNING server trusts an address's <c>X-Forwarded-For</c>. Jellyfin reads Known Proxies
/// once, at startup, into <see cref="ForwardedHeadersOptions"/> (<c>ConfigureForwardHeaders</c>, 10.11.11
/// and 12.4 alike), and its forwarded-headers middleware uses that same singleton. So this reads what the
/// middleware reads, rather than assuming a saved setting took effect.
/// </summary>
public static class ForwardingTrust
{
    public static bool Trusts(ForwardedHeadersOptions options, IPAddress address)
    {
        if (!options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor))
        {
            return false; // Known Proxies was empty at startup: Jellyfin reads no forwarding header at all
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (options.KnownProxies.Any(p => (p.IsIPv4MappedToIPv6 ? p.MapToIPv4() : p).Equals(address)))
        {
            return true;
        }

#if JF_10_11
        return options.KnownNetworks.Any(n => n.Contains(address));
#else
        // ASP.NET 10 keeps the old list for compatibility; the middleware consults both.
#pragma warning disable ASPDEPR005, CS0618
        return options.KnownIPNetworks.Any(n => n.Contains(address)) || options.KnownNetworks.Any(n => n.Contains(address));
#pragma warning restore ASPDEPR005, CS0618
#endif
    }
}

/// <summary>
/// What the plugin reports about the doorway (<c>GET /Kale/Share/Doorway</c>). Codes only: the app owns
/// every word shown to a person.
/// </summary>
public sealed record DoorwayStatus(
    string Outcome,
    bool Enabled,
    bool CanStart,
    bool RestartNeeded,
    string GuestNetwork,
    string Upstream,
    bool KnownProxyConfigured,
    bool KnownProxyLive,
    int LocalGuestAddresses,
    bool GuestsAdmitted,
    string? AdmitRefusal)
{
    /// <summary>Sharing through the doorway is not turned on.</summary>
    public const string Off = "off";

    /// <summary>Jellyfin's LAN networks include guest addresses: the doorway must not run (whatever <c>Enabled</c> says).</summary>
    public const string GuestsLookLocal = "guestsLookLocal";

    /// <summary>On, but Known Proxies no longer names the upstream (the owner removed it): Enable adds it back.</summary>
    public const string ProxyMissing = "proxyMissing";

    /// <summary>On and saved, but the running server doesn't trust the upstream yet: restart Jellyfin.</summary>
    public const string RestartNeededOutcome = "restartNeeded";

    /// <summary>The running server trusts the upstream and classes every guest address as remote.</summary>
    public const string Live = "live";

    /// <summary>
    /// The doorway may run only when Jellyfin is ALREADY trusting its upstream address and classes every
    /// guest address as remote. Running it before the restart would be the kale#373 bug itself: the
    /// header ignored, every guest seen as loopback, so local.
    /// </summary>
    public static DoorwayStatus Compute(bool enabled, bool configured, bool live, int localGuestAddresses,
        string? admitRefusal, IPAddress upstream)
    {
        var outcome = (localGuestAddresses, enabled, configured, live) switch
        {
            ( > 0, _, _, _) => GuestsLookLocal,
            (_, false, _, _) => Off,
            (_, true, false, _) => ProxyMissing,
            (_, true, true, false) => RestartNeededOutcome,
            _ => Live,
        };
        return new DoorwayStatus(
            outcome,
            enabled,
            CanStart: outcome == Live,
            RestartNeeded: outcome == RestartNeededOutcome,
            DoorwayAddresses.GuestNetworkText,
            upstream.ToString(),
            configured,
            live,
            localGuestAddresses,
            GuestsAdmitted: admitRefusal is null,
            admitRefusal);
    }
}

/// <summary>
/// The plugin's half of the doorway (kale#373): Known Proxies, and the go/no-go for running it. It never
/// restarts Jellyfin (Kale asks the admin, kale#375) and does not yet download or supervise the binary.
/// </summary>
public sealed class Doorway
{
    private readonly IServerConfigurationManager _config;
    private readonly INetworkManager _network;
    private readonly IOptions<ForwardedHeadersOptions> _forwarded;
    private readonly IServerApplicationHost _host;
    private readonly ILogger<Doorway> _logger;

    public Doorway(IServerConfigurationManager config, INetworkManager network, IOptions<ForwardedHeadersOptions> forwarded,
        IServerApplicationHost host, ILogger<Doorway> logger)
    {
        _config = config;
        _network = network;
        _forwarded = forwarded;
        _host = host;
        _logger = logger;
    }

    private static PluginConfiguration Settings => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Does the running server trust the doorway's upstream address's X-Forwarded-For?</summary>
    public bool TrustLive() => ForwardingTrust.Trusts(_forwarded.Value, DoorwayAddresses.Upstream(_config.GetNetworkConfiguration()));

    /// <summary>Computed fresh on every call: LAN networks change live, without a restart.</summary>
    public DoorwayStatus Status()
    {
        var net = _config.GetNetworkConfiguration();
        var upstream = DoorwayAddresses.Upstream(net);
        var hosts = DoorwayAddresses.GuestHosts().ToList();
        var local = hosts.Count(_network.IsInLocalNetwork);
        var refusal = hosts.Select(_network.ShouldAllowServerAccess).FirstOrDefault(r => r != RemoteAccessPolicyResult.Allow);
        return DoorwayStatus.Compute(
            Settings.DoorwayEnabled,
            KnownProxyList.Lists(net.KnownProxies, upstream),
            ForwardingTrust.Trusts(_forwarded.Value, upstream),
            local,
            refusal == RemoteAccessPolicyResult.Allow ? null : refusal.ToString(),
            upstream);
    }

    /// <summary>
    /// Turn sharing through the doorway on: append the upstream address to Known Proxies (if the owner's
    /// list doesn't already name it) and flag the server for a restart if the running one doesn't trust
    /// it yet. Refused, changing nothing, when Jellyfin would class guests as local.
    /// </summary>
    public (DoorwayStatus Status, bool Refused) Enable()
    {
        var before = Status();
        if (before.Outcome == DoorwayStatus.GuestsLookLocal)
        {
            return (before, true);
        }

        var net = _config.GetNetworkConfiguration();
        var (list, added) = KnownProxyList.Append(net.KnownProxies, DoorwayAddresses.Upstream(net));
        var plugin = Plugin.Instance;
        if (added is not null && plugin is not null)
        {
            // Record ownership BEFORE writing Jellyfin's list: a failure between the two then leaves a
            // record of nothing (harmless), never an entry Kale would not know to remove.
            if (!plugin.Configuration.DoorwayKnownProxies.Contains(added, StringComparer.OrdinalIgnoreCase))
            {
                plugin.Configuration.DoorwayKnownProxies.Add(added);
            }

            plugin.SaveConfiguration();
            net.KnownProxies = list;
            _config.SaveConfiguration(NetworkConfigurationStore.StoreKey, net);
            _logger.LogInformation("Kale doorway: added {Entry} to Known Proxies", added);
        }

        if (plugin is not null && !plugin.Configuration.DoorwayEnabled)
        {
            plugin.Configuration.DoorwayEnabled = true;
            plugin.SaveConfiguration();
        }

        var after = Status();
        if (after.RestartNeeded)
        {
            _host.NotifyPendingRestart(); // flags it; Kale asks the admin (kale#375). Never restarts.
        }

        return (after, false);
    }

    /// <summary>
    /// Turn it off: remove only the Known Proxies entries Kale added. The running server keeps trusting
    /// loopback until its next restart (and after it too, while the owner lists any other proxy, by
    /// ASP.NET's defaults); with no doorway running, that admits nobody.
    /// </summary>
    public DoorwayStatus Disable()
    {
        if (Plugin.Instance is { } plugin)
        {
            ReleaseKnownProxies(_config, plugin.Configuration);
            plugin.Configuration.DoorwayEnabled = false;
            plugin.SaveConfiguration();
        }

        return Status();
    }

    /// <summary>Take Kale's own Known Proxies entries out of Jellyfin's list. Used by Disable and by uninstalling.</summary>
    public static void ReleaseKnownProxies(IServerConfigurationManager config, PluginConfiguration settings)
    {
        if (settings.DoorwayKnownProxies.Count == 0)
        {
            return;
        }

        var net = config.GetNetworkConfiguration();
        var list = KnownProxyList.Remove(net.KnownProxies, settings.DoorwayKnownProxies);
        if (list.Length != net.KnownProxies.Length)
        {
            net.KnownProxies = list;
            config.SaveConfiguration(NetworkConfigurationStore.StoreKey, net);
        }

        settings.DoorwayKnownProxies.Clear();
    }
}
