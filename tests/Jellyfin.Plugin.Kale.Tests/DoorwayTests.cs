using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Jellyfin.Plugin.Kale.Configuration;
using Jellyfin.Plugin.Kale.Share;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Xunit;

namespace Jellyfin.Plugin.Kale.Tests;

/// <summary>
/// kale#373: the doorway's Known Proxies entry is appended to the owner's list and removed alone; the
/// running server's trust is read from <see cref="ForwardedHeadersOptions"/>; and the doorway may run only
/// once that trust is live and no guest address is local.
/// </summary>
public class DoorwayTests
{
    private static readonly IPAddress V4 = IPAddress.Loopback;

    [Fact]
    public void AppendKeepsTheOwnersListAndAddsOneEntry()
    {
        var owner = new[] { "10.20.0.10", "proxy.lan", "192.168.0.0/16" };
        var (list, added) = KnownProxyList.Append(owner, V4);
        Assert.Equal(new[] { "10.20.0.10", "proxy.lan", "192.168.0.0/16", "127.0.0.1" }, list);
        Assert.Equal("127.0.0.1", added);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData(" 127.0.0.1 ")]
    [InlineData("127.0.0.1/32")]
    [InlineData("::ffff:127.0.0.1")]
    public void AnOwnersEntryForTheSameAddressIsNotDuplicatedAndNeverOurs(string ownersEntry)
    {
        var (list, added) = KnownProxyList.Append(new[] { ownersEntry }, V4);
        Assert.Equal(new[] { ownersEntry }, list);
        Assert.Null(added);
    }

    [Fact]
    public void ASubnetIsNeverTreatedAsNamingTheAddress()
    {
        // The plugin only ever adds a single address; an owner's 127.0.0.0/8 doesn't make one "ours".
        var (list, added) = KnownProxyList.Append(new[] { "127.0.0.0/8" }, V4);
        Assert.Equal("127.0.0.1", added);
        Assert.Equal(2, list.Length);
    }

    [Fact]
    public void RemoveTakesOnlyOurEntryAndKeepsTheOwnersInOrder()
    {
        var list = new[] { "10.20.0.10", "127.0.0.1", "proxy.lan" };
        Assert.Equal(new[] { "10.20.0.10", "proxy.lan" }, KnownProxyList.Remove(list, new[] { "127.0.0.1" }));
        Assert.Equal(list, KnownProxyList.Remove(list, Array.Empty<string>()));
        // The owner had listed it too (twice in all): one goes, theirs stays.
        Assert.Equal(new[] { "127.0.0.1", "x" }, KnownProxyList.Remove(new[] { "127.0.0.1", "x", "127.0.0.1" }, new[] { "127.0.0.1" }));
    }

    [Fact]
    public void UpstreamIsIPv6LoopbackOnlyWhenIPv4IsOff()
    {
        Assert.Equal(IPAddress.Loopback, DoorwayAddresses.Upstream(new NetworkConfiguration { EnableIPv4 = true }));
        Assert.Equal(IPAddress.IPv6Loopback, DoorwayAddresses.Upstream(new NetworkConfiguration { EnableIPv4 = false, EnableIPv6 = true }));
    }

    [Fact]
    public void GuestHostsAreTheWholeDocumentationRangeAndNothingElse()
    {
        var hosts = DoorwayAddresses.GuestHosts().ToList();
        Assert.Equal(254, hosts.Count);
        Assert.Equal("203.0.113.1", hosts[0].ToString());
        Assert.Equal("203.0.113.254", hosts[^1].ToString());
    }

    /// <summary>What Jellyfin's ConfigureForwardHeaders does with its startup Known Proxies (10.11.11 and 12.4 read alike).</summary>
    private static ForwardedHeadersOptions AsJellyfinStarts(params string[] knownProxies)
    {
        var o = new ForwardedHeadersOptions();
        if (knownProxies.Length == 0)
        {
            o.ForwardedHeaders = ForwardedHeaders.None;
#if JF_10_11
            o.KnownNetworks.Clear();
#else
            o.KnownIPNetworks.Clear();
#pragma warning disable ASPDEPR005, CS0618
            o.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005, CS0618
#endif
            o.KnownProxies.Clear();
        }
        else
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            foreach (var p in knownProxies)
            {
                o.KnownProxies.Add(IPAddress.Parse(p));
            }
        }

        return o;
    }

    [Fact]
    public void EmptyKnownProxiesAtStartupTrustsNobody()
    {
        Assert.False(ForwardingTrust.Trusts(AsJellyfinStarts(), V4));
        Assert.False(ForwardingTrust.Trusts(AsJellyfinStarts(), IPAddress.IPv6Loopback));
    }

    [Fact]
    public void OurEntryAtStartupIsLive()
    {
        Assert.True(ForwardingTrust.Trusts(AsJellyfinStarts("127.0.0.1"), V4));
        Assert.True(ForwardingTrust.Trusts(AsJellyfinStarts("127.0.0.1"), IPAddress.Parse("::ffff:127.0.0.1")));
    }

    [Fact]
    public void AnyOtherKnownProxyAlsoLeavesAspNetsLoopbackDefaultsTrusted()
    {
        // Measured fact of ASP.NET + Jellyfin, pinned so a change shows: Jellyfin keeps the options'
        // default loopback entries when Known Proxies is non-empty, so loopback is trusted on ANY server
        // whose owner lists a proxy. (Why a raw TCP relay on loopback is never acceptable.)
        Assert.True(ForwardingTrust.Trusts(AsJellyfinStarts("10.20.0.10"), V4));
        Assert.True(ForwardingTrust.Trusts(AsJellyfinStarts("10.20.0.10"), IPAddress.IPv6Loopback));
        Assert.False(ForwardingTrust.Trusts(AsJellyfinStarts("10.20.0.10"), IPAddress.Parse("10.20.0.11")));
    }

    [Fact]
    public void TrustNeedsTheXForwardedForFlag()
    {
        var o = AsJellyfinStarts("127.0.0.1");
        o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        Assert.False(ForwardingTrust.Trusts(o, V4));
    }

    [Theory]
    [InlineData(3, true, true, true, DoorwayStatus.GuestsLookLocal)]
    [InlineData(3, false, false, false, DoorwayStatus.GuestsLookLocal)]
    [InlineData(0, false, true, true, DoorwayStatus.Off)]
    [InlineData(0, true, false, true, DoorwayStatus.ProxyMissing)]
    [InlineData(0, true, true, false, DoorwayStatus.RestartNeededOutcome)]
    [InlineData(0, true, true, true, DoorwayStatus.Live)]
    public void TheDoorwayMayRunOnlyWhenLiveAndNoGuestIsLocal(int local, bool enabled, bool configured, bool live, string outcome)
    {
        var s = DoorwayStatus.Compute(enabled, configured, live, local, null, V4);
        Assert.Equal(outcome, s.Outcome);
        Assert.Equal(outcome == DoorwayStatus.Live, s.CanStart);
        Assert.Equal(outcome == DoorwayStatus.RestartNeededOutcome, s.RestartNeeded);
        Assert.Equal("203.0.113.0/24", s.GuestNetwork);
        Assert.Equal("127.0.0.1", s.Upstream);
    }

    [Fact]
    public void ReleaseRemovesOnlyOurEntriesAndForgetsThem()
    {
        var net = new NetworkConfiguration { KnownProxies = new[] { "10.20.0.10", "127.0.0.1" } };
        var config = FakeConfig.Create(net);
        var settings = new PluginConfiguration { DoorwayKnownProxies = new() { "127.0.0.1" } };

        Doorway.ReleaseKnownProxies(config, settings);

        Assert.Equal(new[] { "10.20.0.10" }, net.KnownProxies);
        Assert.Empty(settings.DoorwayKnownProxies);
        Assert.Equal(1, FakeConfig.Saves(config));

        Doorway.ReleaseKnownProxies(config, settings); // nothing of ours left: no write
        Assert.Equal(1, FakeConfig.Saves(config));
    }

    [Fact]
    public void ReleaseLeavesAnOwnersEntryWhenKaleAddedNone()
    {
        var net = new NetworkConfiguration { KnownProxies = new[] { "127.0.0.1" } };
        var config = FakeConfig.Create(net);
        Doorway.ReleaseKnownProxies(config, new PluginConfiguration());
        Assert.Equal(new[] { "127.0.0.1" }, net.KnownProxies);
        Assert.Equal(0, FakeConfig.Saves(config));
    }

    /// <summary>Just enough IServerConfigurationManager: the network configuration, and a count of saves.</summary>
    public class FakeConfig : DispatchProxy
    {
        private NetworkConfiguration _net = new();
        private int _saves;

        public static IServerConfigurationManager Create(NetworkConfiguration net)
        {
            var proxy = DispatchProxy.Create<IServerConfigurationManager, FakeConfig>();
            ((FakeConfig)(object)proxy)._net = net;
            return proxy;
        }

        public static int Saves(IServerConfigurationManager c) => ((FakeConfig)(object)c)._saves;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "GetConfiguration" when Equals(args?[0], NetworkConfigurationStore.StoreKey):
                    return _net;
                case "SaveConfiguration" when args?.Length == 2:
                    _net = (NetworkConfiguration)args[1]!;
                    _saves++;
                    return null;
                default:
                    throw new NotSupportedException(method?.Name);
            }
        }
    }
}
