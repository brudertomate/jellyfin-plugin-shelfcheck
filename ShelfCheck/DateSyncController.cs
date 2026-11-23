using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ShelfCheck;

[ApiController]
[Route("ShelfCheck/Dates")]
[Authorize(Policy = Policies.RequiresElevation)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DateSyncController(DateSync sync, DateRunStore store) : ControllerBase
{
    [HttpGet]
    public IActionResult Status() => Ok(sync.Status());

    [HttpPut("Libraries")]
    public IActionResult Libraries([FromBody] Guid[] ids) => Result(sync.SetSelection(ids));

    [HttpPost("Preview")]
    public IActionResult Preview() => Result(sync.Start("Preview"));

    [HttpPost("Apply")]
    public IActionResult Apply() => Result(sync.Start("Apply"));

    [HttpPost("Undo/{id:long}")]
    public IActionResult Undo(long id) => Result(sync.Start("Undo", id));

    [HttpPost("Resume")]
    public IActionResult Resume() => Result(sync.Start("Resume"));

    [HttpPost("Pause")]
    public IActionResult Pause()
    {
        sync.Pause();
        return NoContent();
    }

    [HttpPost("End")]
    public IActionResult End() => Result(sync.EndPaused());

    [HttpPost("Abort")]
    public IActionResult Abort() { sync.Abort(); return NoContent(); }

    [HttpGet("Runs/{id:long}")]
    public IActionResult Report(long id, [FromQuery] string? outcome, [FromQuery] int offset = 0)
    {
        var report = store.Report(id, outcome, offset);
        return report is null ? NotFound() : Ok(sync.EnrichLegacyReport(report));
    }

    private IActionResult Result(string? error) => error is null ? NoContent() : Conflict(new { Message = error });
}
