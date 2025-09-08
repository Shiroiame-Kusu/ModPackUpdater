using Microsoft.AspNetCore.Mvc;
using ModPackUpdater.Models;
using ModPackUpdater.Services;

namespace ModPackUpdater.Controllers;

[ApiController]
[Route("packs")]
public class PacksController : ControllerBase
{
    private readonly PackService _svc;
    public PacksController(PackService svc) => _svc = svc;

    // GET /packs/
    [HttpGet("")]
    public ActionResult<IEnumerable<string>> ListPacks()
        => Ok(_svc.GetPackIds());

    // GET /packs/{packId}
    [HttpGet("{packId}")]
    public ActionResult<PackSummary> GetSummary([FromRoute] string packId)
    {
        var versions = _svc.GetVersions(packId);
        if (versions.Count == 0) return NotFound();
        var latest = versions.First();
        var summary = new PackSummary(packId, latest, versions);
        return Ok(summary);
    }

    // GET /packs/{packId}/manifest?version=...
    [HttpGet("{packId}/manifest")]
    public async Task<ActionResult<ModPackManifest>> GetManifest([FromRoute] string packId, [FromQuery] string? version, CancellationToken ct)
    {
        version ??= _svc.GetLatestVersion(packId);
        if (string.IsNullOrWhiteSpace(version)) return NotFound();
        var manifest = await _svc.TryGetManifestAsync(packId, version!, ct);
        return manifest is null ? NotFound() : Ok(manifest);
    }

    // GET /packs/{packId}/file?path=...&version=...
    [HttpGet("{packId}/file")]
    public ActionResult GetFile([FromRoute] string packId, [FromQuery] string? version, [FromQuery] string path)
    {
        version ??= _svc.GetLatestVersion(packId);
        if (string.IsNullOrWhiteSpace(version)) return NotFound();
        if (!_svc.TryResolveFile(packId, version!, path, out var full)) return NotFound();
        var fileName = System.IO.Path.GetFileName(path);
        return PhysicalFile(full!, "application/octet-stream", fileName, enableRangeProcessing: true);
    }
}
