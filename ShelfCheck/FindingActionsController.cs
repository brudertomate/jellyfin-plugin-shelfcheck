using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ShelfCheck;

[ApiController]
[Route("ShelfCheck/Actions")]
[Authorize(Policy = Policies.RequiresElevation)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class FindingActionsController(FindingActions actions) : ControllerBase
{
    [HttpPost("Preview")]
    public IActionResult Preview([FromBody] ActionRequest request)
    {
        try { return Ok(actions.Prepare(request)); }
        catch (InvalidOperationException ex) { return Conflict(new { Message = ex.Message }); }
    }

    [HttpPost("{id:guid}/Start")]
    public IActionResult Start(Guid id)
    {
        try { actions.Start(id); return NoContent(); }
        catch (InvalidOperationException ex) { return Conflict(new { Message = ex.Message }); }
    }

    [HttpGet]
    public IActionResult Status() => Ok(new { Job = actions.Status() });

    [HttpPost("Cancel")]
    public IActionResult Cancel() { actions.Cancel(); return NoContent(); }
}
