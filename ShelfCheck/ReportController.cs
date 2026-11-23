using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace ShelfCheck;

[ApiController]
[Route("ShelfCheck")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ReportController(ReportStore store, ILibraryManager libraries, ScanControl control) : ControllerBase
{
    [HttpGet("Libraries")]
    public IActionResult Libraries() => Ok(new
    {
        Excluded = store.Excluded,
        Items = libraries.GetVirtualFolders(false)
            .Select(f => new { Id = f.ItemId, f.Name, Type = f.CollectionType?.ToString() ?? "mixed",
                Supported = f.CollectionType is null or CollectionTypeOptions.mixed or CollectionTypeOptions.movies or
                    CollectionTypeOptions.tvshows or CollectionTypeOptions.musicvideos or CollectionTypeOptions.homevideos })
    });

    [HttpPut("Libraries")]
    public IActionResult SetLibraries([FromBody] Guid[] excluded)
    {
        store.Excluded = excluded.Distinct().ToArray();
        return NoContent();
    }

    [HttpPost("Pause")]
    public IActionResult Pause() { control.SetPaused(true); return NoContent(); }

    [HttpPost("Resume")]
    public IActionResult Resume() { control.SetPaused(false); return NoContent(); }

    [HttpGet("Report")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Get([FromQuery] string? code, [FromQuery] string? library, [FromQuery] string? search,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
    {
        var report = store.Current;
        if (report is null) return Ok(new { Available = false });

        var matches = report.Findings.Where(f =>
            (string.IsNullOrEmpty(code) || f.Code == code) &&
            (string.IsNullOrEmpty(library) || f.Library == library) &&
            (string.IsNullOrEmpty(search) || f.Path.Contains(search, StringComparison.OrdinalIgnoreCase)));
        var count = matches.Count();
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Clamp(offset, 0, Math.Max(0, (count - 1) / limit * limit));
        return Ok(new
        {
            Available = true, report.StartedUtc, report.FinishedUtc, report.Roots,
            report.UpdatedUtc, report.State, report.CurrentPath, Paused = control.Paused,
            report.Directories, report.Titles, report.Seasons, report.Videos,
            Total = report.Findings.Count, Filtered = count, Offset = offset,
            Items = matches.Skip(offset).Take(limit).ToArray()
        });
    }
}
