using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Application.Interfaces;
using Microsoft.AspNetCore.RateLimiting;

namespace HiveOps.Api.Controllers;

[EnableRateLimiting("onboarding-policy")]
[ApiController]
[Route("api/onboarding")]
public sealed class OnboardingController : ControllerBase
{
    private readonly IDataProtector _protector;
    private readonly AppDbContext _db;
    private readonly IStripeService _stripe;
    private readonly ILogger<OnboardingController> _logger;

    public OnboardingController(IDataProtectionProvider dataProtectionProvider, AppDbContext db, IStripeService stripe, ILogger<OnboardingController> logger)
    {
        _protector = dataProtectionProvider.CreateProtector("onboarding-token");
        _db = db;
        _stripe = stripe;
        _logger = logger;
    }

    public sealed record StartRequest(string CompanyName, string AdminName, string Email, string Password, string? Subdomain);
    public sealed record StartResponse(string Token, DateTimeOffset ExpiresAtUtc);
    public sealed record VerifyRequest(string Token);
    public sealed record VerifyResponse(bool Verified, Guid TenantId, Guid AdminUserId, string CompanyName, string AdminName, string Email, string? Subdomain);
    public sealed record PlanRequest(Guid TenantId, string PriceId);
    public sealed record PlanResponse(Guid TenantId, string PriceId, string? PortalUrl);

    [HttpPost("start")]
    public ActionResult<StartResponse> Start([FromBody] StartRequest req)
    {
        var payload = new
        {
            company = req.CompanyName,
            admin = req.AdminName,
            email = req.Email,
            password = req.Password,
            sub = req.Subdomain,
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var json = JsonSerializer.Serialize(payload);
        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(json));
        var token = WebEncoders.Base64UrlEncode(protectedBytes);
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);

        return Accepted(new StartResponse(token, expires));
    }

    [HttpPost("verify")]
    public async Task<ActionResult<VerifyResponse>> Verify([FromBody] VerifyRequest req, CancellationToken ct)
    {
        byte[] unprotected;
        try
        {
            var raw = WebEncoders.Base64UrlDecode(req.Token);
            unprotected = _protector.Unprotect(raw);
        }
        catch
        {
            return BadRequest();
        }

        var json = Encoding.UTF8.GetString(unprotected);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var iat = root.GetProperty("iat").GetInt64();
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iat);
        if (DateTimeOffset.UtcNow - issuedAt > TimeSpan.FromMinutes(30))
        {
            return Unauthorized();
        }

        var company = root.GetProperty("company").GetString() ?? string.Empty;
        var admin = root.GetProperty("admin").GetString() ?? string.Empty;
        var email = root.GetProperty("email").GetString() ?? string.Empty;
        var password = root.GetProperty("password").GetString() ?? string.Empty;
        var sub = root.TryGetProperty("sub", out var sv) ? sv.GetString() : null;

        if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(admin) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return BadRequest("Invalid onboarding payload.");

        _logger.LogInformation("[Onboarding] Verify requested for {Email} from {IP}", email, HttpContext.Connection.RemoteIpAddress?.ToString());

        var username = $"tenant_{Slug(company)}";

        // Prevent duplicates (company, username, email)
        if (await _db.Tenants.AsNoTracking().AnyAsync(t => t.Name == company, ct))
            return Conflict("A tenant with this company name already exists.");
        if (await _db.AppUsers.AsNoTracking().AnyAsync(u => u.Username == username || u.Email == email, ct))
            return Conflict("Username or email already exists.");

        var tenant = new Tenant
        {
            Name = company,
            IsActive = false,
            ApiKey = GenerateApiKey(),
            Plan = PlanTier.Starter,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        var user = new AppUser
        {
            Username = username,
            Email = email.Trim(),
            PasswordHash = PasswordSecurity.HashPassword(password),
            Role = AppRoles.Tenant,
            TenantId = tenant.Id,
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.AppUsers.Add(user);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/admin/tenants/{tenant.Id}", new VerifyResponse(true, tenant.Id, user.Id, company, admin, email, sub));
    }

    [HttpPost("plan")]
    public async Task<ActionResult<PlanResponse>> SelectPlan([FromBody] PlanRequest req, CancellationToken ct)
    {
        if (req.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(req.PriceId))
            return BadRequest("TenantId and PriceId are required.");

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == req.TenantId, ct);
        if (tenant is null)
            return NotFound("Tenant not found.");

        await _stripe.EnsureCustomerAsync(tenant.Id, tenant.Name, ct);
        await _stripe.EnsureSubscriptionAsync(tenant.Id, req.PriceId, ct);

        tenant.StripePriceId = req.PriceId;
        tenant.SubscriptionStatus = tenant.SubscriptionStatus ?? "active";
        tenant.IsActive = true;
        await _db.SaveChangesAsync(ct);

        string? portalUrl = null;
        try
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var returnUrl = baseUrl + "/dashboard/";
            portalUrl = await _stripe.CreatePortalSessionAsync(tenant.Id, returnUrl, ct);
        }
        catch
        {
            // ignore; service may return null when STRIPE_SDK is not compiled
        }

        return Ok(new PlanResponse(tenant.Id, req.PriceId, portalUrl));
    }

    private static string GenerateApiKey()
    {
        // 32 bytes -> 64 hex chars
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray();
        return new string(chars).Replace(' ', '_');
    }
}
