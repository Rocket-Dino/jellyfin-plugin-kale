using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Plugin.Kale.Configuration;
using Jellyfin.Plugin.Kale.Requests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kale.Tests;

/// <summary>
/// The broker's wire rules (kale#308), against a fake Seerr. The ones that matter most: a member call
/// never leaves without <c>X-API-User</c> (without it the key is Seerr's owner), and the key never
/// comes back out in a body, a refusal or a log line.
/// </summary>
public class SeerrBrokerTests
{
    private const string Key = "seerr-api-key-0123456789abcdef";
    private static readonly Guid Kid = Guid.Parse("8f2a4c1e-0d6b-4b7a-9e3f-5a1c2b3d4e5f");
    private static readonly Guid Adult = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>A Seerr in a box: answers by path, records every request it saw.</summary>
    private sealed class FakeSeerr : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = new();

        public List<string> Bodies { get; } = new();

        public Func<HttpRequestMessage, (HttpStatusCode, string)?>? Override { get; set; }

        public List<object> Users { get; } = new()
        {
            new { id = 1, jellyfinUserId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", jellyfinUsername = "owner" },
            new { id = 7, jellyfinUserId = "111111112222333344445555555555555", jellyfinUsername = "typo" },
            new { id = 9, jellyfinUserId = "11111111222233334444555555555555", jellyfinUsername = "adult" },
        };

        public bool ImportWorks { get; set; } = true;

        public int ListCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var path = request.RequestUri!.AbsolutePath;
            var o = Override?.Invoke(request);
            if (o is { } hit)
            {
                return Reply(hit.Item1, hit.Item2);
            }

            if (path == "/api/v1/user" && request.Method == HttpMethod.Get)
            {
                ListCalls++;
                var skip = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["skip"] ?? "0");
                var page = Users.Skip(skip).Take(2).ToList();
                return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { pageInfo = new { pages = (Users.Count + 1) / 2, results = Users.Count }, results = page }));
            }

            if (path == "/api/v1/user/import-from-jellyfin")
            {
                if (!ImportWorks)
                {
                    return Reply(HttpStatusCode.InternalServerError, "{\"message\":\"no jellyfin key\"}");
                }

                Users.Add(new { id = 42, jellyfinUserId = Kid.ToString("N"), jellyfinUsername = "kid" });
                return Reply(HttpStatusCode.Created, JsonSerializer.Serialize(new[] { new { id = 42, jellyfinUserId = Kid.ToString("N") } }));
            }

            if (path == "/api/v1/status")
            {
                return Reply(HttpStatusCode.OK, "{\"version\":\"3.5.0\"}");
            }

            if (path == "/api/v1/auth/me")
            {
                return request.Headers.TryGetValues("X-API-Key", out var k) && k.Single() == Key
                    ? Reply(HttpStatusCode.OK, "{\"id\":1,\"permissions\":2}")
                    : Reply(HttpStatusCode.Forbidden, "{\"message\":\"You do not have permission to access this endpoint.\"}");
            }

            return Reply(HttpStatusCode.OK, "{\"results\":[]}");
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-08T00:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Captures every log line, so a test can prove the key is in none of them.</summary>
    private sealed class CapturingLogger : ILogger<SeerrBroker>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }

    private static SeerrBroker Broker(FakeSeerr seerr, Clock? clock = null, ILogger<SeerrBroker>? logger = null, bool configured = true) =>
        new(seerr, () => configured ? new SeerrSettings(new Uri("http://seerr.test:5055"), Key) : null, logger ?? NullLogger<SeerrBroker>.Instance, clock);

    [Fact]
    public async Task TheCallerIsResolvedFromTheUserListOnJellyfinUserId()
    {
        var seerr = new FakeSeerr();
        // Jellyfin writes ids with dashes; Seerr stores them without. A near-miss (id 7) must not match.
        Assert.Equal(9, await Broker(seerr).SeerrIdFor(Adult));
    }

    [Fact]
    public async Task AResolvedIdIsCachedForAnHourThenLookedUpAgain()
    {
        var seerr = new FakeSeerr();
        var clock = new Clock();
        var broker = Broker(seerr, clock);
        await broker.SeerrIdFor(Adult);
        var calls = seerr.ListCalls;
        await broker.SeerrIdFor(Adult);
        Assert.Equal(calls, seerr.ListCalls);
        clock.Now = clock.Now.AddMinutes(61);
        await broker.SeerrIdFor(Adult);
        Assert.True(seerr.ListCalls > calls);
    }

    [Fact]
    public async Task SomebodySeerrHasNeverSeenIsImportedLikeAFirstSignIn()
    {
        var seerr = new FakeSeerr();
        Assert.Equal(42, await Broker(seerr).SeerrIdFor(Kid));
        var import = seerr.Seen.FindIndex(r => r.RequestUri!.AbsolutePath == "/api/v1/user/import-from-jellyfin");
        Assert.True(import >= 0);
        Assert.Contains(Kid.ToString("N"), seerr.Bodies[import], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemberCallAlwaysCarriesXApiUserAndTheKey()
    {
        var seerr = new FakeSeerr();
        var r = await Broker(seerr).AsUser(Adult, HttpMethod.Get, "api/v1/search", new[] { new KeyValuePair<string, string>("query", "star wars") });
        Assert.Equal(200, r.Status);
        var search = seerr.Seen.Single(x => x.RequestUri!.AbsolutePath == "/api/v1/search");
        Assert.Equal("9", search.Headers.GetValues("X-API-User").Single());
        Assert.Equal(Key, search.Headers.GetValues("X-API-Key").Single());
        Assert.Equal("star wars", System.Web.HttpUtility.ParseQueryString(search.RequestUri!.Query)["query"]);
    }

    [Fact]
    public async Task WithNoSeerrIdTheCallIsRefusedAndNeverSentAsTheOwner()
    {
        var seerr = new FakeSeerr { ImportWorks = false };
        var r = await Broker(seerr).AsUser(Kid, HttpMethod.Post, "api/v1/request", body: _ => new JsonObject { ["mediaType"] = "movie", ["mediaId"] = 1 });
        Assert.Equal("noSeerrAccount", r.Refusal?.Outcome);
        Assert.DoesNotContain(seerr.Seen, x => x.RequestUri!.AbsolutePath == "/api/v1/request");
    }

    [Fact]
    public async Task TheBodyBuilderIsGivenTheCallersSeerrId()
    {
        var seerr = new FakeSeerr();
        await Broker(seerr).AsUser(Adult, HttpMethod.Post, "api/v1/blocklist", body: id => new JsonObject { ["tmdbId"] = 5, ["user"] = id });
        var i = seerr.Seen.FindIndex(x => x.RequestUri!.AbsolutePath == "/api/v1/blocklist");
        Assert.Equal(9, JsonNode.Parse(seerr.Bodies[i])!["user"]!.GetValue<int>());
    }

    [Fact]
    public async Task NotConfiguredIsARefusalNotACall()
    {
        var seerr = new FakeSeerr();
        var r = await Broker(seerr, configured: false).AsUser(Adult, HttpMethod.Get, "api/v1/search");
        Assert.Equal("notConfigured", r.Refusal?.Outcome);
        Assert.Equal(503, r.Status);
        Assert.Empty(seerr.Seen);
    }

    [Fact]
    public async Task SeerrsForbiddenBecomesAPlainWordsRefusal()
    {
        var seerr = new FakeSeerr
        {
            Override = r => r.RequestUri!.AbsolutePath == "/api/v1/request" ? (HttpStatusCode.Forbidden, "{\"message\":\"Movie Quota exceeded.\"}") : null,
        };
        var r = await Broker(seerr).AsUser(Adult, HttpMethod.Post, "api/v1/request", body: _ => new JsonObject());
        Assert.Equal("quotaSpent", r.Refusal?.Outcome);
        Assert.Equal(403, r.Status);
    }

    [Fact]
    public async Task AskingTwiceIsAlreadyRequestedNotAServerFault()
    {
        var seerr = new FakeSeerr
        {
            Override = r => r.RequestUri!.AbsolutePath == "/api/v1/request" ? (HttpStatusCode.Conflict, "{\"message\":\"Request for this media already exists.\"}") : null,
        };
        var r = await Broker(seerr).AsUser(Adult, HttpMethod.Post, "api/v1/request", body: _ => new JsonObject());
        Assert.Equal("alreadyRequested", r.Refusal?.Outcome);
        Assert.Equal(409, r.Status);
    }

    [Fact]
    public async Task AnUnreachableSeerrSaysSoWithoutTheAddressOrKey()
    {
        var logger = new CapturingLogger();
        var seerr = new FakeSeerr { Override = _ => throw new HttpRequestException("connection refused (seerr.test:5055) X-API-Key=" + Key) };
        var r = await Broker(seerr, logger: logger).AsUser(Adult, HttpMethod.Get, "api/v1/search");
        Assert.Equal("unreachable", r.Refusal?.Outcome);
        Assert.DoesNotContain(Key, r.Refusal!.Message, StringComparison.Ordinal);
        Assert.All(logger.Lines, l => Assert.DoesNotContain(Key, l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABodyCarryingTheKeyIsWithheld()
    {
        var logger = new CapturingLogger();
        var seerr = new FakeSeerr
        {
            Override = r => r.RequestUri!.AbsolutePath == "/api/v1/movie/1" ? (HttpStatusCode.OK, "{\"apiKey\":\"" + Key + "\"}") : null,
        };
        var r = await Broker(seerr, logger: logger).AsUser(Adult, HttpMethod.Get, "api/v1/movie/1");
        Assert.Null(r.Json);
        Assert.Equal("seerrError", r.Refusal?.Outcome);
        Assert.All(logger.Lines, l => Assert.DoesNotContain(Key, l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnyOtherSeerrFailureIsSeerrError()
    {
        var seerr = new FakeSeerr
        {
            Override = r => r.RequestUri!.AbsolutePath == "/api/v1/movie/0" ? (HttpStatusCode.InternalServerError, "{\"message\":\"Unable to retrieve movie.\"}") : null,
        };
        var r = await Broker(seerr).AsUser(Adult, HttpMethod.Get, "api/v1/movie/0");
        Assert.Equal("seerrError", r.Refusal?.Outcome);
        Assert.Equal(502, r.Status);
    }

    [Fact]
    public async Task ACheckAcceptsAWorkingKeyAndRefusesAWrongOneWithoutEchoingIt()
    {
        var seerr = new FakeSeerr();
        var broker = Broker(seerr);
        var ok = await broker.Check("http://seerr.test:5055", Key);
        Assert.True(ok.Ok);
        var bad = await broker.Check("http://seerr.test:5055", "wrong-key-value-xyz");
        Assert.False(bad.Ok);
        Assert.Equal("keyRefused", bad.Outcome);
        Assert.DoesNotContain("wrong-key-value-xyz", bad.Message, StringComparison.Ordinal);
        var url = await broker.Check("ftp://nope", Key);
        Assert.Equal("badUrl", url.Outcome);
    }

    [Fact]
    public void TheKeyPersistsToDiskButNeverSerialisesToJson()
    {
        var config = new PluginConfiguration { SeerrUrl = "http://seerr.test:5055", SeerrApiKey = Key };
        var json = JsonSerializer.Serialize(config);
        Assert.DoesNotContain(Key, json, StringComparison.Ordinal);
        Assert.Contains("seerr.test", json, StringComparison.Ordinal);

        var xml = new XmlSerializer(typeof(PluginConfiguration));
        using var w = new System.IO.StringWriter();
        xml.Serialize(w, config);
        Assert.Contains(Key, w.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SavingSeerrSettingsCarriesEverythingElse()
    {
        var config = new PluginConfiguration { SeerrUrl = "http://old:5055", SeerrApiKey = "old" };
        var merged = config.WithSeerr("http://seerr.test:5055/", Key);
        Assert.Same(config, merged);
        Assert.Equal("http://seerr.test:5055", merged.SeerrUrl);
        Assert.Equal(Key, merged.SeerrApiKey);
        Assert.Equal(new SeerrSettings(new Uri("http://seerr.test:5055"), Key), merged.Seerr);
        Assert.Null(new PluginConfiguration().Seerr);
    }
}
