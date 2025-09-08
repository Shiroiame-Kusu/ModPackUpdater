using Microsoft.AspNetCore.Mvc;
using ModPackUpdater.Models;

namespace ModPackUpdater.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public ActionResult<HealthResponse> Get() => Ok(new HealthResponse("ok"));
}

