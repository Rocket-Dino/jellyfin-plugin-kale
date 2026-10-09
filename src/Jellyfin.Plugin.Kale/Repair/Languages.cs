using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kale.Repair;

/// <summary>
/// Enough of ISO 639 to tell whether two tags name the same language — Matroska writes
/// "chi" or BCP 47 "zh", ffprobe may report either, and a mismatch would make the
/// plugin refuse a file it could fix. Built in rather than read from CultureInfo, because
/// Jellyfin's Docker images run with invariant globalisation, where CultureInfo knows nothing.
/// </summary>
internal static class Languages
{
    private static readonly (string Name, string[] Codes)[] Table =
    {
        ("English", new[] { "eng", "en" }),
        ("Chinese", new[] { "chi", "zho", "zh", "cmn", "yue" }),
        ("French", new[] { "fre", "fra", "fr" }),
        ("German", new[] { "ger", "deu", "de" }),
        ("Spanish", new[] { "spa", "es" }),
        ("Portuguese", new[] { "por", "pt" }),
        ("Italian", new[] { "ita", "it" }),
        ("Japanese", new[] { "jpn", "ja" }),
        ("Korean", new[] { "kor", "ko" }),
        ("Russian", new[] { "rus", "ru" }),
        ("Swedish", new[] { "swe", "sv" }),
        ("Danish", new[] { "dan", "da" }),
        ("Norwegian", new[] { "nor", "nob", "nno", "no", "nb", "nn" }),
        ("Finnish", new[] { "fin", "fi" }),
        ("Dutch", new[] { "dut", "nld", "nl" }),
        ("Polish", new[] { "pol", "pl" }),
        ("Czech", new[] { "cze", "ces", "cs" }),
        ("Greek", new[] { "gre", "ell", "el" }),
        ("Turkish", new[] { "tur", "tr" }),
        ("Hebrew", new[] { "heb", "he", "iw" }),
        ("Arabic", new[] { "ara", "ar" }),
        ("Hindi", new[] { "hin", "hi" }),
        ("Thai", new[] { "tha", "th" }),
        ("Vietnamese", new[] { "vie", "vi" }),
        ("Hungarian", new[] { "hun", "hu" }),
        ("Romanian", new[] { "rum", "ron", "ro" }),
        ("Ukrainian", new[] { "ukr", "uk" }),
        ("Indonesian", new[] { "ind", "id" }),
        ("Malay", new[] { "may", "msa", "ms" }),
        ("Persian", new[] { "per", "fas", "fa" }),
        ("Icelandic", new[] { "ice", "isl", "is" }),
        ("Catalan", new[] { "cat", "ca" }),
        ("Croatian", new[] { "hrv", "hr" }),
        ("Serbian", new[] { "srp", "sr" }),
        ("Bulgarian", new[] { "bul", "bg" }),
        ("Slovak", new[] { "slo", "slk", "sk" }),
        ("Slovenian", new[] { "slv", "sl" }),
        ("Tamil", new[] { "tam", "ta" }),
        ("Telugu", new[] { "tel", "te" }),
    };

    private static readonly Dictionary<string, string> ByCode = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, codes) in Table)
        {
            foreach (var code in codes)
            {
                map[code] = name;
            }
        }

        return map;
    }

    /// <summary>"zh-Hans" → "zh"; "chi" → "chi".</summary>
    private static string Primary(string? tag) =>
        (tag ?? string.Empty).Split('-', '_')[0].Trim();

    public static bool IsUnknown(string? tag)
    {
        var p = Primary(tag);
        return p.Length == 0 || p.Equals("und", StringComparison.OrdinalIgnoreCase) || p.Equals("mis", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Same language, or nothing to compare. An unknown tag on either side is not a disagreement.</summary>
    public static bool Same(string? a, string? b)
    {
        if (IsUnknown(a) || IsUnknown(b))
        {
            return true;
        }

        var pa = Primary(a);
        var pb = Primary(b);
        if (pa.Equals(pb, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ByCode.TryGetValue(pa, out var na) && ByCode.TryGetValue(pb, out var nb) && na == nb;
    }

    public static string? DisplayName(string? tag) =>
        ByCode.TryGetValue(Primary(tag), out var name) ? name : null;
}
