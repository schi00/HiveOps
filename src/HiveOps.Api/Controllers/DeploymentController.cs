using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HiveOps.Application.Configuration;

namespace HiveOps.Api.Controllers;

/// <summary>Read-only deployment metadata for authenticated clients (dashboard).</summary>
[ApiController]
[Route("api/deployment")]
[Authorize]
public sealed class DeploymentController : ControllerBase
{
    private readonly HiveOpsDeploymentOptions _options;

    public DeploymentController(IOptions<HiveOpsDeploymentOptions> options)
    {
        _options = options.Value;
    }

    [HttpGet("info")]
    public ActionResult<DeploymentInfoDto> GetInfo()
    {
        return Ok(new DeploymentInfoDto(_options.Mode.ToString()));
    }

    public sealed record DeploymentInfoDto(string Mode);
}
