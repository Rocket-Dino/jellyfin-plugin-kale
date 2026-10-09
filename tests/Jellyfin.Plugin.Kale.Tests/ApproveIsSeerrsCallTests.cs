using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kale.Api;
using Jellyfin.Plugin.Kale.Requests;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kale.Tests;

/// <summary>
/// Who may answer an ask through the broker (kale#308 follow-up, decided 2026-10-09): **Seerr decides.**
/// Every broker call goes out AS the caller, so Seerr's own MANAGE_REQUESTS is the gate. A Jellyfin
/// admin check on top would take approving away from a second parent who has that permission in
/// Seerr and can approve by signing in to Seerr directly. ADR-019: the plugin upgrades, it never
/// gates. Configuring the plugin itself stays Jellyfin-admin only.
/// </summary>
public class ApproveIsSeerrsCallTests
{
    private const string Key = "seerr-api-key-0123456789abcdef";

    // A Jellyfin user who is NOT a Jellyfin administrator (a new User holds no admin permission).
    private static readonly User Parent = new("rach", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");

    private static bool RequiresJellyfinAdmin(string action) =>
        typeof(KaleRequestsController).GetMethod(action)!
            .GetCustomAttributes<AuthorizeAttribute>()
            .Any(a => a.Policy == Policies.RequiresElevation);

    [Theory]
    [InlineData(nameof(KaleRequestsController.Approve))]
    [InlineData(nameof(KaleRequestsController.Decline))]
    [InlineData(nameof(KaleRequestsController.Pending))]
    public void AnsweringAnAskIsNotGatedOnBeingAJellyfinAdmin(string action)
    {
        // ASP.NET enforces this attribute before the action runs: while it is there, a non-admin
        // with Seerr's MANAGE_REQUESTS is refused by Jellyfin and Seerr is never asked.
        Assert.False(RequiresJellyfinAdmin(action), $"{action} must leave the decision to Seerr");
    }

    [Fact]
    public void EveryBrokerRouteStillNeedsASignedInPerson()
    {
        Assert.NotNull(typeof(KaleRequestsController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Theory]
    [InlineData(nameof(KaleRequestsController.SetConfiguration))]
    [InlineData(nameof(KaleRequestsController.GetConfiguration))]
    public void ConfiguringThePluginStaysJellyfinAdminOnly(string action)
    {
        Assert.True(RequiresJellyfinAdmin(action), $"{action} configures the plugin: Jellyfin admins only");
    }

    /// <summary>A Seerr that knows one user (id 12) and answers approve/decline as told.</summary>
    private sealed class FakeSeerr : HttpMessageHandler
    {
        public HttpStatusCode AnswerStatus { get; set; } = HttpStatusCode.OK;

        public string AnswerBody { get; set; } = "{\"id\":5,\"status\":2}";

        public List<HttpRequestMessage> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/user" && request.Method == HttpMethod.Get)
            {
                var users = new { pageInfo = new { results = 1 }, results = new[] { new { id = 12, jellyfinUserId = Parent.Id.ToString("N") } } };
                return Task.FromResult(Reply(HttpStatusCode.OK, JsonSerializer.Serialize(users)));
            }

            return Task.FromResult(Reply(AnswerStatus, AnswerBody));
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class SignedIn : IAuthorizationContext
    {
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext requestContext) => Task.FromResult(Info());

        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest requestContext) => Task.FromResult(Info());

        private static AuthorizationInfo Info() => new() { User = Parent, IsApiKey = false };
    }

    private static KaleRequestsController Controller(FakeSeerr seerr)
    {
        var broker = new SeerrBroker(seerr, () => new SeerrSettings(new Uri("http://seerr.test:5055"), Key), NullLogger<SeerrBroker>.Instance);
        // Approve and decline never read the member's discovery limits.
        return new KaleRequestsController(broker, null!, new SignedIn())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task AParentWithSeerrsPermissionApprovesAsThemselves()
    {
        var seerr = new FakeSeerr();
        var result = await Controller(seerr).Approve(5, CancellationToken.None);
        Assert.Equal(200, Assert.IsType<ContentResult>(result).StatusCode);
        var approve = seerr.Seen.Single(r => r.RequestUri!.AbsolutePath == "/api/v1/request/5/approve");
        Assert.Equal(HttpMethod.Post, approve.Method);
        Assert.Equal("12", approve.Headers.GetValues("X-API-User").Single());
    }

    [Theory]
    [InlineData("Approve")]
    [InlineData("Decline")]
    public async Task WithoutSeerrsPermissionTheAnswerIsSeerrsNoInPlainWords(string verb)
    {
        // Seerr's own answer to a member without MANAGE_REQUESTS.
        var seerr = new FakeSeerr
        {
            AnswerStatus = HttpStatusCode.Forbidden,
            AnswerBody = "{\"message\":\"You do not have permission to access this endpoint.\"}",
        };
        var controller = Controller(seerr);
        var result = verb == "Approve" ? await controller.Approve(5, CancellationToken.None) : await controller.Decline(5, CancellationToken.None);
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, refused.StatusCode);
        Assert.Equal(BrokerRefusal.NotAllowed, refused.Value);
        Assert.Contains(seerr.Seen, r => r.RequestUri!.AbsolutePath == $"/api/v1/request/5/{verb.ToLowerInvariant()}"
            && r.Headers.GetValues("X-API-User").Single() == "12");
    }
}
