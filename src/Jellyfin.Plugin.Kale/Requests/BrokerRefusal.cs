namespace Jellyfin.Plugin.Kale.Requests;

/// <summary>
/// Why the broker said no, in the plugin's <c>RepairResult</c> style: a stable <see cref="Outcome"/> for
/// the app to branch on and a <see cref="Message"/> sentence to show. <see cref="Status"/> is the HTTP
/// status it is sent with and is not serialised.
/// </summary>
public sealed record BrokerRefusal(string Outcome, string Message)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int Status { get; init; } = 403;

    public static readonly BrokerRefusal NotConfigured = new("notConfigured", "Requests aren't set up in kale's plugin on this server yet.") { Status = 503 };
    public static readonly BrokerRefusal Unreachable = new("unreachable", "Your server couldn't reach Seerr just now.") { Status = 502 };
    public static readonly BrokerRefusal SearchOff = new("searchOff", "Searching beyond your library is turned off for you.");
    public static readonly BrokerRefusal SearchCapped = new("searchCapped", "With your rating limit, search covers your library only. Browse shows what you can ask for.");
    public static readonly BrokerRefusal BrowseCapped = new("browseCapped", "Your rating limit can't be applied to browsing, so browsing is off for you.");
    public static readonly BrokerRefusal AboveLimit = new("aboveLimit", "That's above your rating limit.");
    public static readonly BrokerRefusal QuotaSpent = new("quotaSpent", "You've used all your requests for now. You can ask again soon.");
    public static readonly BrokerRefusal Blocklisted = new("blocklisted", "This one has been hidden in your household.");
    public static readonly BrokerRefusal NotAllowed = new("notAllowed", "You're not able to do that in Seerr.");
    public static readonly BrokerRefusal AlreadyRequested = new("alreadyRequested", "Someone has already asked for this.") { Status = 409 };
    public static readonly BrokerRefusal NoSeerrAccount = new("noSeerrAccount", "You don't have a Seerr account yet, and kale couldn't make one. Ask whoever runs Seerr to add you.");
    public static readonly BrokerRefusal SeerrError = new("seerrError", "Seerr couldn't do that just now.") { Status = 502 };
    public static readonly BrokerRefusal BadRequest = new("badRequest", "kale didn't understand that request.") { Status = 400 };
    public static readonly BrokerRefusal NeedsAPerson = new("needsAPerson", "Requests are made by a signed-in person, not an API key.");

    /// <summary>
    /// Seerr's 403 means several things with opposite remedies (measured: "Movie Quota exceeded.",
    /// "Series Quota exceeded.", "This media is blocklisted."). Matched on the WORD, as the app does.
    /// </summary>
    public static BrokerRefusal FromSeerr403(string? message)
    {
        var m = message?.ToLowerInvariant() ?? string.Empty;
        if (m.Contains("quota", System.StringComparison.Ordinal))
        {
            return QuotaSpent;
        }

        return m.Contains("blocklist", System.StringComparison.Ordinal) ? Blocklisted : NotAllowed;
    }
}
