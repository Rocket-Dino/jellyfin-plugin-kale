using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kale.Api;
using Jellyfin.Plugin.Kale.Requests;
using Xunit;

namespace Jellyfin.Plugin.Kale.Tests;

/// <summary>
/// kale#347: a capped member's Coming Soon, and the unrated films <c>certificationLte</c> lets through.
/// Measured on the sandbox Seerr 2026-10-23: <c>/discover/movies/upcoming</c> refuses a certification
/// (400), and is <c>discover/movies</c> from today; <c>certificationLte=PG</c> alone admitted 10 unrated
/// films in 20 on Coming Soon, <c>certification=G|PG</c> none.
/// </summary>
public class ComingSoonCapTests
{
    private static CertificationLadder UsLadder() => new(new (string, int?)[]
    {
        ("G", 0), ("TV-G", 0), ("TV-Y", 0), ("TV-Y7", 7), ("PG", 10), ("TV-PG", 10),
        ("PG-13", 13), ("TV-14", 14), ("R", 17), ("TV-MA", 17), ("NC-17", 18), ("Unrated", null),
    });

    private static CertificationLadder AuLadder() => new(new (string, int?, int?)[]
    {
        ("G", 0, 0), ("Exempt", 0, 0), ("7+", 7, 0), ("10", 10, null), ("13", 13, null), ("14", 14, null),
        ("PG", 15, 1), ("M", 15, 2), ("MA", 15, 3), ("MA15+", 15, 3), ("MA 15+", 15, 3),
        ("16+", 16, 0), ("R", 18, 1), ("R18+", 18, 1), ("Unrated", null, null),
    });

    private static MemberRules Capped(string? unrated) => new(
        DiscoveryLimits.FromCustomPrefs(unrated is null
            ? new Dictionary<string, string?> { [DiscoveryLimits.CapKey] = "10" }
            : new Dictionary<string, string?> { [DiscoveryLimits.CapKey] = "10", [DiscoveryLimits.UnratedKey] = unrated }),
        null,
        UsLadder(),
        "US");

    [Fact]
    public void TheListIsEveryCertificateUpToTheCapsOwn()
    {
        Assert.Equal(new[] { "G", "PG" }, UsLadder().CertificationsUpTo("PG", MediaKind.Movie));
        Assert.Equal(new[] { "TV-G", "TV-Y7", "TV-PG" }, UsLadder().CertificationsUpTo("TV-PG", MediaKind.Series));
        // AU's PG and M share score 15; the sub-score keeps M out of a PG list.
        Assert.Equal(new[] { "G", "7+", "10", "13", "14", "PG" }, AuLadder().CertificationsUpTo("PG", MediaKind.Movie));
        Assert.Null(UsLadder().CertificationsUpTo("NR", MediaKind.Movie));
    }

    [Fact]
    public void ACappedMemberWhoHidesUnratedGetsTheList()
    {
        // A cap with no unrated key hides unrated: the default.
        Assert.Equal("G|PG", Capped(null).RatedOnly(MediaKind.Movie, "PG"));
        Assert.Equal("G|PG", Capped("false").RatedOnly(MediaKind.Movie, "PG"));
        Assert.Null(Capped("true").RatedOnly(MediaKind.Movie, "PG"));
        var uncapped = new MemberRules(DiscoveryLimits.FromCustomPrefs(new Dictionary<string, string?>()), null, UsLadder(), "US");
        Assert.Null(uncapped.RatedOnly(MediaKind.Movie, "PG"));
    }

    private static readonly DateTime Late = new(2026, 10, 23, 23, 30, 0, DateTimeKind.Utc);

    private static Dictionary<string, string> Query(List<KeyValuePair<string, string>> q) => q.ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public void ACappedUpcomingIsDiscoverFromTodayWithTheCapAndTheList()
    {
        var (path, q) = KaleRequestsController.BrowseCall(MediaKind.Movie, upcoming: true, new[] { KeyValuePair.Create("page", "1") }, "PG", "US", "G|PG", Late);
        Assert.Equal("api/v1/discover/movies", path);
        var query = Query(q);
        Assert.Equal("2026-10-23", query["primaryReleaseDateGte"]);
        Assert.Equal("PG", query["certificationLte"]);
        Assert.Equal("US", query["certificationCountry"]);
        Assert.Equal("G|PG", query["certification"]);
    }

    [Fact]
    public void AnUncappedUpcomingIsSeerrsOwnRouteAsBefore()
    {
        var (path, q) = KaleRequestsController.BrowseCall(MediaKind.Movie, upcoming: true, Array.Empty<KeyValuePair<string, string>>(), null, null, null, Late);
        Assert.Equal("api/v1/discover/movies/upcoming", path);
        Assert.Equal(new Dictionary<string, string> { ["page"] = "1" }, Query(q));
    }

    [Fact]
    public void TheAppCannotSendItsOwnListOrDateAroundTheCap()
    {
        var asked = new[]
        {
            KeyValuePair.Create("certification", "G|PG|PG-13|R|NR"),
            KeyValuePair.Create("certificationGte", "NR"),
            KeyValuePair.Create("primaryReleaseDateGte", "1900-01-01"),
            KeyValuePair.Create("sortBy", "popularity.desc"),
        };
        var query = Query(KaleRequestsController.BrowseCall(MediaKind.Movie, upcoming: true, asked, "PG", "US", "G|PG", Late).Query);
        Assert.Equal("G|PG", query["certification"]);
        Assert.False(query.ContainsKey("certificationGte"));
        Assert.Equal("2026-10-23", query["primaryReleaseDateGte"]);
        Assert.Equal("popularity.desc", query["sortBy"]);
    }

    [Fact]
    public void AnyCappedRowCarriesTheListAndKeepsItsRoute()
    {
        var (path, q) = KaleRequestsController.BrowseCall(MediaKind.Series, upcoming: false, new[] { KeyValuePair.Create("sortBy", "popularity.desc") }, "TV-PG", "US", "TV-G|TV-Y7|TV-PG", Late);
        Assert.Equal("api/v1/discover/tv", path);
        Assert.Equal("TV-G|TV-Y7|TV-PG", Query(q)["certification"]);
        Assert.False(Query(q).ContainsKey("primaryReleaseDateGte"));
    }
}
