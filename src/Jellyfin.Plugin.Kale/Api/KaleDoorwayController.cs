using System.Net.Mime;
using Jellyfin.Plugin.Kale.Share;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kale.Api;

/// <summary>The body of <c>POST /Kale/Share/GuestLocality/Exclude</c>: an address from a current sighting.</summary>
public sealed record ExcludeBody(string? Address);

/// <summary>
/// Kale Share's doorway (kale#373, ADR-020 §1c) and the guest locality check (§1c point 6). Admin only.
/// Answers carry codes, never sentences: the app owns the words. Nothing here restarts Jellyfin (the app
/// asks the admin); the one setting it writes beyond Known Proxies is a LAN-network exclusion, on an admin's
/// request, for an address a guest was seen at.
/// </summary>
[ApiController]
[Route("Kale/Share")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class KaleDoorwayController : ControllerBase
{
    private readonly Doorway _doorway;
    private readonly GuestLocality _locality;

    public KaleDoorwayController(Doorway doorway, GuestLocality locality)
    {
        _doorway = doorway;
        _locality = locality;
    }

    /// <summary>The doorway's state: off, guestsLookLocal, proxyMissing, restartNeeded or live; and whether it may run.</summary>
    [HttpGet("Doorway")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DoorwayStatus> Status() => _doorway.Status();

    /// <summary>
    /// Turn sharing through the doorway on: appends the doorway's upstream to Known Proxies (keeping
    /// every entry the owner has) and flags a pending restart if the running server doesn't trust it
    /// yet. 409 with the status, changing nothing, when Jellyfin would class guests as local.
    /// </summary>
    [HttpPost("Doorway/Enable")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<DoorwayStatus> Enable()
    {
        var (status, refused) = _doorway.Enable();
        return refused ? Conflict(status) : status;
    }

    /// <summary>Turn it off: removes only the Known Proxies entries Kale added.</summary>
    [HttpPost("Doorway/Disable")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DoorwayStatus> Disable() => _doorway.Disable();

    /// <summary>Is any Kale Share guest being treated as local, and what fixes it.</summary>
    [HttpGet("GuestLocality")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<GuestLocalityReport> GuestLocality() => _locality.Report();

    /// <summary>
    /// Apply an <c>excludeAddress</c> fix server-side: append <c>!&lt;address&gt;</c> to Jellyfin's LAN networks.
    /// 409 with a code and the report, writing nothing, unless the address is an open sighting whose fix is
    /// <c>excludeAddress</c>. The answer carries the report made after the write.
    /// </summary>
    [HttpPost("GuestLocality/Exclude")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<GuestLocalityExclusion> Exclude([FromBody] ExcludeBody body)
    {
        var (result, refused) = _locality.Exclude(body.Address);
        return refused ? Conflict(result) : result;
    }
}
