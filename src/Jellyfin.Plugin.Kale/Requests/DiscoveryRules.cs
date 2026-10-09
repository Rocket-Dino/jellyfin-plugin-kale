using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Kale.Requests;

/// <summary>Films or television. TMDB keeps a certification ladder for each.</summary>
public enum MediaKind
{
    Movie,
    Series,
}

/// <summary>
/// A member's discovery limits, read from their own <c>DisplayPreferences</c> custom prefs
/// (<c>usersettings</c>, client <c>kale</c>) — a port of the app's <c>DiscoveryLimits(customPrefs:)</c>,
/// key for key, so the broker and the app can never read the same row differently.
/// </summary>
public sealed record DiscoveryLimits(int? MaxRatingScore, bool ShowsUnrated, bool IsStored, bool SearchesBeyondLibrary)
{
    public const string CapKey = "kale-discovery-max-rating";
    public const string UnratedKey = "kale-discovery-show-unrated";
    public const string SearchKey = "kale-discovery-search";
    public const string Client = "kale";
    public const string PreferencesId = "usersettings";

    public static DiscoveryLimits FromCustomPrefs(IReadOnlyDictionary<string, string?> prefs)
    {
        prefs.TryGetValue(CapKey, out var capText);
        prefs.TryGetValue(UnratedKey, out var unratedText);
        prefs.TryGetValue(SearchKey, out var searchText);
        int? cap = int.TryParse(capText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : null;
        // Both keys absent means UNSET, not "no cap chosen": only Kale's own key is evidence.
        bool stored = prefs.ContainsKey(CapKey) || prefs.ContainsKey(UnratedKey);
        // Absent-but-capped falls to the SAFE side: unrated hidden.
        bool showsUnrated = unratedText is null ? cap is null : unratedText == "true";
        // Only the literal "false" turns search off (a privacy preference; a child is capped anyway).
        return new DiscoveryLimits(cap, showsUnrated, stored, searchText != "false");
    }
}

/// <summary>The scope actually applied: the app's <c>DiscoveryScope.applied(discovery:library:)</c>.</summary>
public sealed record DiscoveryScope(int? MaxRatingScore, bool ShowsUnrated)
{
    public static DiscoveryScope Applied(DiscoveryLimits discovery, int? libraryMaxScore)
    {
        if (!discovery.IsStored)
        {
            // Derived from the library cap: a cap hides unrated.
            return new DiscoveryScope(libraryMaxScore, libraryMaxScore is null);
        }

        int? cap = (discovery.MaxRatingScore, libraryMaxScore) switch
        {
            ({ } mine, { } theirs) => Math.Min(mine, theirs),
            ({ } mine, null) => mine,
            (null, { } theirs) => theirs,
            _ => null,
        };
        // Stricter only: a library cap implies unrated hidden, and a stored "show" can't undo it.
        bool libraryHidesUnrated = libraryMaxScore is not null;
        return new DiscoveryScope(cap, cap is null || (discovery.ShowsUnrated && !libraryHidesUnrated));
    }

    /// <summary>A capped scope admits nothing from search: TMDB's multi-search carries no ratings.</summary>
    public bool AdmitsNothingFromSearch => MaxRatingScore is not null && !ShowsUnrated;

    /// <summary>Whether a title with this certification may be shown. An unknown name is UNRATED.</summary>
    public bool Admits(string? certification, IReadOnlyDictionary<string, int> scoresByName)
    {
        if (MaxRatingScore is not { } cap)
        {
            return true;
        }

        if (certification is null || !scoresByName.TryGetValue(certification, out var score))
        {
            return ShowsUnrated;
        }

        return score <= cap;
    }
}

/// <summary>
/// Jellyfin's rating SCORE → the certification NAME TMDB wants, per kind — a port of the app's
/// <c>CertificationLadder</c>. A <c>TV-</c> prefix is television; shortest name per score wins,
/// ties alphabetical; a score with no exact name falls DOWN to the next one below.
/// </summary>
public sealed class CertificationLadder
{
    private readonly Dictionary<int, string> _films = new();
    private readonly Dictionary<int, string> _television = new();

    public CertificationLadder(IEnumerable<(string Name, int? Score)> ratings)
    {
        foreach (var (name, score) in ratings)
        {
            if (score is not { } s || string.IsNullOrEmpty(name))
            {
                continue;
            }

            var target = name.StartsWith("TV-", StringComparison.OrdinalIgnoreCase) ? _television : _films;
            // Shortest name wins (the canonical one); ties break alphabetically, so the answer
            // never depends on the order the server listed them in.
            if (target.TryGetValue(s, out var existing)
                && (existing.Length < name.Length || (existing.Length == name.Length && string.CompareOrdinal(existing, name) <= 0)))
            {
                continue;
            }

            target[s] = name;
        }

        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (s, n) in _films)
        {
            names[n] = s;
        }

        foreach (var (s, n) in _television)
        {
            names[n] = s;
        }

        ScoresByName = names;
    }

    public IReadOnlyDictionary<string, int> ScoresByName { get; }

    /// <summary>The certificate for a cap, or null when this ladder can't express it (then: no browse).</summary>
    public string? CertificationFor(int score, MediaKind kind)
    {
        var ladder = kind == MediaKind.Movie ? _films : _television;
        if (ladder.TryGetValue(score, out var exact))
        {
            return exact;
        }

        // Fall DOWN, never up: stricter than asked is safe, looser is the harm.
        var below = ladder.Keys.Where(k => k <= score).ToList();
        return below.Count == 0 ? null : ladder[below.Max()];
    }
}

/// <summary>Reads a title's certification for one country out of Seerr's movie/tv detail body.</summary>
public static class SeerrCertification
{
    public static string? From(JsonElement detail, string country)
    {
        if (detail.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Seerr passes TMDB's snake_case keys through (`iso_3166_1`, `release_dates`).
        if (detail.TryGetProperty("releases", out var releases)
            && releases.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array
            && results.GetArrayLength() > 0)
        {
            foreach (var row in results.EnumerateArray())
            {
                if (Str(row, "iso_3166_1") != country || !row.TryGetProperty("release_dates", out var dates) || dates.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                // FIRST NON-EMPTY: earlier release types often carry an empty certification.
                foreach (var date in dates.EnumerateArray())
                {
                    var c = Str(date, "certification");
                    if (!string.IsNullOrEmpty(c))
                    {
                        return c;
                    }
                }
            }

            return null;
        }

        if (detail.TryGetProperty("contentRatings", out var ratings)
            && ratings.TryGetProperty("results", out var rows)
            && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (Str(row, "iso_3166_1") == country)
                {
                    var r = Str(row, "rating");
                    return string.IsNullOrEmpty(r) ? null : r;
                }
            }
        }

        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Where a title stands in Seerr, read from its detail body.</summary>
public static class SeerrState
{
    /// <summary>Whether anyone's ask for it is still open (PENDING 1 or APPROVED 2).</summary>
    public static bool HasOpenAsk(JsonElement detail)
    {
        if (detail.ValueKind != JsonValueKind.Object
            || !detail.TryGetProperty("mediaInfo", out var media) || media.ValueKind != JsonValueKind.Object
            || !media.TryGetProperty("requests", out var requests) || requests.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return requests.EnumerateArray().Any(r => r.TryGetProperty("status", out var s) && s.TryGetInt32(out var n) && (n == 1 || n == 2));
    }
}

/// <summary>
/// Every discovery decision the broker makes for one member, in one place. Each returns a refusal
/// (in plain words) or lets the call through — a barred member gets a refusal, never results.
/// </summary>
public sealed class MemberRules
{
    public MemberRules(DiscoveryLimits limits, int? libraryMaxScore, CertificationLadder ladder, string country)
    {
        Limits = limits;
        Scope = DiscoveryScope.Applied(limits, libraryMaxScore);
        Ladder = ladder;
        Country = string.IsNullOrEmpty(country) ? "US" : country;
    }

    public DiscoveryLimits Limits { get; }

    public DiscoveryScope Scope { get; }

    public CertificationLadder Ladder { get; }

    public string Country { get; }

    public bool SearchAllowed => RefuseSearch() is null;

    public bool BrowseAllowed => Browse(MediaKind.Movie, null, null).Refusal is null && Browse(MediaKind.Series, null, null).Refusal is null;

    public BrokerRefusal? RefuseSearch()
    {
        if (!Limits.SearchesBeyondLibrary)
        {
            return BrokerRefusal.SearchOff;
        }

        return Scope.AdmitsNothingFromSearch ? BrokerRefusal.SearchCapped : null;
    }

    /// <summary>
    /// The certification pair to send to discover. Uncapped: whatever the app asked for. Capped: the
    /// server's own name for the cap, unless the app asked for a stricter one it can name.
    /// </summary>
    public (BrokerRefusal? Refusal, string? Certification, string? Country) Browse(MediaKind kind, string? requestedCertification, string? requestedCountry)
    {
        if (!Limits.SearchesBeyondLibrary)
        {
            return (BrokerRefusal.SearchOff, null, null);
        }

        if (Scope.MaxRatingScore is not { } cap)
        {
            // Uncapped: the app may filter however it likes.
            return (null, requestedCertification, requestedCertification is null ? null : (requestedCountry ?? Country));
        }

        // Capped: a certificate without a country is IGNORED by TMDB (measured: everything comes
        // back), so the pair is always the server's own, and a name it can't express means no browse.
        if (Ladder.CertificationFor(cap, kind) is not { } server)
        {
            return (BrokerRefusal.BrowseCapped, null, null);
        }

        var serverScore = Ladder.ScoresByName[server];
        if (requestedCertification is not null
            && Ladder.ScoresByName.TryGetValue(requestedCertification, out var asked)
            && asked < serverScore
            && Ladder.CertificationFor(asked, kind) == requestedCertification)
        {
            return (null, requestedCertification, Country);
        }

        return (null, server, Country);
    }

    /// <summary>Detail: refused only when the title's rating is KNOWN and above the cap.</summary>
    public BrokerRefusal? RefuseDetail(string? certification)
    {
        if (Scope.MaxRatingScore is not { } cap || certification is null || !Ladder.ScoresByName.TryGetValue(certification, out var score))
        {
            return null;
        }

        return score > cap ? BrokerRefusal.AboveLimit : null;
    }

    /// <summary>Request: allowed only when the scope admits the title (unrated counts as unknown).</summary>
    public BrokerRefusal? RefuseRequest(string? certification) =>
        Scope.Admits(certification, Ladder.ScoresByName) ? null : BrokerRefusal.AboveLimit;
}
