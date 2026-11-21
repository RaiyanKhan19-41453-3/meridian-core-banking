using CoreBanking.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class MetricsController : ControllerBase
{
    private readonly MetricsRegistry _metrics;

    public MetricsController(MetricsRegistry metrics) => _metrics = metrics;

    [HttpGet]
    public IActionResult Snapshot() => Ok(_metrics.Snapshot());

    [HttpGet("prometheus")]
    [Produces("text/plain")]
    public IActionResult Prometheus() => Content(_metrics.Prometheus(), "text/plain; version=0.0.4");
}
