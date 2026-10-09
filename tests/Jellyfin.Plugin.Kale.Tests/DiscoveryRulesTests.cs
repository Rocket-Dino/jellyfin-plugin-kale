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
        Assert.True(scope.Admits(null, UsLadder().ScoresByName));
        Assert.True(scope.Admits("NC-17", UsLadder().ScoresByName));
    }

    [Fact]
    public void ACappedScopeAdmitsByScoreAndTreatsUnknownNamesAsUnrated()
    {
        var scope = new DiscoveryScope(10, ShowsUnrated: false);
        var names = UsLadder().ScoresByName;
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
        Assert.Equal(13, ladder.ScoresByName["PG-13"]);
        Assert.False(ladder.ScoresByName.ContainsKey("Unrated"));
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
