using System;
using System.Net.Mime;
using System.Reflection;
using Jellyfin.Plugin.Kale.Repair;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kale.Api;

/// <summary>
/// The Kale app's server-side repairs. Admin only, by Jellyfin's own policy; one item and
/// one change per request (ADR-019 amendment). <see cref="Capabilities"/> answers any signed-in
/// user, because a member's app has to know whether requests go through the broker (kale#308).
/// </summary>
[ApiController]
[Route("Kale")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class KaleController : ControllerBase
{
    private readonly DefaultAudioRepair _repair;

    public KaleController(DefaultAudioRepair repair)
    {
        _repair = repair;
    }

    /// <summary>
    /// What this plugin can do. The app detects every feature by this body, never by version.
    /// Any signed-in user: it names features, not files, and holds nothing secret.
    /// </summary>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Capabilities() => new
    {
        Version = typeof(KaleController).Assembly.GetName().Version?.ToString(),
        Repairs = new[] { "defaultAudio" },
        Requests = new { Configured = Plugin.Instance?.Configuration.Seerr is not null },
    };

    /// <summary>Would making this audio stream the default work? Reads the file; writes nothing.</summary>
    [HttpGet("Items/{itemId}/DefaultAudio")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RepairResult> Check([FromRoute] Guid itemId, [FromQuery] int audioStreamIndex) =>
        _repair.Check(itemId, audioStreamIndex);

    /// <summary>Make this audio stream the file's only default soundtrack.</summary>
    [HttpPost("Items/{itemId}/DefaultAudio")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RepairResult> Apply([FromRoute] Guid itemId, [FromQuery] int audioStreamIndex) =>
        _repair.Apply(itemId, audioStreamIndex);

    /// <summary>Put the file's default flags back the way they were before Kale first changed them.</summary>
    [HttpPost("Items/{itemId}/DefaultAudio/Undo")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RepairResult> Undo([FromRoute] Guid itemId) => _repair.Undo(itemId);
}
