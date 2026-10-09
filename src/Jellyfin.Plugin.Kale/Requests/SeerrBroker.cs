using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kale.Requests;

/// <summary>Where Seerr is and the key that opens it. Server-side only.</summary>
public sealed record SeerrSettings(Uri BaseUrl, string ApiKey)
{
    // The key must not appear in a record's generated ToString (which a log or a debugger would print).
    public override string ToString() => $"SeerrSettings {{ BaseUrl = {BaseUrl} }}";
}

/// <summary>What the broker hands back to the controller: Seerr's own body, or a refusal.</summary>
public sealed record BrokerResponse(int Status, string? Json, BrokerRefusal? Refusal)
{
    public static BrokerResponse Refused(BrokerRefusal refusal) => new(refusal.Status, null, refusal);
}

/// <summary>The answer to "is this address and key a working Seerr?".</summary>
public sealed record ConfigurationCheck(string Outcome, string Message, bool Ok);

/// <summary>
/// Talks to Seerr for the request broker (kale#308). Every member call goes out AS that member:
/// <c>X-API-Key</c> plus <c>X-API-User</c> set to their Seerr id. Without <c>X-API-User</c> the key
/// is Seerr's user 1, the owner, so a member call that cannot resolve an id is refused, never sent.
/// The key never leaves this class: not in a response, not in a log line, not in an error.
/// </summary>
public sealed class SeerrBroker
{
    private static readonly TimeSpan IdLifetime = TimeSpan.FromHours(1);
    private const int PageSize = 100;
    private const int MaxPages = 50;

    private readonly HttpClient _http;
    private readonly Func<SeerrSettings?> _settings;
    private readonly ILogger<SeerrBroker> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (int Id, DateTimeOffset At)> _ids = new(StringComparer.Ordinal);

    public SeerrBroker(HttpMessageHandler handler, Func<SeerrSettings?> settings, ILogger<SeerrBroker> logger, TimeProvider? time = null)
    {
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool IsConfigured => _settings() is not null;

    /// <summary>Forget every cached Seerr id (the address or key changed).</summary>
    public void Forget() => _ids.Clear();

    /// <summary>Seerr's own form of a Jellyfin id: no dashes, lower case (its <c>normalizeJellyfinGuid</c>).</summary>
    public static string Normalise(string? id) => (id ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>
    /// The caller's Seerr id: from Seerr's user list matched on <c>jellyfinUserId</c> (dashes and case
    /// ignored), cached for an hour; imported from Jellyfin when Seerr has never seen them.
    /// </summary>
    public async Task<int?> SeerrIdFor(Guid jellyfinUserId, CancellationToken cancellationToken = default)
    {
        var settings = _settings();
        if (settings is null)
        {
            return null;
        }

        var wanted = jellyfinUserId.ToString("N");
        if (_ids.TryGetValue(wanted, out var known) && _time.GetUtcNow() - known.At < IdLifetime)
        {
            return known.Id;
        }

        var id = await FindInUserList(settings, wanted, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            // Never seen: import, with the household's defaultPermissions, as a first sign-in would.
            var body = new JsonObject { ["jellyfinUserIds"] = new JsonArray(wanted) };
            using var res = await Send(settings, HttpMethod.Post, "api/v1/user/import-from-jellyfin", null, body, actAs: null, cancellationToken).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                _logger.LogInformation("kale requests: imported a Jellyfin user into Seerr");
                id = await FindInUserList(settings, wanted, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("kale requests: Seerr refused to import a Jellyfin user (HTTP {Status})", (int)res.StatusCode);
            }
        }

        if (id is { } found)
        {
            _ids[wanted] = (found, _time.GetUtcNow());
        }

        return id;
    }

    private async Task<int?> FindInUserList(SeerrSettings settings, string wanted, CancellationToken cancellationToken)
    {
        int skip = 0;
        for (int page = 0; page < MaxPages; page++)
        {
            var query = new[]
            {
                new KeyValuePair<string, string>("take", PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("skip", skip.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            using var res = await Send(settings, HttpMethod.Get, "api/v1/user", query, null, actAs: null, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("kale requests: Seerr's user list answered HTTP {Status}", (int)res.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            int count = 0;
            foreach (var user in results.EnumerateArray())
            {
                count++;
                if (user.TryGetProperty("jellyfinUserId", out var jf) && jf.ValueKind == JsonValueKind.String
                    && Normalise(jf.GetString()) == wanted
                    && user.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
                {
                    return id;
                }
            }

            // Advance by what came back (a server may cap `take`); stop at the total or an empty page.
            skip += count;
            int total = doc.RootElement.TryGetProperty("pageInfo", out var info)
                && info.TryGetProperty("results", out var t) && t.TryGetInt32(out var n) ? n : int.MaxValue;
            if (count == 0 || skip >= total)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Call Seerr as this Jellyfin user. <paramref name="body"/> is given the caller's Seerr id.</summary>
    public async Task<BrokerResponse> AsUser(
        Guid jellyfinUserId,
        HttpMethod method,
        string path,
        IReadOnlyList<KeyValuePair<string, string>>? query = null,
        Func<int, JsonNode?>? body = null,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings();
        if (settings is null)
        {
            return BrokerResponse.Refused(BrokerRefusal.NotConfigured);
        }

        try
        {
            var seerrId = await SeerrIdFor(jellyfinUserId, cancellationToken).ConfigureAwait(false);
            if (seerrId is not { } id)
            {
                // Without an id the only thing left to send is the key alone, which IS the owner.
                return BrokerResponse.Refused(BrokerRefusal.NoSeerrAccount);
            }

            using var res = await Send(settings, method, path, query, body?.Invoke(id), actAs: id, cancellationToken).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Contains(settings.ApiKey, StringComparison.Ordinal))
            {
                // Never forward a body that carries the key, whatever Seerr meant by it.
                _logger.LogWarning("kale requests: a Seerr answer to {Path} carried the API key and was withheld", path);
                return BrokerResponse.Refused(BrokerRefusal.SeerrError);
            }

            int status = (int)res.StatusCode;
            if (res.IsSuccessStatusCode)
            {
                return new BrokerResponse(status, string.IsNullOrWhiteSpace(text) ? null : text, null);
            }

            _logger.LogInformation("kale requests: Seerr answered {Path} with HTTP {Status}", path, status);
            if (status == 403)
            {
                return BrokerResponse.Refused(BrokerRefusal.FromSeerr403(MessageOf(text)));
            }

            if (status == 409)
            {
                return BrokerResponse.Refused(BrokerRefusal.AlreadyRequested);
            }

            if (status == 404)
            {
                return BrokerResponse.Refused(BrokerRefusal.SeerrError with { Status = 404 });
            }

            return BrokerResponse.Refused(BrokerRefusal.SeerrError);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The exception's own text can carry the address; the type is enough to diagnose.
            _logger.LogWarning("kale requests: Seerr could not be reached ({Kind})", e.GetType().Name);
            return BrokerResponse.Refused(BrokerRefusal.Unreachable);
        }
    }

    /// <summary>Is this a Seerr, and does it accept this key? Writes nothing.</summary>
    public async Task<ConfigurationCheck> Check(string url, string apiKey, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(apiKey))
        {
            return new ConfigurationCheck("badUrl", "That doesn't look like a Seerr address.", false);
        }

        var settings = new SeerrSettings(uri, apiKey.Trim());
        try
        {
            using var status = await Send(settings, HttpMethod.Get, "api/v1/status", null, null, actAs: null, cancellationToken).ConfigureAwait(false);
            if (!status.IsSuccessStatusCode)
            {
                return new ConfigurationCheck("badUrl", "Something answered at that address, but it isn't Seerr.", false);
            }

            using var me = await Send(settings, HttpMethod.Get, "api/v1/auth/me", null, null, actAs: null, cancellationToken).ConfigureAwait(false);
            return me.IsSuccessStatusCode
                ? new ConfigurationCheck("saved", "Requests now go through your server.", true)
                : new ConfigurationCheck("keyRefused", "Seerr didn't accept that API key.", false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("kale requests: Seerr could not be reached while checking settings ({Kind})", e.GetType().Name);
            return new ConfigurationCheck("unreachable", "Your server couldn't reach Seerr at that address.", false);
        }
    }

    private async Task<HttpResponseMessage> Send(
        SeerrSettings settings,
        HttpMethod method,
        string path,
        IReadOnlyList<KeyValuePair<string, string>>? query,
        JsonNode? body,
        int? actAs,
        CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(new Uri(settings.BaseUrl.AbsoluteUri.TrimEnd('/') + "/" + path.TrimStart('/')));
        if (query is { Count: > 0 })
        {
            builder.Query = string.Join('&', query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        }

        using var request = new HttpRequestMessage(method, builder.Uri);
        // Both headers, never the query: a URL reaches logs.
        request.Headers.TryAddWithoutValidation("X-API-Key", settings.ApiKey);
        if (actAs is { } id)
        {
            request.Headers.TryAddWithoutValidation("X-API-User", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static string? MessageOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
