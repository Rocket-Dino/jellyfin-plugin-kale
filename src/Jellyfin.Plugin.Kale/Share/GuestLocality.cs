using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kale.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kale.Share;

/// <summary>
/// What the plugin remembers about one Kale Share guest's locality (ADR-020 §1c point 6). Kept in the
/// plugin's configuration so a sighting outlives the session that produced it and a restart.
/// </summary>
public sealed class GuestSighting
{
    public Guid UserId { get; set; }

    public string? UserName { get; set; }

    /// <summary>The last address Jellyfin classed as LOCAL for this guest. Null = no open sighting.</summary>
    public string? LocalAddress { get; set; }

    public DateTime? LocalSeenUtc { get; set; }

    /// <summary>Whether the running server trusted the doorway's upstream when the local sighting was made.</summary>
    public bool TrustLiveWhenSeen { get; set; }

    /// <summary>The last time Jellyfin classed this guest as remote.</summary>
    public DateTime? RemoteSeenUtc { get; set; }

    /// <summary>When this entry last asked to be saved: last-seen times are saved at most every <see cref="GuestLocalityBook.SaveEvery"/>.</summary>
    public DateTime? SavedUtc { get; set; }
}

/// <summary>The fix a local sighting calls for. The plugin applies none of them.</summary>
public static class GuestLocalityFix
{
    /// <summary>Append <c>!&lt;address&gt;</c> to Jellyfin's LAN networks (<c>LocalNetworkSubnets</c>): live, no restart.</summary>
    public const string ExcludeAddress = "excludeAddress";

    /// <summary>The guest arrived on loopback (the doorway's upstream) and Known Proxies is saved but not live: restart Jellyfin.</summary>
    public const string RestartJellyfin = "restartJellyfin";

    /// <summary>The guest arrived on loopback and Known Proxies doesn't name the upstream: turn the doorway on (then restart).</summary>
    public const string EnableDoorway = "enableDoorway";

    /// <summary>
    /// Loopback while the upstream WAS already trusted: something forwarded the guest without the
    /// doorway's header (a raw TCP relay, or a client on the server itself). No setting fixes it.
    /// </summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// The guests' locality, apart from Jellyfin so it can be tested on its own. Not thread-safe: the
/// watcher serialises calls.
/// </summary>
public sealed class GuestLocalityBook
{
    /// <summary>How stale a persisted last-seen time may get before a sighting saves again.</summary>
    public static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(10);

    public const string Ok = "ok";
    public const string GuestsLookLocal = "guestsLookLocal";
    public const string Unknown = "unknown";

    private readonly List<GuestSighting> _book;

    public GuestLocalityBook(List<GuestSighting> book) => _book = book;

    public IReadOnlyList<GuestSighting> Entries => _book;

    /// <summary>Every invited guest gets an entry, so a guest stays known after its invitation is pruned.</summary>
    public bool Know(IEnumerable<Guid> guests)
    {
        var changed = false;
        foreach (var id in guests.Where(g => g != Guid.Empty))
        {
            if (Find(id) is null)
            {
                _book.Add(new GuestSighting { UserId = id });
                changed = true;
            }
        }

        return changed;
    }

    public bool IsGuest(Guid userId) => Find(userId) is not null;

    /// <summary>
    /// One sighting of a guest, classified by Jellyfin, at <paramref name="seenUtc"/> (the session's own
    /// last activity, so sightings order by when they happened, not by when they were read: a guest may
    /// have several sessions at once). A local sighting older than the last remote one is history; a
    /// remote one clears a local sighting no newer than itself. Returns whether the book should be saved.
    /// </summary>
    public bool Observe(Guid userId, string? userName, IPAddress address, bool local, bool trustLive, DateTime seenUtc)
    {
        var g = Find(userId);
        if (g is null)
        {
            return false;
        }

        var changed = false;
        if (!string.IsNullOrEmpty(userName) && g.UserName != userName)
        {
            g.UserName = userName;
            changed = true;
        }

        var text = Normal(address).ToString();
        if (local)
        {
            if ((g.RemoteSeenUtc is { } r && seenUtc < r) || (g.LocalSeenUtc is { } l && seenUtc < l))
            {
                return changed;
            }

            changed |= g.LocalAddress != text || g.TrustLiveWhenSeen != trustLive;
            g.LocalAddress = text;
            g.LocalSeenUtc = seenUtc;
            g.TrustLiveWhenSeen = trustLive;
        }
        else
        {
            if (g.LocalSeenUtc is { } l && seenUtc >= l)
            {
                g.LocalAddress = null;
                g.LocalSeenUtc = null;
                g.TrustLiveWhenSeen = false;
                changed = true;
            }

            if (g.RemoteSeenUtc is null || seenUtc > g.RemoteSeenUtc)
            {
                changed |= g.RemoteSeenUtc is null;
                g.RemoteSeenUtc = seenUtc;
            }
        }

        changed |= Stale(g.SavedUtc, seenUtc);
        if (changed)
        {
            g.SavedUtc = seenUtc;
        }

        return changed;
    }

    /// <summary>
    /// Clear sightings a fix has since dealt with: an address Jellyfin no longer classes as local (the
    /// exclusion was added), or a loopback sighting made before the upstream was trusted, now that it is
    /// (Jellyfin was restarted). Returns whether anything was cleared.
    /// </summary>
    public bool ClearFixed(Func<IPAddress, bool> isLocal, bool trustLive)
    {
        var changed = false;
        foreach (var g in _book)
        {
            if (g.LocalAddress is null || !IPAddress.TryParse(g.LocalAddress, out var a))
            {
                continue;
            }

            var fixedNow = IPAddress.IsLoopback(a) ? trustLive && !g.TrustLiveWhenSeen : !isLocal(a);
            if (fixedNow)
            {
                g.LocalAddress = null;
                g.LocalSeenUtc = null;
                g.TrustLiveWhenSeen = false;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>The fix one open sighting calls for.</summary>
    public static string FixFor(IPAddress address, bool trustLiveWhenSeen, bool configured, bool trustLive)
    {
        if (!IPAddress.IsLoopback(address))
        {
            return GuestLocalityFix.ExcludeAddress;
        }

        if (trustLiveWhenSeen && trustLive)
        {
            return GuestLocalityFix.Unknown;
        }

        return configured ? GuestLocalityFix.RestartJellyfin : GuestLocalityFix.EnableDoorway;
    }

    /// <summary><c>ok</c> once a guest has been seen remote and none is open as local; <c>unknown</c> before any guest is seen.</summary>
    public string State() =>
        _book.Any(g => g.LocalAddress is not null) ? GuestsLookLocal
        : _book.Any(g => g.RemoteSeenUtc is not null) ? Ok
        : Unknown;

    private GuestSighting? Find(Guid id) => _book.FirstOrDefault(g => g.UserId == id);

    private static bool Stale(DateTime? saved, DateTime now) => saved is null || now - saved.Value >= SaveEvery;

    private static IPAddress Normal(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}

/// <summary>The answer of <c>GET /Kale/Share/GuestLocality</c>.</summary>
public sealed record GuestLocalityReport(
    string State,
    int Guests,
    IReadOnlyList<GuestLocalitySighting> Sightings,
    IReadOnlyList<GuestLocalityFixItem> Fixes,
    string Upstream,
    bool KnownProxyConfigured,
    bool KnownProxyLive);

public sealed record GuestLocalitySighting(Guid UserId, string? UserName, string Address, DateTime LastSeenUtc, string Fix, string? Exclusion);

public sealed record GuestLocalityFixItem(string Fix, string? Address, string? Exclusion);

/// <summary>The answer of <c>POST /Kale/Share/GuestLocality/Exclude</c>: what happened, and the fresh report.</summary>
public sealed record GuestLocalityExclusion(string Outcome, string? Exclusion, GuestLocalityReport GuestLocality)
{
    /// <summary><c>!&lt;address&gt;</c> appended to LAN networks.</summary>
    public const string Excluded = "excluded";

    /// <summary>LAN networks already held that entry: nothing written.</summary>
    public const string AlreadyExcluded = "alreadyExcluded";

    /// <summary>Not an IP address.</summary>
    public const string BadAddress = "badAddress";

    /// <summary>No open sighting of a guest at that address.</summary>
    public const string NotASighting = "notASighting";

    /// <summary>Loopback, the doorway's upstream, or a sighting whose fix is not <c>excludeAddress</c>.</summary>
    public const string NotExcludable = "notExcludable";
}

/// <summary>Appending a LAN-network exclusion: the owner's entries are kept, in order, and never duplicated.</summary>
public static class LanExclusions
{
    public static string EntryFor(IPAddress address) => "!" + Normal(address);

    /// <summary>The list with <c>!&lt;address&gt;</c> appended, and the entry added (null when it was already there).</summary>
    public static (string[] List, string? Added) Append(string[] existing, IPAddress address)
    {
        var entry = EntryFor(address);
        return existing.Any(e => string.Equals(e.Trim(), entry, StringComparison.OrdinalIgnoreCase))
            ? (existing, null)
            : (existing.Append(entry).ToArray(), entry);
    }

    /// <summary>Why <paramref name="address"/> may not be excluded (a <see cref="GuestLocalityExclusion"/> code), or null.</summary>
    public static string? Refusal(IPAddress address, IPAddress upstream, GuestLocalityReport report)
    {
        address = Normal(address);
        if (IPAddress.IsLoopback(address) || address.Equals(Normal(upstream)))
        {
            return GuestLocalityExclusion.NotExcludable; // and Jellyfin ignores exclusions for loopback anyway
        }

        var sighting = report.Sightings.FirstOrDefault(s => IPAddress.TryParse(s.Address, out var a) && Normal(a).Equals(address));
        if (sighting is null)
        {
            return GuestLocalityExclusion.NotASighting;
        }

        return sighting.Fix == GuestLocalityFix.ExcludeAddress ? null : GuestLocalityExclusion.NotExcludable;
    }

    private static IPAddress Normal(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}

/// <summary>
/// Watches Jellyfin's sessions for Kale Share guests and classifies each with Jellyfin's own
/// <see cref="INetworkManager.IsInLocalNetwork(IPAddress)"/>: on every session start and activity
/// (Jellyfin raises activity at most every 10 s per session), and again over the current sessions when
/// an admin asks. Guests are the accounts the owner's invitations were made for
/// (<see cref="ShareGrant"/> with no <c>FromDevice</c>), remembered past the invitation's pruning.
/// It never changes Jellyfin's settings.
/// </summary>
public sealed class GuestLocality : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly INetworkManager _network;
    private readonly IServerConfigurationManager _config;
    private readonly Doorway _doorway;
    private readonly ILogger<GuestLocality> _logger;
    private readonly object _gate = new();

    public GuestLocality(ISessionManager sessions, INetworkManager network, IServerConfigurationManager config, Doorway doorway,
        ILogger<GuestLocality> logger)
    {
        _sessions = sessions;
        _network = network;
        _config = config;
        _doorway = doorway;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.SessionStarted += OnSession;
        _sessions.SessionActivity += OnSession;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.SessionStarted -= OnSession;
        _sessions.SessionActivity -= OnSession;
        return Task.CompletedTask;
    }

    private void OnSession(object? sender, SessionEventArgs e)
    {
        try
        {
            if (e.SessionInfo is { } s)
            {
                lock (_gate)
                {
                    WithBook(book => Observe(book, s, _doorway.TrustLive()));
                }
            }
        }
        catch (Exception ex)
        {
            // Never let a bookkeeping failure reach Jellyfin's session code.
            _logger.LogWarning(ex, "Kale Share: guest locality check failed");
        }
    }

    /// <summary>Re-check every current session, clear what has been fixed, and report.</summary>
    public GuestLocalityReport Report()
    {
        lock (_gate)
        {
            var net = _config.GetNetworkConfiguration();
            var upstream = DoorwayAddresses.Upstream(net);
            var configured = KnownProxyList.Lists(net.KnownProxies, upstream);
            var live = _doorway.TrustLive();
            GuestLocalityReport? report = null;
            WithBook(book =>
            {
                var changed = false;
                foreach (var s in _sessions.Sessions)
                {
                    changed |= Observe(book, s, live);
                }

                changed |= book.ClearFixed(_network.IsInLocalNetwork, live);
                var sightings = book.Entries
                    .Where(g => g.LocalAddress is not null && g.LocalSeenUtc is not null)
                    .Select(g =>
                    {
                        var a = IPAddress.Parse(g.LocalAddress!);
                        var fix = GuestLocalityBook.FixFor(a, g.TrustLiveWhenSeen, configured, live);
                        return new GuestLocalitySighting(g.UserId, g.UserName, g.LocalAddress!, g.LocalSeenUtc!.Value, fix,
                            fix == GuestLocalityFix.ExcludeAddress ? "!" + g.LocalAddress : null);
                    })
                    .OrderByDescending(s => s.LastSeenUtc)
                    .ToList();
                var fixes = sightings
                    .Select(s => new GuestLocalityFixItem(s.Fix, s.Fix == GuestLocalityFix.ExcludeAddress ? s.Address : null, s.Exclusion))
                    .Distinct()
                    .ToList();
                report = new GuestLocalityReport(book.State(), book.Entries.Count, sightings, fixes, upstream.ToString(), configured, live);
                return changed;
            });
            return report!;
        }
    }

    /// <summary>
    /// Apply an <c>excludeAddress</c> fix: a server-side read-merge-write of Jellyfin's network configuration
    /// through its own configuration manager, appending <c>!&lt;address&gt;</c> to LAN networks (never replacing or
    /// reordering an entry; nothing written if it is already there). Only for an address that is an OPEN
    /// sighting whose fix is <c>excludeAddress</c>; never loopback or the doorway's upstream. Jellyfin applies
    /// it live (its network manager re-reads on the save), and the answer carries the report made after it.
    /// </summary>
    public (GuestLocalityExclusion Result, bool Refused) Exclude(string? address)
    {
        lock (_gate)
        {
            if (!IPAddress.TryParse(address?.Trim(), out var a))
            {
                return (new GuestLocalityExclusion(GuestLocalityExclusion.BadAddress, null, Report()), true);
            }

            if (a.IsIPv4MappedToIPv6)
            {
                a = a.MapToIPv4();
            }

            var before = Report();
            var net = _config.GetNetworkConfiguration();
            var refusal = LanExclusions.Refusal(a, DoorwayAddresses.Upstream(net), before);
            if (refusal is not null)
            {
                return (new GuestLocalityExclusion(refusal, null, before), true);
            }

            var (list, added) = LanExclusions.Append(net.LocalNetworkSubnets, a);
            if (added is not null)
            {
                // Record first, as for Known Proxies: a failure between the two leaves a record of nothing.
                if (Plugin.Instance is { } plugin)
                {
                    if (!plugin.Configuration.LanExclusionsAdded.Contains(added, StringComparer.OrdinalIgnoreCase))
                    {
                        plugin.Configuration.LanExclusionsAdded.Add(added);
                    }

                    plugin.SaveConfiguration();
                }

                net.LocalNetworkSubnets = list;
                _config.SaveConfiguration(NetworkConfigurationStore.StoreKey, net);
                _logger.LogInformation("Kale Share: added {Entry} to Jellyfin's LAN networks (a guest was classed local there)", added);
            }

            return (new GuestLocalityExclusion(
                added is null ? GuestLocalityExclusion.AlreadyExcluded : GuestLocalityExclusion.Excluded,
                LanExclusions.EntryFor(a),
                Report()), false);
        }
    }

    private bool Observe(GuestLocalityBook book, SessionInfo s, bool trustLive)
    {
        if (s.UserId == Guid.Empty || !book.IsGuest(s.UserId) || !IPAddress.TryParse(s.RemoteEndPoint, out var address))
        {
            return false;
        }

        var local = _network.IsInLocalNetwork(address);
        if (local)
        {
            _logger.LogWarning("Kale Share: guest {User} is classed LOCAL from {Address}", s.UserId, address);
        }

        var seen = s.LastActivityDate == default ? DateTime.UtcNow : DateTime.SpecifyKind(s.LastActivityDate, DateTimeKind.Utc);
        return book.Observe(s.UserId, s.UserName, address, local, trustLive, seen);
    }

    private static void WithBook(Func<GuestLocalityBook, bool> use)
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration ?? new PluginConfiguration();
        var book = new GuestLocalityBook(config.GuestSightings);
        var changed = book.Know(config.ShareGrants.Where(g => g.FromDevice is null).Select(g => g.UserId));
        changed |= use(book);
        if (changed && plugin is not null)
        {
            plugin.SaveConfiguration();
        }
    }
}
