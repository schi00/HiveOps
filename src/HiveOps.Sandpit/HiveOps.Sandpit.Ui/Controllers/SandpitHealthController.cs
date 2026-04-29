using HiveOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HiveOps.Sandpit.Ui.Controllers;

[ApiController]
[Route("api/sandpit/health")]
public class SandpitHealthController(AppDbContext db, ILogger<SandpitHealthController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        logger.LogInformation("[SANDBOX-HEALTH] Health check requested");

        var dbHealthy = false;
        var dbName = "unknown";
        try
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            dbName = connection.Database;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
            dbHealthy = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SANDBOX-HEALTH] Database connectivity check failed");
        }

        return Ok(new
        {
            status = dbHealthy ? "healthy" : "unhealthy",
            database = dbName,
            isolationVerified = dbName == "HiveOps_Sandpit",
            timestamp = DateTimeOffset.UtcNow,
            controllers = new[] { "DatabaseFaultController", "GithubMockController" }
        });
    }
}
