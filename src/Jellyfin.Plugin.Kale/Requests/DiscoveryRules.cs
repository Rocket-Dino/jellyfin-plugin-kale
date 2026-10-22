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
/// A place on the server's rating ladder: score and sub-score — a port of the app's <c>RatingLevel</c>
/// (kale#311). AU's PG, M and MA15+ all score 15 and differ only in sub-score (15.1, 15.2, 15.3), so a
/// score alone cannot tell them apart. A missing sub-score compares as 0, the server's own list rule.
/// </summary>
public readonly record struct RatingLevel(int Score, int? SubScore = null)
{
    /// <summary>Whether a title at <paramref name="level"/> is within this level, read as a cap.</summary>
    public bool Admits(RatingLevel level) =>
        level.Score < Score || (level.Score == Score && (level.SubScore ?? 0) <= (SubScore ?? 0));

    /// <summary>Strictly below <paramref name="other"/> in the server's order.</summary>
    public bool IsBelow(RatingLevel other) => !Admits(other);

    /// <summary>The stricter of two caps (null = none); on a tie the one carrying a sub-score.</summary>
    public static RatingLevel? Stricter(RatingLevel? a, RatingLevel? b)
    {
        if (a is not { } x)
        {
            return b;
        }

        if (b is not { } y)
        {
            return a;
        }

        if (x.IsBelow(y))
        {
            return x;
        }

        if (y.IsBelow(x))
        {
            return y;
        }

        return x.SubScore is not null ? x : y;
    }
}

/// <summary>
/// A member's discovery limits, read from their own <c>DisplayPreferences</c> custom prefs
/// (<c>usersettings</c>, client <c>kale</c>) — a port of the app's <c>DiscoveryLimits(customPrefs:)</c>,
/// key for key, so the broker and the app can never read the same row differently.
/// </summary>
public sealed record DiscoveryLimits(int? MaxRatingScore, bool ShowsUnrated, bool IsStored, bool SearchesBeyondLibrary, int? MaxSubRatingScore = null)
{
    public const string CapKey = "kale-discovery-max-rating";
    /// <summary>The cap's sub-score, under its own key so an integer parse of <see cref="CapKey"/> still works (kale#311).</summary>
    public const string SubKey = "kale-discovery-max-subrating";
    public const string UnratedKey = "kale-discovery-show-unrated";
    public const string SearchKey = "kale-discovery-search";
    public const string Client = "kale";
    public const string PreferencesId = "usersettings";

    public static DiscoveryLimits FromCustomPrefs(IReadOnlyDictionary<string, string?> prefs)
    {
        prefs.TryGetValue(CapKey, out var capText);
        prefs.TryGetValue(UnratedKey, out var unratedText);
        prefs.TryGetValue(SearchKey, out var searchText);
        prefs.TryGetValue(SubKey, out var subText);
        int? cap = int.TryParse(capText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : null;
        int? sub = cap is not null && int.TryParse(subText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        // Both keys absent means UNSET, not "no cap chosen": only Kale's own key is evidence.
        bool stored = prefs.ContainsKey(CapKey) || prefs.ContainsKey(UnratedKey);
        // Absent-but-capped falls to the SAFE side: unrated hidden.
        bool showsUnrated = unratedText is null ? cap is null : unratedText == "true";
        // Only the literal "false" turns search off (a privacy preference; a child is capped anyway).
        return new DiscoveryLimits(cap, showsUnrated, stored, searchText != "false", sub);
    }

    public RatingLevel? Cap => MaxRatingScore is { } s ? new RatingLevel(s, MaxSubRatingScore) : null;
}

/// <summary>The scope actually applied: the app's <c>DiscoveryScope.applied(discovery:library:)</c>.</summary>
public sealed record DiscoveryScope(int? MaxRatingScore, bool ShowsUnrated, int? MaxSubRatingScore = null)
{
    public RatingLevel? Cap => MaxRatingScore is { } s ? new RatingLevel(s, MaxSubRatingScore) : null;

    public static DiscoveryScope Applied(DiscoveryLimits discovery, int? libraryMaxScore, int? librarySubScore = null)
    {
        RatingLevel? library = libraryMaxScore is { } l ? new RatingLevel(l, librarySubScore) : null;
        if (!discovery.IsStored)
        {
            // Derived from the library cap: a cap hides unrated.
            return new DiscoveryScope(library?.Score, library is null, library?.SubScore);
        }

        // Score AND sub-score: a stored PG (15.1) under a library MA15+ (15.3) is the stricter one.
        var cap = RatingLevel.Stricter(discovery.Cap, library);
        // Stricter only: a library cap implies unrated hidden, and a stored "show" can't undo it.
        bool libraryHidesUnrated = library is not null;
        return new DiscoveryScope(cap?.Score, cap is null || (discovery.ShowsUnrated && !libraryHidesUnrated), cap?.SubScore);
    }

    /// <summary>A capped scope admits nothing from search: TMDB's multi-search carries no ratings.</summary>
    public bool AdmitsNothingFromSearch => MaxRatingScore is not null && !ShowsUnrated;

    /// <summary>Whether a title with this certification may be shown. An unknown name is UNRATED.</summary>
    public bool Admits(string? certification, IReadOnlyDictionary<string, RatingLevel> levelsByName)
    {
        if (Cap is not { } cap)
        {
            return true;
        }

        if (certification is null || !levelsByName.TryGetValue(certification, out var level))
        {
            return ShowsUnrated;
        }

        return cap.Admits(level);
    }
}

/// <summary>
/// Jellyfin's rating LEVEL → the certification NAME TMDB wants, per kind — a port of the app's
/// <c>CertificationLadder</c>. A <c>TV-</c> prefix is television; a content-descriptor variant
/// (<c>TV-PG-D</c>, another name at the same score plus a separator) is never a candidate; shortest
/// name per level wins, ties alphabetical; the highest name the cap admits, so a level with no exact
/// name falls DOWN. A cap with no sub-score names the strictest certificate AT its score (AU's
/// pre-kale#311 bare 15 → PG, not the generic 14 TMDB's AU list lacks).
/// </summary>
public sealed class CertificationLadder
{
    private readonly Dictionary<RatingLevel, string> _films = new();
    private readonly Dictionary<RatingLevel, string> _television = new();

    public CertificationLadder(IEnumerable<(string Name, int? Score)> ratings)
        : this(ratings.Select(r => (r.Name, r.Score, (int?)null)))
    {
    }

    public CertificationLadder(IEnumerable<(string Name, int? Score, int? SubScore)> ratings)
    {
        var scored = ratings
            .Where(r => r.Score is not null && !string.IsNullOrEmpty(r.Name))
            .Select(r => (r.Name, Level: new RatingLevel(r.Score!.Value, r.SubScore)))
            .ToList();
        foreach (var (name, level) in scored)
        {
            if (scored.Any(o => o.Level.Score == level.Score && o.Name != name && name.StartsWith(o.Name + "-", StringComparison.Ordinal)))
            {
                continue;
            }

            var target = name.StartsWith("TV-", StringComparison.OrdinalIgnoreCase) ? _television : _films;
            var key = new RatingLevel(level.Score, level.SubScore ?? 0);
            // Shortest name wins (the canonical one); ties break alphabetically, so the answer
            // never depends on the order the server listed them in.
            if (target.TryGetValue(key, out var existing)
                && (existing.Length < name.Length || (existing.Length == name.Length && string.CompareOrdinal(existing, name) <= 0)))
            {
                continue;
            }

            target[key] = name;
        }

        var names = new Dictionary<string, RatingLevel>(StringComparer.Ordinal);
        foreach (var (l, n) in _films)
        {
            names[n] = l;
        }

        foreach (var (l, n) in _television)
        {
            names[n] = l;
        }

        LevelsByName = names;
    }

    public IReadOnlyDictionary<string, RatingLevel> LevelsByName { get; }

    /// <summary>The certificate for a cap, or null when this ladder can't express it (then: no browse).</summary>
    public string? CertificationFor(RatingLevel cap, MediaKind kind)
    {
        var ladder = kind == MediaKind.Movie ? _films : _television;
        if (cap.SubScore is null)
        {
            var atScore = ladder.Keys.Where(k => k.Score == cap.Score).OrderBy(k => k.SubScore ?? 0).ToList();
            if (atScore.Count > 0)
            {
                return ladder[atScore[0]];
            }
        }

        // Fall DOWN, never up: stricter than asked is safe, looser is the harm.
        var admitted = ladder.Keys.Where(cap.Admits).OrderBy(k => k.Score).ThenBy(k => k.SubScore ?? 0).ToList();
        return admitted.Count == 0 ? null : ladder[admitted[^1]];
    }

    /// <summary>A cap given as a score alone.</summary>
    public string? CertificationFor(int score, MediaKind kind) => CertificationFor(new RatingLevel(score), kind);

    /// <summary>
    /// Every certificate at or below <paramref name="top"/> for this kind, strictest first; null when
    /// the ladder doesn't name it (kale#347). TMDB orders <c>NR</c> BELOW <c>G</c>, so
    /// <c>certificationLte</c> alone admits every unrated film; this list, sent as TMDB's
    /// <c>certification</c>, leaves it out. A name TMDB doesn't carry matches nothing: it errs strict.
    /// </summary>
    public IReadOnlyList<string>? CertificationsUpTo(string top, MediaKind kind)
    {
        var ladder = kind == MediaKind.Movie ? _films : _television;
        var at = ladder.Where(e => e.Value == top).Select(e => (RatingLevel?)e.Key).FirstOrDefault();
        if (at is not { } level)
        {
            return null;
        }

        return ladder
            .Where(e => (e.Key.Score, e.Key.SubScore ?? 0).CompareTo((level.Score, level.SubScore ?? 0)) <= 0)
            .OrderBy(e => e.Key.Score).ThenBy(e => e.Key.SubScore ?? 0)
            .Select(e => e.Value)
            .ToList();
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
    public MemberRules(DiscoveryLimits limits, int? libraryMaxScore, CertificationLadder ladder, string country, int? librarySubScore = null)
    {
        Limits = limits;
        Scope = DiscoveryScope.Applied(limits, libraryMaxScore, librarySubScore);
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

        if (Scope.Cap is not { } cap)
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

        var serverLevel = Ladder.LevelsByName[server];
        if (requestedCertification is not null
            && Ladder.LevelsByName.TryGetValue(requestedCertification, out var asked)
            && asked.IsBelow(serverLevel)
            && Ladder.CertificationFor(asked, kind) == requestedCertification)
        {
            return (null, requestedCertification, Country);
        }

        return (null, server, Country);
    }

    /// <summary>
    /// The certificates themselves, pipe-joined for TMDB's <c>certification</c>, when this member is
    /// capped and HIDES UNRATED (kale#347) — the default for a cap. Measured on the sandbox Seerr
    /// 2026-10-23: Coming Soon at PG with the pair alone was 10 unrated films in 20, Top Rated 6 in 20;
    /// with <c>certification=G|PG</c>, none. Null for an uncapped member or one who shows unrated.
    /// </summary>
    public string? RatedOnly(MediaKind kind, string certification)
    {
        if (Scope.Cap is null || Scope.ShowsUnrated || Ladder.CertificationsUpTo(certification, kind) is not { Count: > 0 } names)
        {
            return null;
        }

        return string.Join('|', names);
    }

    /// <summary>Detail: refused only when the title's rating is KNOWN and above the cap.</summary>
    public BrokerRefusal? RefuseDetail(string? certification)
    {
        if (Scope.Cap is not { } cap || certification is null || !Ladder.LevelsByName.TryGetValue(certification, out var level))
        {
            return null;
        }

        return cap.Admits(level) ? null : BrokerRefusal.AboveLimit;
    }

    /// <summary>Request: allowed only when the scope admits the title (unrated counts as unknown).</summary>
    public BrokerRefusal? RefuseRequest(string? certification) =>
        Scope.Admits(certification, Ladder.LevelsByName) ? null : BrokerRefusal.AboveLimit;
}
