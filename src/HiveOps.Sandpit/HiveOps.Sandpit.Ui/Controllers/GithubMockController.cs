using Microsoft.AspNetCore.Mvc;

namespace HiveOps.Sandpit.Ui.Controllers;

[ApiController]
[Route("api/sandpit/github-mock")]
public class GithubMockController(ILogger<GithubMockController> logger) : ControllerBase
{
    [HttpGet("repos/{owner}/{repo}/pulls/{prNumber}/files")]
    public IActionResult GetPullRequestDiffs([FromRoute] string owner, [FromRoute] string repo, [FromRoute] int prNumber)
    {
        logger.LogWarning("[SANDBOX] Serving intentionally malformed diff for PR #{PrNumber}", prNumber);

        var corruptedDiff = "@@ -1,5 +1,5 @@\n" +
            " public class OrderService\n" +
            " {\n" +
            "-    public decimal CalculateTotal(Order order)\n" +
            "+    public decimal? CalculateTotal(Order? order)\n" +
            "     {\n" +
            "         // BUG: missing null check\n" +
            "         return order.Items.Sum(i => i.Price * i.Quantity);\n" +
            "     }\n" +
            " }\n" +
            "+// INCOMPLETE: method body missing\n" +
            "+public void ProcessPayment() {\n" +
            "+\n" +
            "+}\n";

        return Ok(new
        {
            fault = "malformed-diff",
            filename = "OrderService.cs",
            diff = corruptedDiff,
            message = "Diff introduces null-unsafe parameter change and incomplete method signature"
        });
    }

    [HttpGet("repos/{owner}/{repo}/commits/{sha}")]
    public IActionResult GetBrokenCommit([FromRoute] string owner, [FromRoute] string repo, [FromRoute] string sha)
    {
        logger.LogWarning("[SANDBOX] Serving broken JSON commit for {Sha}", sha);

        var brokenJson = "{ \"sha\": \"" + sha + "\", \"message\": \"WIP\", \"author\": { \"name\": null, \"email\": \"broken\", \"date\": \"not-a-date\" }, \"stats\": { \"additions\": -5, \"deletions\": \"many\" }";

        return Ok(new
        {
            fault = "broken-json",
            rawJson = brokenJson,
            message = "Commit JSON is intentionally broken: missing closing braces, negative additions count, invalid date format"
        });
    }

    [HttpGet("repos/{owner}/{repo}/contents/{*path}")]
    public IActionResult GetBuggyCode([FromRoute] string owner, [FromRoute] string repo, [FromRoute] string path)
    {
        logger.LogWarning("[SANDBOX] Serving buggy code file {Path}", path);

        string buggyCode;
        if (path.Contains("OrderService", StringComparison.OrdinalIgnoreCase))
        {
            buggyCode = @"
public class OrderService
{
    // CWE-369: Divide By Zero
    public decimal CalculateDiscount(decimal total, int percentage)
    {
        return total / (100 - percentage); // Crash when percentage == 100
    }
}";
        }
        else if (path.Contains("UserService", StringComparison.OrdinalIgnoreCase))
        {
            buggyCode = @"
public class UserService
{
    // CWE-89: SQL Injection
    public List<User> SearchUsers(string keyword)
    {
        var query = ""SELECT * FROM Users WHERE Name LIKE '%"" + keyword + ""%'"";
        return db.ExecuteSqlRaw(query); // Direct string concatenation into SQL
    }
}";
        }
        else
        {
            buggyCode = @"
public class GenericService
{
    // CWE-674: Uncontrolled Recursion
    public int Fibonacci(int n)
    {
        return Fibonacci(n - 1) + Fibonacci(n - 2); // No base case -> StackOverflow
    }
}";
        }

        return Ok(new
        {
            fault = "buggy-code",
            filename = path,
            content = buggyCode,
            message = "Code contains known vulnerability patterns (CWE-369, CWE-89, CWE-674)"
        });
    }

    [HttpGet("repos/{owner}/{repo}/pulls/{prNumber}/merge")]
    public IActionResult SimulateMergeConflict([FromRoute] string owner, [FromRoute] string repo, [FromRoute] int prNumber)
    {
        logger.LogWarning("[SANDBOX] Simulating merge conflict for PR #{PrNumber}", prNumber);

        return StatusCode(409, new
        {
            fault = "merge-conflict",
            message = "Merge conflict detected in 3 files",
            files = new[]
            {
                new { path = "src/Services/OrderService.cs", conflictMarkers = 2, baseBranch = "feature/payment-refactor", head = "main" },
                new { path = "src/Controllers/HomeController.cs", conflictMarkers = 1, baseBranch = "feature/ui-redesign", head = "main" },
                new { path = "appsettings.json", conflictMarkers = 1, baseBranch = "feature/new-config", head = "main" }
            }
        });
    }

    [HttpPost("trigger-bad-webhook")]
    public IActionResult TriggerBadWebhook()
    {
        logger.LogWarning("[SANDBOX] Sending malformed webhook payload");

        var rawPayload = "{ \"action\": null, \"repository\": { \"id\": 999999, \"name\": null, \"owner\": { \"login\": \"test-org\" } }, \"pull_request\": { \"number\": -1, \"title\": null, \"head\": { \"ref\": null }, \"base\": { \"ref\": \"main\" }, \"merged_by\": } }";

        return Ok(new
        {
            fault = "malformed-webhook",
            rawPayload = rawPayload,
            message = "Webhook JSON is intentionally malformed: 'merged_by' has no value after colon, causing parser failure"
        });
    }
}
