using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kale.Requests;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kale.Api;

/// <summary>The body of <c>POST /Kale/Requests/Configuration</c>.</summary>
public sealed record SeerrConfigurationBody(string? SeerrUrl, string? ApiKey);

/// <summary>
/// The request broker (kale#308): Kale's requests, made with the member's Jellyfin token and sent
/// on to Seerr as that member. Contract: <c>JellyfinPlugin/README.md</c>, "Request broker".
/// </summary>
[ApiController]
[Route("Kale/Requests")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public partial class KaleRequestsController : ControllerBase
{
    private readonly SeerrBroker _broker;
    private readonly MemberRulesSource _rules;
    private readonly IAuthorizationContext _auth;

    public KaleRequestsController(SeerrBroker broker, MemberRulesSource rules, IAuthorizationContext auth)
    {
        _broker = broker;
        _rules = rules;
        _auth = auth;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.]{0,40}$")]
    private static partial Regex ParameterName();

    // ---- Who is asking -------------------------------------------------------------------

    private async Task<User?> CallerAsync()
    {
        var info = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return info.IsApiKey ? null : info.User;
    }

    private static ActionResult Refuse(BrokerRefusal refusal) => new ObjectResult(refusal) { StatusCode = refusal.Status };

    private static ActionResult Answer(BrokerResponse response)
    {
        if (response.Refusal is { } refusal)
        {
            return Refuse(refusal);
        }

        if (response.Json is null)
        {
            return new StatusCodeResult(response.Status);
        }

        return new ContentResult { Content = response.Json, ContentType = MediaTypeNames.Application.Json, StatusCode = response.Status };
    }

    private static MediaKind? KindOf(string kind) => kind.ToLowerInvariant() switch
    {
        "movie" or "movies" => MediaKind.Movie,
        "tv" or "series" => MediaKind.Series,
        _ => null,
    };

    // ---- Anyone signed in ---------------------------------------------------------------

    /// <summary>What this member may do in Seerr, and what Kale's limits leave them.</summary>
    [HttpGet("Me")]
    public async Task<ActionResult> Me(CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        var me = await _broker.AsUser(user.Id, HttpMethod.Get, "api/v1/auth/me", cancellationToken: ct).ConfigureAwait(false);
        if (me.Refusal is not null || me.Json is null)
        {
            return Answer(me);
        }

        using var meDoc = JsonDocument.Parse(me.Json);
        int permissions = meDoc.RootElement.TryGetProperty("permissions", out var p) && p.TryGetInt32(out var bits) ? bits : 0;
        int? seerrId = meDoc.RootElement.TryGetProperty("id", out var i) && i.TryGetInt32(out var id) ? id : null;

        JsonNode? quota = null;
        if (seerrId is { } sid)
        {
            // A quota failure must not remove the button: Seerr enforces the limit regardless.
            var q = await _broker.AsUser(user.Id, HttpMethod.Get, $"api/v1/user/{sid}/quota", cancellationToken: ct).ConfigureAwait(false);
            quota = q.Json is null ? null : JsonNode.Parse(q.Json);
        }

        var rules = _rules.For(user);
        var body = new JsonObject
        {
            ["Permissions"] = permissions,
            ["Quota"] = quota,
            ["SearchAllowed"] = rules.SearchAllowed,
            ["BrowseAllowed"] = rules.BrowseAllowed,
        };
        return new ContentResult { Content = body.ToJsonString(), ContentType = MediaTypeNames.Application.Json, StatusCode = 200 };
    }

    /// <summary>Search beyond the library. Refused for a member whose limits bar it.</summary>
    [HttpGet("Search")]
    public async Task<ActionResult> Search([FromQuery] string? query, [FromQuery] int? page, CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        if (_rules.For(user).RefuseSearch() is { } refusal)
        {
            return Refuse(refusal);
        }

        var q = new List<KeyValuePair<string, string>>
        {
            new("query", (query ?? string.Empty).Trim()),
            new("page", Math.Max(1, page ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        return Answer(await _broker.AsUser(user.Id, HttpMethod.Get, "api/v1/search", q, cancellationToken: ct).ConfigureAwait(false));
    }

    /// <summary>One browse row. Row parameters pass through; the certification is decided here.</summary>
    [HttpGet("Discover/{kind}")]
    public Task<ActionResult> Discover([FromRoute] string kind, CancellationToken ct) => Browse(kind, upcoming: false, ct);

    /// <summary>Not yet released (films only, as Seerr has it).</summary>
    [HttpGet("Upcoming/{kind}")]
    public Task<ActionResult> Upcoming([FromRoute] string kind, CancellationToken ct) => Browse(kind, upcoming: true, ct);

    private async Task<ActionResult> Browse(string kind, bool upcoming, CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        if (KindOf(kind) is not { } k || (upcoming && k != MediaKind.Movie))
        {
            return Refuse(BrokerRefusal.BadRequest);
        }

        var asked = Request.Query;
        var (refusal, certification, country) = _rules.For(user).Browse(
            k,
            asked.TryGetValue("certificationLte", out var c) ? c.ToString() : null,
            asked.TryGetValue("certificationCountry", out var cc) ? cc.ToString() : null);
        if (refusal is not null)
        {
            return Refuse(refusal);
        }

        var q = new List<KeyValuePair<string, string>>();
        foreach (var (name, values) in asked)
        {
            // Never the app's certification pair (decided above), and only plain parameter names.
            if (name.StartsWith("certification", StringComparison.OrdinalIgnoreCase) || !ParameterName().IsMatch(name))
            {
                continue;
            }

            var value = values.ToString();
            if (value.Length <= 200)
            {
                q.Add(new(name, value));
            }
        }

        if (!q.Any(kv => kv.Key == "page"))
        {
            q.Add(new("page", "1"));
        }

        if (certification is not null)
        {
            q.Add(new("certificationCountry", country ?? _rules.For(user).Country));
            q.Add(new("certificationLte", certification));
        }

        var path = upcoming ? "api/v1/discover/movies/upcoming" : (k == MediaKind.Movie ? "api/v1/discover/movies" : "api/v1/discover/tv");
        return Answer(await _broker.AsUser(user.Id, HttpMethod.Get, path, q, cancellationToken: ct).ConfigureAwait(false));
    }

    /// <summary>Genre names and ids, for matching browse rows to the library.</summary>
    [HttpGet("Genres/{kind}")]
    public async Task<ActionResult> Genres([FromRoute] string kind, CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        if (KindOf(kind) is not { } k)
        {
            return Refuse(BrokerRefusal.BadRequest);
        }

        return Answer(await _broker.AsUser(user.Id, HttpMethod.Get, k == MediaKind.Movie ? "api/v1/genres/movie" : "api/v1/genres/tv", cancellationToken: ct).ConfigureAwait(false));
    }

    /// <summary>A film's detail: rating, state (<c>mediaInfo</c>), release dates.</summary>
    [HttpGet("Movie/{tmdbId:int}")]
    public Task<ActionResult> Movie([FromRoute] int tmdbId, CancellationToken ct) => Detail(MediaKind.Movie, tmdbId, ct);

    /// <summary>A series' detail, including <c>mediaInfo.seasons</c> for the season picker.</summary>
    [HttpGet("Tv/{tmdbId:int}")]
    public Task<ActionResult> Tv([FromRoute] int tmdbId, CancellationToken ct) => Detail(MediaKind.Series, tmdbId, ct);

    /// <summary>One season's episodes and air dates, for the series page's schedule.</summary>
    [HttpGet("Tv/{tmdbId:int}/Season/{season:int}")]
    public async Task<ActionResult> Season([FromRoute] int tmdbId, [FromRoute] int season, CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        if (season < 0)
        {
            return Refuse(BrokerRefusal.BadRequest);
        }

        return Answer(await _broker.AsUser(user.Id, HttpMethod.Get, $"api/v1/tv/{tmdbId}/season/{season}", cancellationToken: ct).ConfigureAwait(false));
    }

    private async Task<ActionResult> Detail(MediaKind kind, int tmdbId, CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        var rules = _rules.For(user);
        var answer = await _broker.AsUser(user.Id, HttpMethod.Get, DetailPath(kind, tmdbId), cancellationToken: ct).ConfigureAwait(false);
        if (answer.Json is not null)
        {
            using var doc = JsonDocument.Parse(answer.Json);
            if (rules.RefuseDetail(SeerrCertification.From(doc.RootElement, rules.Country)) is { } refusal)
            {
                return Refuse(refusal);
            }
        }

        return Answer(answer);
    }

    private static string DetailPath(MediaKind kind, int tmdbId) =>
        (kind == MediaKind.Movie ? "api/v1/movie/" : "api/v1/tv/") + tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Ask. Seerr's own permissions and quota apply; Kale's rating cap is checked first.</summary>
    [HttpPost("")]
    public async Task<ActionResult> Ask([FromBody] JsonElement body, CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("mediaType", out var mt) || mt.ValueKind != JsonValueKind.String || KindOf(mt.GetString() ?? string.Empty) is not { } kind
            || !body.TryGetProperty("mediaId", out var mi) || !mi.TryGetInt32(out var tmdbId) || tmdbId <= 0)
        {
            return Refuse(BrokerRefusal.BadRequest);
        }

        // Only what a request is: never a server, profile or folder chosen by a device.
        var forward = new JsonObject { ["mediaType"] = kind == MediaKind.Movie ? "movie" : "tv", ["mediaId"] = tmdbId };
        if (kind == MediaKind.Series && body.TryGetProperty("seasons", out var seasons))
        {
            if (seasons.ValueKind == JsonValueKind.String && seasons.GetString() == "all")
            {
                forward["seasons"] = "all";
            }
            else if (seasons.ValueKind == JsonValueKind.Array && seasons.EnumerateArray().All(s => s.TryGetInt32(out var n) && n >= 0))
            {
                forward["seasons"] = new JsonArray(seasons.EnumerateArray().Select(s => (JsonNode)s.GetInt32()).ToArray());
            }
            else
            {
                return Refuse(BrokerRefusal.BadRequest);
            }
        }

        // Read the title first: Kale's rating cap needs its certificate, and Seerr 3.4.1 accepts a
        // second identical ask (measured on the sandbox), so an open one on a film is refused here,
        // the way the app's own state check would. (A series' asks are per season: Seerr's to judge.)
        var rules = _rules.For(user);
        var detail = await _broker.AsUser(user.Id, HttpMethod.Get, DetailPath(kind, tmdbId), cancellationToken: ct).ConfigureAwait(false);
        string? certification = null;
        if (detail.Json is not null)
        {
            using var doc = JsonDocument.Parse(detail.Json);
            certification = SeerrCertification.From(doc.RootElement, rules.Country);
            if (kind == MediaKind.Movie && SeerrState.HasOpenAsk(doc.RootElement))
            {
                return Refuse(BrokerRefusal.AlreadyRequested);
            }
        }
        else if (detail.Refusal is { Outcome: "notConfigured" or "unreachable" or "noSeerrAccount" } r)
        {
            return Refuse(r);
        }

        // Capped: the title's rating must be KNOWN and within the cap. Not knowing is a no.
        if (rules.Scope.MaxRatingScore is not null && rules.RefuseRequest(certification) is { } refusal)
        {
            return Refuse(refusal);
        }

        return Answer(await _broker.AsUser(user.Id, HttpMethod.Post, "api/v1/request", body: _ => forward, cancellationToken: ct).ConfigureAwait(false));
    }

    /// <summary>Take an ask back. Seerr lets the asker withdraw their own.</summary>
    [HttpDelete("{requestId:int}")]
    public async Task<ActionResult> Withdraw([FromRoute] int requestId, CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        return Answer(await _broker.AsUser(user.Id, HttpMethod.Delete, $"api/v1/request/{requestId}", cancellationToken: ct).ConfigureAwait(false));
    }

    /// <summary>Hide a title from the household. Seerr decides who may (MANAGE_BLOCKLIST).</summary>
    [HttpPost("Blocklist")]
    public async Task<ActionResult> Block([FromBody] JsonElement body, CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("tmdbId", out var t) || !t.TryGetInt32(out var tmdbId)
            || !body.TryGetProperty("mediaType", out var mt) || KindOf(mt.GetString() ?? string.Empty) is not { } kind)
        {
            return Refuse(BrokerRefusal.BadRequest);
        }

        var title = body.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() : string.Empty;
        return Answer(await _broker.AsUser(user.Id, HttpMethod.Post, "api/v1/blocklist", body: seerrId => new JsonObject
        {
            ["tmdbId"] = tmdbId,
            ["mediaType"] = kind == MediaKind.Movie ? "movie" : "tv",
            ["title"] = title,
            ["user"] = seerrId,
        }, cancellationToken: ct).ConfigureAwait(false));
    }

    /// <summary>Show a hidden title again.</summary>
    [HttpDelete("Blocklist/{tmdbId:int}")]
    public async Task<ActionResult> Unblock([FromRoute] int tmdbId, [FromQuery] string? mediaType, CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        if (KindOf(mediaType ?? string.Empty) is not { } kind)
        {
            return Refuse(BrokerRefusal.BadRequest);
        }

        var q = new[] { new KeyValuePair<string, string>("mediaType", kind == MediaKind.Movie ? "movie" : "tv") };
        return Answer(await _broker.AsUser(user.Id, HttpMethod.Delete, $"api/v1/blocklist/{tmdbId}", q, cancellationToken: ct).ConfigureAwait(false));
    }

    // ---- Answering asks: Seerr decides (MANAGE_REQUESTS) ---------------------------------
    //
    // No Jellyfin-admin check here, on purpose (decided 2026-10-09; ADR-019 "the plugin upgrades, it
    // never gates"). Every call goes out AS the caller, so Seerr's own MANAGE_REQUESTS is the gate:
    // a second parent who can approve by signing in to Seerr must not lose that through the broker.
    // Seerr's 403 comes back as `notAllowed`. Only configuring the plugin is Jellyfin-admin.

    /// <summary>
    /// Everything waiting for an answer. Seerr scopes it: with MANAGE_REQUESTS (or REQUEST_VIEW) the
    /// household's, without it only the caller's own asks.
    /// </summary>
    [HttpGet("Pending")]
    public async Task<ActionResult> Pending(CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        var q = new[]
        {
            new KeyValuePair<string, string>("filter", "pending"),
            new KeyValuePair<string, string>("take", "40"),
            new KeyValuePair<string, string>("sort", "added"),
        };
        return Answer(await _broker.AsUser(user.Id, HttpMethod.Get, "api/v1/request", q, cancellationToken: ct).ConfigureAwait(false));
    }

    /// <summary>Approve one ask. Approving hands it to Radarr/Sonarr at once.</summary>
    [HttpPost("{requestId:int}/Approve")]
    public Task<ActionResult> Approve([FromRoute] int requestId, CancellationToken ct) => AnswerRequest(requestId, "approve", ct);

    /// <summary>Decline one ask.</summary>
    [HttpPost("{requestId:int}/Decline")]
    public Task<ActionResult> Decline([FromRoute] int requestId, CancellationToken ct) => AnswerRequest(requestId, "decline", ct);

    private async Task<ActionResult> AnswerRequest(int requestId, string verb, CancellationToken ct)
    {
        if (await CallerAsync().ConfigureAwait(false) is not { } user)
        {
            return Refuse(BrokerRefusal.NeedsAPerson);
        }

        return Answer(await _broker.AsUser(user.Id, HttpMethod.Post, $"api/v1/request/{requestId}/{verb}", cancellationToken: ct).ConfigureAwait(false));
    }

    // ---- Configuring the plugin: Jellyfin admins only ----------------------------------

    /// <summary>Is the broker set up? Never returns the key.</summary>
    [HttpGet("Configuration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> GetConfiguration()
    {
        var config = Plugin.Instance?.Configuration;
        return new
        {
            Configured = config?.Seerr is not null,
            SeerrUrl = config?.SeerrUrl ?? string.Empty,
            HasApiKey = !string.IsNullOrEmpty(config?.SeerrApiKey),
        };
    }

    /// <summary>
    /// Set Seerr's address and key (pushed by an admin's Kale app, which already holds them). Checked
    /// against Seerr first, then read-merge-written into the plugin's configuration: only these two
    /// fields change. The key is never echoed.
    /// </summary>
    [HttpPost("Configuration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> SetConfiguration([FromBody] SeerrConfigurationBody body, CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Refuse(BrokerRefusal.SeerrError);
        }

        var check = await _broker.Check(body.SeerrUrl ?? string.Empty, body.ApiKey ?? string.Empty, ct).ConfigureAwait(false);
        if (check.Ok)
        {
            plugin.Configuration.WithSeerr(body.SeerrUrl!, body.ApiKey!);
            plugin.SaveConfiguration();
            _broker.Forget();
        }

        var status = check.Ok ? 200 : check.Outcome == "unreachable" ? 502 : 400;
        return new ObjectResult(new { check.Outcome, check.Message, Configured = plugin.Configuration.Seerr is not null }) { StatusCode = status };
    }
}
