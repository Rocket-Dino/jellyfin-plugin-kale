using System;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kale.Share;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kale.Api;

/// <summary>The body of <c>POST /Kale/Share/Grants</c>: the hash of a secret the owner's app made.</summary>
public sealed record CreateGrantBody(Guid UserId, string? SecretHash, string? Label, DateTime ExpiresUtc);

/// <summary>The body of <c>PUT /Kale/Share/Grants/{id}/Spares</c>: the sealed keys, base64.</summary>
public sealed record SparesBody(string? Spares);

/// <summary>The body of <c>POST /Kale/Share/SelfGrants</c>: the hash of a secret the caller's own app made.</summary>
public sealed record SelfGrantBody(string? SecretHash);

/// <summary>The body of <c>POST /Kale/Share/Redeem</c>: the secret from the invitation, as hex.</summary>
public sealed record RedeemBody(string? Secret);

/// <summary>
/// Kale Share invitations sign the guest in without a password or a code to approve (kale#331). The
/// owner (a Jellyfin admin) registers a grant; the guest's app redeems the secret once and gets its
/// own Jellyfin session as the account the owner chose. Without this plugin Kale falls back to Quick
/// Connect, approved by the owner (ADR-019: the plugin upgrades, it never gates).
/// </summary>
[ApiController]
[Route("Kale/Share")]
[Produces(MediaTypeNames.Application.Json)]
public class KaleShareController : ControllerBase
{
    private readonly ShareGrants _grants;
    private readonly IUserManager _users;
    private readonly ISessionManager _sessions;
    private readonly IAuthorizationContext _auth;
    private readonly IServerConfigurationManager _config;
    private readonly ILogger<KaleShareController> _logger;

    public KaleShareController(ShareGrants grants, IUserManager users, ISessionManager sessions, IAuthorizationContext auth,
        IServerConfigurationManager config, ILogger<KaleShareController> logger)
    {
        _grants = grants;
        _users = users;
        _sessions = sessions;
        _auth = auth;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Whether a signed-in person may make a sign-in for another of their own devices: exactly when
    /// Quick Connect is on, because that is the server's existing answer to the same question (any
    /// signed-in user may approve a Quick Connect code for their own account). An admin who turned
    /// Quick Connect off has said no to both.
    /// </summary>
    public static bool SelfGrantsAllowed(IServerConfigurationManager config) => config.Configuration.QuickConnectAvailable;

    /// <summary>Register an invitation's sign-in for one account. Admin only.</summary>
    [HttpPost("Grants")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> CreateGrant([FromBody] CreateGrantBody body)
    {
        if (_users.GetUserById(body.UserId) is null)
        {
            return BadRequest(new { Reason = "noSuchUser" });
        }

        var grant = _grants.Create(body.UserId, body.SecretHash ?? string.Empty, body.Label ?? string.Empty, body.ExpiresUtc.ToUniversalTime());
        if (grant is null)
        {
            return BadRequest(new { Reason = "invalidGrant" });
        }

        _logger.LogInformation("Kale Share: invitation {Grant} registered for user {User}, expires {Expires}", grant.Id, grant.UserId, grant.ExpiresUtc);
        return new { grant.Id, grant.ExpiresUtc };
    }

    /// <summary>Every invitation's sign-in the owner can still see, without hashes. Admin only.</summary>
    [HttpGet("Grants")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> ListGrants() =>
        _grants.List().Select(g => new
        {
            g.Id,
            g.UserId,
            g.Label,
            g.CreatedUtc,
            g.ExpiresUtc,
            g.RedeemedUtc,
            g.RedeemedDeviceName,
            g.FromDevice,
        }).ToList();

    /// <summary>Attach the invitation's sealed keys for the guest's other devices. Admin only.</summary>
    [HttpPut("Grants/{id}/Spares")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult SetSpares([FromRoute] Guid id, [FromBody] SparesBody body) =>
        _grants.SetSpares(id, body.Spares ?? string.Empty) ? NoContent() : BadRequest(new { Reason = "invalidSpares" });

    /// <summary>Withdraw an invitation's sign-in. A session already made by it is not touched. Admin only.</summary>
    [HttpDelete("Grants/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult WithdrawGrant([FromRoute] Guid id) => _grants.Withdraw(id) ? NoContent() : NotFound();

    /// <summary>
    /// A one-time sign-in for another of the CALLER's own devices (Add Another Device). Any signed-in
    /// person, for their own account only: the account is the session's, never a body field. One day,
    /// single use, at most five open. No more than Quick Connect already lets them do, and only while
    /// it is on (<see cref="SelfGrantsAllowed"/>).
    /// </summary>
    [HttpPost("SelfGrants")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<object>> CreateSelfGrant([FromBody] SelfGrantBody body)
    {
        var info = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (info.IsApiKey || info.User is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Reason = "noUser" });
        }

        if (info.User.HasPermission(PermissionKind.IsDisabled))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Reason = "accountDisabled" });
        }

        if (!SelfGrantsAllowed(_config))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Reason = "quickConnectOff" });
        }

        var (outcome, grant) = _grants.CreateSelf(info.User.Id, body.SecretHash ?? string.Empty, info.Device ?? string.Empty, info.User.Username);
        switch (outcome)
        {
            case SelfGrantOutcome.TooMany:
                return Conflict(new { Reason = "tooMany" });
            case SelfGrantOutcome.Invalid:
                return BadRequest(new { Reason = "invalidGrant" });
        }

        _logger.LogInformation("Kale Share: user {User} made a sign-in {Grant} for another of their devices, from {Device}", grant!.UserId, grant.Id, grant.FromDevice);
        return new { grant.Id, grant.ExpiresUtc };
    }

    /// <summary>The caller's own device sign-ins, without hashes.</summary>
    [HttpGet("SelfGrants")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> ListSelfGrants()
    {
        var info = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (info.IsApiKey || info.User is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Reason = "noUser" });
        }

        return _grants.ListOwn(info.User.Id).Select(g => new
        {
            g.Id,
            g.CreatedUtc,
            g.ExpiresUtc,
            g.RedeemedUtc,
            g.RedeemedDeviceName,
            g.FromDevice,
        }).ToList();
    }

    /// <summary>Withdraw one of the caller's own device sign-ins. Anyone else's is not found.</summary>
    [HttpDelete("SelfGrants/{id}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> WithdrawSelfGrant([FromRoute] Guid id)
    {
        var info = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (info.IsApiKey || info.User is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Reason = "noUser" });
        }

        return _grants.WithdrawOwn(info.User.Id, id) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Sign in with an invitation. Anonymous by necessity (the guest has no session yet): the secret is
    /// the credential, 16+ random bytes, checked against stored hashes in fixed time. The device is the
    /// one named in the request's Authorization header, as for any Jellyfin sign-in.
    /// </summary>
    [HttpPost("Redeem")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<object>> Redeem([FromBody] RedeemBody body)
    {
        var info = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (string.IsNullOrEmpty(info.DeviceId) || string.IsNullOrEmpty(info.Device)
            || string.IsNullOrEmpty(info.Client) || string.IsNullOrEmpty(info.Version))
        {
            return BadRequest(new { Reason = "noDevice" });
        }

        var (outcome, grant, fresh) = _grants.Redeem(body.Secret ?? string.Empty, info.DeviceId, info.Device);
        switch (outcome)
        {
            case RedeemOutcome.Unknown:
                return NotFound(new { Reason = "unknown" });
            case RedeemOutcome.Expired:
                return StatusCode(StatusCodes.Status410Gone, new { Reason = "expired" });
            case RedeemOutcome.Used:
                return StatusCode(StatusCodes.Status410Gone, new { Reason = "used" });
        }

        var user = _users.GetUserById(grant!.UserId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled))
        {
            // AuthenticateDirect does not check this itself. Leave the invitation usable for when the
            // owner restores the account.
            if (fresh)
            {
                _grants.Unredeem(grant.Id);
            }

            return StatusCode(StatusCodes.Status403Forbidden, new { Reason = user is null ? "noSuchUser" : "accountDisabled" });
        }

        try
        {
            var result = await _sessions.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = user.Id,
                DeviceId = info.DeviceId,
                DeviceName = info.Device,
                App = info.Client,
                AppVersion = info.Version,
                RemoteEndPoint = HttpContext.Connection.RemoteIpAddress?.ToString(),
            }).ConfigureAwait(false);
            _logger.LogInformation("Kale Share: invitation {Grant} signed in user {User} on device {Device}", grant.Id, user.Id, info.Device);
            // Jellyfin's own AuthenticationResult fields, plus the sealed keys for the guest's other devices.
            return new
            {
                result.User,
                result.SessionInfo,
                result.AccessToken,
                result.ServerId,
                KaleSpares = grant.Spares,
            };
        }
        catch (Exception e) when (e is System.Security.SecurityException or System.Security.Authentication.AuthenticationException)
        {
            if (fresh)
            {
                _grants.Unredeem(grant.Id);
            }

            _logger.LogWarning("Kale Share: invitation {Grant} refused by Jellyfin: {Message}", grant.Id, e.Message);
            return StatusCode(StatusCodes.Status403Forbidden, new { Reason = "refused" });
        }
    }
}
