using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Kale.Requests;
using Xunit;

namespace Jellyfin.Plugin.Kale.Tests;

/// <summary>
/// The broker's half of Kale's discovery limits (kale#308). These are ports of the app's
/// <c>DiscoveryLimits</c>, <c>DiscoveryScope.applied</c> and <c>CertificationLadder</c>, and the cases
/// mirror the app's own tests so the two can't drift apart unnoticed.
/// </summary>
public class DiscoveryRulesTests
{
    private static Dictionary<string, string?> Prefs(params (string Key, string? Value)[] pairs)
    {
        var d = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs)
        {
            d[k] = v;
        }

        return d;
    }

    // The US ladder as Jellyfin publishes it (a subset: enough to have variants and two kinds).
    private static CertificationLadder UsLadder() => new(new (string, int?)[]
    {
        ("G", 0), ("TV-G", 0), ("TV-Y", 0), ("TV-Y7", 7), ("PG", 10), ("TV-PG", 10),
        ("PG-13", 13), ("TV-14", 14), ("R", 17), ("TV-MA", 17), ("NC-17", 18), ("Unrated", null),
    });

    // The AU ladder as 12.1 and 10.11.11 publish it with the server set to AU (measured 2026-10-11):
    // PG, M and MA15+ share score 15 and only the sub-score tells them apart (kale#311).
    private static CertificationLadder AuLadder() => new(new (string, int?, int?)[]
    {
        ("G", 0, 0), ("Exempt", 0, 0), ("7+", 7, 0), ("10", 10, null), ("13", 13, null), ("14", 14, null),
        ("PG", 15, 1), ("M", 15, 2), ("MA", 15, 3), ("MA15+", 15, 3), ("MA 15+", 15, 3),
        ("16+", 16, 0), ("R", 18, 1), ("R18+", 18, 1), ("Unrated", null, null),
    });

    [Fact]
    public void NoKeysIsUnsetAndSearchStaysOn()
    {
        var limits = DiscoveryLimits.FromCustomPrefs(Prefs(("homesection0", "smalllibrarytiles")));
        Assert.False(limits.IsStored);
        Assert.Null(limits.MaxRatingScore);
        Assert.True(limits.ShowsUnrated);
        Assert.True(limits.SearchesBeyondLibrary);
    }

    [Fact]
    public void ACapWithoutTheUnratedKeyHidesUnrated()
    {
        var limits = DiscoveryLimits.FromCustomPrefs(Prefs((DiscoveryLimits.CapKey, "10")));
        Assert.True(limits.IsStored);
        Assert.Equal(10, limits.MaxRatingScore);
        Assert.False(limits.ShowsUnrated);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("garbled", true)]
    [InlineData(null, true)]
    public void OnlyTheLiteralFalseTurnsSearchOff(string? value, bool expected)
    {
        var limits = DiscoveryLimits.FromCustomPrefs(Prefs((DiscoveryLimits.SearchKey, value)));
        Assert.Equal(expected, limits.SearchesBeyondLibrary);
        // Search-off alone is not an answer to the rating question.
        Assert.False(limits.IsStored);
    }

    [Fact]
    public void UnsetFallsBackToTheLibraryCap()
    {
        var scope = DiscoveryScope.Applied(DiscoveryLimits.FromCustomPrefs(Prefs()), libraryMaxScore: 13);
        Assert.Equal(13, scope.MaxRatingScore);
        Assert.False(scope.ShowsUnrated);
        Assert.True(scope.AdmitsNothingFromSearch);
    }

    [Fact]
    public void AStoredCapCanOnlyTightenTheLibraryCap()
    {
        var looser = DiscoveryLimits.FromCustomPrefs(Prefs((DiscoveryLimits.CapKey, "17"), (DiscoveryLimits.UnratedKey, "true")));
        var scope = DiscoveryScope.Applied(looser, libraryMaxScore: 10);
        Assert.Equal(10, scope.MaxRatingScore);
        // A library cap hides unrated, and a stored "show unrated" can't switch it back on.
        Assert.False(scope.ShowsUnrated);
    }

    [Fact]
    public void NoCapAnywhereAdmitsEverything()
    {
        var scope = DiscoveryScope.Applied(DiscoveryLimits.FromCustomPrefs(Prefs()), libraryMaxScore: null);
        Assert.Null(scope.MaxRatingScore);
        Assert.False(scope.AdmitsNothingFromSearch);
        Assert.True(scope.Admits(null, UsLadder().LevelsByName));
        Assert.True(scope.Admits("NC-17", UsLadder().LevelsByName));
    }

    [Fact]
    public void ACappedScopeAdmitsByScoreAndTreatsUnknownNamesAsUnrated()
    {
        var scope = new DiscoveryScope(10, ShowsUnrated: false);
        var names = UsLadder().LevelsByName;
        Assert.True(scope.Admits("PG", names));
        Assert.False(scope.Admits("PG-13", names));
        Assert.False(scope.Admits(null, names));
        Assert.False(scope.Admits("M15+", names)); // not on this ladder: unknown, so unrated
        Assert.True(new DiscoveryScope(10, ShowsUnrated: true).Admits(null, names));
    }

    [Fact]
    public void TheLadderPicksTheCanonicalNamePerKindAndFallsDownward()
    {
        var ladder = UsLadder();
        Assert.Equal("PG", ladder.CertificationFor(10, MediaKind.Movie));
        Assert.Equal("TV-PG", ladder.CertificationFor(10, MediaKind.Series));
        // Ties at score 0 break alphabetically: TV-G before TV-Y.
        Assert.Equal("TV-G", ladder.CertificationFor(0, MediaKind.Series));
        // No film certificate at 14: fall DOWN to PG-13, never up to R.
        Assert.Equal("PG-13", ladder.CertificationFor(14, MediaKind.Movie));
        Assert.Null(new CertificationLadder(new (string, int?)[] { ("R", 17) }).CertificationFor(10, MediaKind.Movie));
        Assert.Equal(13, ladder.LevelsByName["PG-13"].Score);
        Assert.False(ladder.LevelsByName.ContainsKey("Unrated"));
    }

    [Fact]
    public void APgCapIsFifteenPointOneAndRefusesM()
    {
        // The library cap jellyfin-web (and now Kale) writes for PG.
        var rules = new MemberRules(DiscoveryLimits.FromCustomPrefs(Prefs()), libraryMaxScore: 15, AuLadder(), "AU", librarySubScore: 1);
        Assert.Equal("PG", rules.Browse(MediaKind.Movie, null, null).Certification);
        // Asking for M under it is widening, so the server's PG goes out instead.
        Assert.Equal("PG", rules.Browse(MediaKind.Movie, "M", "AU").Certification);
        Assert.Null(rules.RefuseDetail("PG"));
        Assert.Equal("aboveLimit", rules.RefuseDetail("M")?.Outcome);
        Assert.Equal("aboveLimit", rules.RefuseRequest("MA15+")?.Outcome);
        Assert.Null(rules.RefuseRequest("G"));
    }

    [Fact]
    public void TheDiscoverySubScoreIsReadFromItsOwnKeyAndTightensTheCap()
    {
        var limits = DiscoveryLimits.FromCustomPrefs(Prefs((DiscoveryLimits.CapKey, "15"), (DiscoveryLimits.SubKey, "1")));
        Assert.Equal(new RatingLevel(15, 1), limits.Cap);
        // Under a library MA15+ (15.3) the stored PG is the stricter: a score alone calls it a tie.
        var scope = DiscoveryScope.Applied(limits, libraryMaxScore: 15, librarySubScore: 3);
        Assert.Equal(new RatingLevel(15, 1), scope.Cap);
        // A sub-score with no cap is not a cap.
        Assert.Null(DiscoveryLimits.FromCustomPrefs(Prefs((DiscoveryLimits.SubKey, "1"))).Cap);
    }

    [Fact]
    public void ABareFifteenSavedBeforeSubScoresBrowsesAtPg()
    {
        // Kale before kale#311 stored AU's PG as 15. Falling below the server's own reading of that
        // would send the generic 14, which TMDB's AU list lacks; it used to send M.
        var rules = new MemberRules(DiscoveryLimits.FromCustomPrefs(Prefs((DiscoveryLimits.CapKey, "15"))), null, AuLadder(), "AU");
        Assert.Equal("PG", rules.Browse(MediaKind.Movie, null, null).Certification);
        Assert.Equal("aboveLimit", rules.RefuseDetail("M")?.Outcome);
    }

    [Fact]
    public void ContentDescriptorVariantsAreNeverSent()
    {
        var us = new CertificationLadder(new (string, int?, int?)[]
        {
            ("TV-PG", 10, 0), ("TV-PG-D", 10, 1), ("TV-14", 14, 0), ("TV-14-D", 14, 1), ("R", 17, 0), ("NC-17", 17, 1), ("TV-MA", 17, 1),
        });
        Assert.Equal("TV-PG", us.CertificationFor(new RatingLevel(10, 1), MediaKind.Series));
        Assert.Equal("TV-14", us.CertificationFor(new RatingLevel(14, 1), MediaKind.Series));
        Assert.Equal("R", us.CertificationFor(new RatingLevel(17, 0), MediaKind.Movie));
        Assert.Equal("TV-MA", us.CertificationFor(17, MediaKind.Series));
    }

    [Fact]
    public void CertificationIsReadForTheCountryFromBothShapes()
    {
        var movie = JsonDocument.Parse("""
            {"releases":{"results":[
              {"iso_3166_1":"AU","release_dates":[{"certification":"M"}]},
              {"iso_3166_1":"US","release_dates":[{"certification":""},{"certification":"PG-13"}]}]}}
            """).RootElement;
        Assert.Equal("PG-13", SeerrCertification.From(movie, "US"));
        Assert.Equal("M", SeerrCertification.From(movie, "AU"));
        Assert.Null(SeerrCertification.From(movie, "GB"));

        var tv = JsonDocument.Parse("""
            {"contentRatings":{"results":[{"iso_3166_1":"US","rating":"TV-14"}]}}
            """).RootElement;
        Assert.Equal("TV-14", SeerrCertification.From(tv, "US"));
        Assert.Null(SeerrCertification.From(JsonDocument.Parse("{}").RootElement, "US"));
    }

    [Fact]
    public void SearchOffRefusesSearchAndBrowseButNotDetailOrRequest()
    {
        var rules = new MemberRules(DiscoveryLimits.FromCustomPrefs(Prefs((DiscoveryLimits.SearchKey, "false"))), null, UsLadder(), "US");
        Assert.False(rules.SearchAllowed);
        Assert.False(rules.BrowseAllowed);
        Assert.Equal("searchOff", rules.RefuseSearch()?.Outcome);
        Assert.Equal("searchOff", rules.Browse(MediaKind.Movie, null, null).Refusal?.Outcome);
        Assert.Null(rules.RefuseDetail("NC-17"));
        Assert.Null(rules.RefuseRequest("NC-17"));
    }

    [Fact]
    public void ACappedMemberCannotSearchAndBrowsesUnderTheServersCertificate()
    {
        var rules = new MemberRules(DiscoveryLimits.FromCustomPrefs(Prefs((DiscoveryLimits.CapKey, "10"))), null, UsLadder(), "US");
        Assert.False(rules.SearchAllowed);
        Assert.True(rules.BrowseAllowed);
        Assert.Equal("searchCapped", rules.RefuseSearch()?.Outcome);

        // The app sent nothing: the server's own certificate and country go out.
        var none = rules.Browse(MediaKind.Movie, null, null);
        Assert.Null(none.Refusal);
        Assert.Equal("PG", none.Certification);
        Assert.Equal("US", none.Country);

        // The app tried to widen it: ignored.
        var wider = rules.Browse(MediaKind.Movie, "R", "US");
        Assert.Equal("PG", wider.Certification);

        // A country swap can't smuggle a different ladder in either.
        Assert.Equal("US", rules.Browse(MediaKind.Movie, "PG", "XX").Country);

        // A STRICTER certificate the server can name is kept.
        Assert.Equal("G", rules.Browse(MediaKind.Movie, "G", "US").Certification);

        Assert.Equal("TV-PG", rules.Browse(MediaKind.Series, null, null).Certification);
    }

    [Fact]
    public void AnUncappedMembersCertificationPassesThrough()
    {
        var rules = new MemberRules(DiscoveryLimits.FromCustomPrefs(Prefs()), null, UsLadder(), "AU");
        var browse = rules.Browse(MediaKind.Movie, "PG", "AU");
        Assert.Null(browse.Refusal);
        Assert.Equal("PG", browse.Certification);
        Assert.Equal("AU", browse.Country);
        Assert.Null(rules.Browse(MediaKind.Movie, null, null).Certification);
    }

    [Fact]
    public void ACapTheLadderCannotExpressRefusesBrowse()
    {
        var onlyR = new CertificationLadder(new (string, int?)[] { ("R", 17), ("TV-MA", 17) });
        var rules = new MemberRules(DiscoveryLimits.FromCustomPrefs(Prefs((DiscoveryLimits.CapKey, "10"))), null, onlyR, "US");
        Assert.False(rules.BrowseAllowed);
        Assert.Equal("browseCapped", rules.Browse(MediaKind.Movie, null, null).Refusal?.Outcome);
    }

    [Fact]
    public void DetailIsRefusedOnlyWhenKnownAboveTheCapButRequestNeedsAKnownRating()
    {
        var rules = new MemberRules(DiscoveryLimits.FromCustomPrefs(Prefs()), libraryMaxScore: 10, UsLadder(), "US");
        Assert.Equal("aboveLimit", rules.RefuseDetail("R")?.Outcome);
        Assert.Null(rules.RefuseDetail("PG"));
        Assert.Null(rules.RefuseDetail(null));
        Assert.Equal("aboveLimit", rules.RefuseRequest("R")?.Outcome);
        Assert.Equal("aboveLimit", rules.RefuseRequest(null)?.Outcome);
        Assert.Null(rules.RefuseRequest("G"));
    }

    [Fact]
    public void AnEmptyCountryIsUS()
    {
        Assert.Equal("US", new MemberRules(DiscoveryLimits.FromCustomPrefs(Prefs()), null, UsLadder(), "").Country);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"mediaInfo\":null}", false)]
    [InlineData("{\"mediaInfo\":{\"requests\":[]}}", false)]
    [InlineData("{\"mediaInfo\":{\"requests\":[{\"status\":3}]}}", false)] // declined: asking again is the asker's business
    [InlineData("{\"mediaInfo\":{\"requests\":[{\"status\":4}]}}", false)] // failed: retryable
    [InlineData("{\"mediaInfo\":{\"requests\":[{\"status\":3},{\"status\":1}]}}", true)]
    [InlineData("{\"mediaInfo\":{\"requests\":[{\"status\":2}]}}", true)]
    public void AnOpenAskOnAFilmIsFoundInItsDetail(string json, bool open)
    {
        // Seerr 3.4.1 accepts a second identical ask (measured on the sandbox, kale#308), so the
        // broker refuses it the way the app's state check does.
        Assert.Equal(open, SeerrState.HasOpenAsk(JsonDocument.Parse(json).RootElement));
    }

    [Theory]
    [InlineData("Movie Quota exceeded.", "quotaSpent")]
    [InlineData("Series Quota exceeded.", "quotaSpent")]
    [InlineData("This media is blocklisted.", "blocklisted")]
    [InlineData("You do not have permission to request.", "notAllowed")]
    [InlineData(null, "notAllowed")]
    public void SeerrsForbiddenIsReadByTheWord(string? message, string outcome)
    {
        var refusal = BrokerRefusal.FromSeerr403(message);
        Assert.Equal(outcome, refusal.Outcome);
        Assert.Equal(403, refusal.Status);
    }
}
