using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.Kale.Share;
using Xunit;

namespace Jellyfin.Plugin.Kale.Tests;

/// <summary>ADR-020 §1c point 6, server-side: is any Kale Share guest treated as local, and what fixes it.</summary>
public class GuestLocalityTests
{
    private static readonly Guid Guest = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime T0 = new(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
    private static readonly IPAddress Router = IPAddress.Parse("10.20.0.28");
    private static readonly IPAddress Doorway = IPAddress.Parse("203.0.113.7");

    private static GuestLocalityBook Book(out List<GuestSighting> store)
    {
        store = new List<GuestSighting>();
        var book = new GuestLocalityBook(store);
        book.Know(new[] { Guest });
        return book;
    }

    [Fact]
    public void BeforeAnyGuestIsSeenTheAnswerIsUnknown()
    {
        Assert.Equal(GuestLocalityBook.Unknown, Book(out _).State());
        Assert.Equal(GuestLocalityBook.Unknown, new GuestLocalityBook(new()).State());
    }

    [Fact]
    public void OnlyGuestsAreWatched()
    {
        var book = Book(out var store);
        Assert.False(book.Observe(Member, "household", Router, local: true, trustLive: false, T0));
        Assert.Single(store);
        Assert.Equal(GuestLocalityBook.Unknown, book.State());
    }

    [Fact]
    public void ALocalSightingOutlivesTheSessionAndAsksForAnExclusion()
    {
        var book = Book(out var store);
        Assert.True(book.Observe(Guest, "sam", Router, local: true, trustLive: false, T0));
        Assert.Equal(GuestLocalityBook.GuestsLookLocal, book.State());
        Assert.Equal("10.20.0.28", store[0].LocalAddress);
        Assert.Equal(GuestLocalityFix.ExcludeAddress, GuestLocalityBook.FixFor(Router, false, configured: false, trustLive: false));

        // The session ends: nothing re-observes, and the sighting stays while the address is still local.
        Assert.False(book.ClearFixed(_ => true, trustLive: false));
        Assert.Equal(GuestLocalityBook.GuestsLookLocal, book.State());
    }

    [Fact]
    public void TheExclusionClearsItWithNoNewSighting()
    {
        var book = Book(out _);
        book.Observe(Guest, "sam", Router, local: true, trustLive: false, T0);
        Assert.True(book.ClearFixed(a => !a.Equals(Router), trustLive: false));
        Assert.Equal(GuestLocalityBook.Unknown, book.State()); // fixed, but this guest was never seen remote
        book.Observe(Guest, "sam", Router, local: false, trustLive: false, T0.AddMinutes(1));
        Assert.Equal(GuestLocalityBook.Ok, book.State());
    }

    [Fact]
    public void ALaterRemoteSightingClearsIt()
    {
        var book = Book(out var store);
        book.Observe(Guest, "sam", Router, local: true, trustLive: false, T0);
        Assert.True(book.Observe(Guest, "sam", Doorway, local: false, trustLive: true, T0.AddMinutes(1)));
        Assert.Equal(GuestLocalityBook.Ok, book.State());
        Assert.Null(store[0].LocalAddress);
    }

    [Fact]
    public void LoopbackBeforeTheRestartAsksForARestartAndTheRestartClearsIt()
    {
        var book = Book(out var store);
        book.Observe(Guest, "sam", IPAddress.Loopback, local: true, trustLive: false, T0);
        Assert.Equal(GuestLocalityFix.RestartJellyfin, GuestLocalityBook.FixFor(IPAddress.Loopback, false, configured: true, trustLive: false));
        Assert.Equal(GuestLocalityFix.EnableDoorway, GuestLocalityBook.FixFor(IPAddress.Loopback, false, configured: false, trustLive: false));

        Assert.False(book.ClearFixed(_ => true, trustLive: false)); // still not restarted
        Assert.True(book.ClearFixed(_ => true, trustLive: true));   // restarted: Known Proxies live
        Assert.Null(store[0].LocalAddress);
    }

    [Fact]
    public void LoopbackWhileTrustWasAlreadyLiveIsNotFixedByARestart()
    {
        var book = Book(out var store);
        book.Observe(Guest, "sam", IPAddress.Loopback, local: true, trustLive: true, T0);
        Assert.False(book.ClearFixed(_ => true, trustLive: true));
        Assert.Equal(GuestLocalityFix.Unknown, GuestLocalityBook.FixFor(IPAddress.Loopback, true, configured: true, trustLive: true));
        Assert.Equal("127.0.0.1", store[0].LocalAddress);
    }

    [Fact]
    public void SightingsSaveOnChangeNotOnEveryActivity()
    {
        var book = Book(out _);
        Assert.True(book.Observe(Guest, "sam", Doorway, local: false, trustLive: true, T0));
        Assert.False(book.Observe(Guest, "sam", Doorway, local: false, trustLive: true, T0.AddSeconds(30)));
        Assert.True(book.Observe(Guest, "sam", Doorway, local: false, trustLive: true, T0.Add(GuestLocalityBook.SaveEvery)));
        Assert.True(book.Observe(Guest, "sam", Router, local: true, trustLive: true, T0.AddMinutes(11)));
        Assert.False(book.Observe(Guest, "sam", Router, local: true, trustLive: true, T0.AddMinutes(12)));
    }

    [Fact]
    public void SightingsOrderByWhenTheyHappenedNotWhenTheyWereRead()
    {
        // One guest, two current sessions: a phone through the doorway (remote, older) and a laptop on a
        // raw relay (local, newer). Reading the older remote one last must not clear the newer local one.
        var book = Book(out var store);
        book.Observe(Guest, "sam", IPAddress.Loopback, local: true, trustLive: true, T0.AddMinutes(5));
        book.Observe(Guest, "sam", Doorway, local: false, trustLive: true, T0);
        Assert.Equal(GuestLocalityBook.GuestsLookLocal, book.State());
        Assert.Equal("127.0.0.1", store[0].LocalAddress);

        // And a stale local session read after a newer remote sighting doesn't reopen it.
        book.Observe(Guest, "sam", Doorway, local: false, trustLive: true, T0.AddMinutes(6));
        book.Observe(Guest, "sam", Router, local: true, trustLive: true, T0.AddMinutes(1));
        Assert.Equal(GuestLocalityBook.Ok, book.State());
    }

    private static GuestLocalityReport ReportWith(params (string Address, string Fix)[] sightings) =>
        new(GuestLocalityBook.GuestsLookLocal, 1,
            sightings.Select(x => new GuestLocalitySighting(Guest, "sam", x.Address, T0, x.Fix,
                x.Fix == GuestLocalityFix.ExcludeAddress ? "!" + x.Address : null)).ToList(),
            Array.Empty<GuestLocalityFixItem>(), "127.0.0.1", true, true);

    [Fact]
    public void AnExclusionIsAppendedAfterTheOwnersEntriesAndNeverDuplicated()
    {
        var owner = new[] { "10.20.0.0/22", "!10.20.0.40", "192.168.50.0/24" };
        var (list, added) = LanExclusions.Append(owner, Router);
        Assert.Equal(new[] { "10.20.0.0/22", "!10.20.0.40", "192.168.50.0/24", "!10.20.0.28" }, list);
        Assert.Equal("!10.20.0.28", added);

        var (again, none) = LanExclusions.Append(new[] { "a", " !10.20.0.28 " }, Router);
        Assert.Null(none);
        Assert.Equal(new[] { "a", " !10.20.0.28 " }, again);
        Assert.Equal("!10.20.0.28", LanExclusions.EntryFor(IPAddress.Parse("::ffff:10.20.0.28")));
    }

    [Fact]
    public void OnlyAnOpenExcludeAddressSightingMayBeExcluded()
    {
        var report = ReportWith(("10.20.0.28", GuestLocalityFix.ExcludeAddress), ("127.0.0.1", GuestLocalityFix.RestartJellyfin));
        Assert.Null(LanExclusions.Refusal(Router, IPAddress.Loopback, report));
        Assert.Null(LanExclusions.Refusal(IPAddress.Parse("::ffff:10.20.0.28"), IPAddress.Loopback, report));
        Assert.Equal(GuestLocalityExclusion.NotASighting, LanExclusions.Refusal(IPAddress.Parse("10.20.0.33"), IPAddress.Loopback, report));
        Assert.Equal(GuestLocalityExclusion.NotASighting, LanExclusions.Refusal(Router, IPAddress.Loopback, ReportWith()));
    }

    [Fact]
    public void LoopbackAndTheUpstreamAreNeverExcluded()
    {
        // Even when a (malformed) report would call for it.
        var report = ReportWith(("127.0.0.1", GuestLocalityFix.ExcludeAddress), ("::1", GuestLocalityFix.ExcludeAddress),
            ("10.20.0.28", GuestLocalityFix.Unknown));
        Assert.Equal(GuestLocalityExclusion.NotExcludable, LanExclusions.Refusal(IPAddress.Loopback, IPAddress.Loopback, report));
        Assert.Equal(GuestLocalityExclusion.NotExcludable, LanExclusions.Refusal(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, report));
        Assert.Equal(GuestLocalityExclusion.NotExcludable, LanExclusions.Refusal(Router, Router, ReportWith(("10.20.0.28", GuestLocalityFix.ExcludeAddress))));
        Assert.Equal(GuestLocalityExclusion.NotExcludable, LanExclusions.Refusal(Router, IPAddress.Loopback, report)); // fix isn't excludeAddress
    }

    [Fact]
    public void AGuestStaysKnownAfterItsInvitationIsPruned()
    {
        var book = Book(out var store);
        Assert.False(book.Know(Array.Empty<Guid>()));
        Assert.True(book.IsGuest(Guest));
        Assert.Single(store);
    }
}
