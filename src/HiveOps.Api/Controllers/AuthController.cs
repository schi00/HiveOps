using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Api.Services;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        AppDbContext db,
        IEmailService emailService,
        IConfiguration config,
        ILogger<AuthController> logger)
    {
        _db = db;
        _emailService = emailService;
        _config = config;
        _logger = logger;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Username and password are required.");

        var username = request.Username.Trim();

        var user = await _db.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username && u.IsActive, ct);

        if (user is null || !PasswordSecurity.VerifyPassword(request.Password, user.PasswordHash))
            return Unauthorized("Invalid credentials.");

        if (string.Equals(user.Role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Role, AppRoles.Admin),
                new(AppClaimTypes.UserId, user.Id.ToString()),
                new(AppClaimTypes.Username, user.Username)
            };

            await SignInAsync(claims);
            return Ok(new AuthMeResponse(true, AppRoles.Admin, null, null, user.Username));
        }

        if (user.TenantId is null)
            return Unauthorized("Tenant user is not linked to a tenant.");

        var tenant = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == user.TenantId && t.IsActive)
            .Select(t => new { t.Id, t.Name })
            .FirstOrDefaultAsync(ct);

        if (tenant is null)
            return Unauthorized("Tenant is inactive or unavailable.");

        var tenantClaims = new List<Claim>
        {
            new(ClaimTypes.Name, tenant.Name),
            new(ClaimTypes.Role, AppRoles.Tenant),
            new(AppClaimTypes.TenantId, tenant.Id.ToString()),
            new(AppClaimTypes.TenantName, tenant.Name),
            new(AppClaimTypes.UserId, user.Id.ToString()),
            new(AppClaimTypes.Username, user.Username)
        };

        await SignInAsync(tenantClaims);
        return Ok(new AuthMeResponse(true, AppRoles.Tenant, tenant.Id, tenant.Name, tenant.Name));
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        const string genericMessage = "Si el correo está registrado, recibirás un enlace en tu casilla de entrada.";

        if (request is null || string.IsNullOrWhiteSpace(request.UsernameOrEmail))
            return BadRequest("El correo electrónico es requerido.");

        var key = request.UsernameOrEmail.Trim();

        var user = await _db.AppUsers.FirstOrDefaultAsync(u =>
            u.IsActive && (u.Username == key || u.Email == key), ct);

        // Always return the same message to prevent user enumeration
        if (user is null || string.IsNullOrWhiteSpace(user.Email))
            return Ok(new { accepted = true, message = genericMessage });

        var token = PasswordSecurity.GenerateResetToken();
        user.PasswordResetTokenHash = PasswordSecurity.HashResetToken(token);
        user.PasswordResetTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var baseUrl = _config["Email:BaseUrl"]?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
        var resetLink = $"{baseUrl}/dashboard/?token={Uri.EscapeDataString(token)}";

        try
        {
            await _emailService.SendPasswordResetEmailAsync(user.Email, user.Username, resetLink, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send password reset email to {Email}", user.Email);
            // Still return success to avoid timing attacks; log internally
        }

        return Ok(new { accepted = true, message = genericMessage });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest("Token and new password are required.");

        if (request.NewPassword.Length < 6)
            return BadRequest("New password must be at least 6 characters.");

        var tokenHash = PasswordSecurity.HashResetToken(request.Token.Trim());
        var now = DateTimeOffset.UtcNow;

        var user = await _db.AppUsers.FirstOrDefaultAsync(u =>
            u.IsActive &&
            u.PasswordResetTokenHash == tokenHash &&
            u.PasswordResetTokenExpiresAt != null &&
            u.PasswordResetTokenExpiresAt > now, ct);

        if (user is null)
            return BadRequest("Invalid or expired token.");

        user.PasswordHash = PasswordSecurity.HashPassword(request.NewPassword);
        user.PasswordResetTokenHash = null;
        user.PasswordResetTokenExpiresAt = null;
        user.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return Ok(new { updated = true });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { authenticated = false });
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        if (User?.Identity?.IsAuthenticated != true)
            return Ok(new AuthMeResponse(false, null, null, null, null));

        var role = User.FindFirstValue(ClaimTypes.Role);
        var tenantIdRaw = User.FindFirstValue(AppClaimTypes.TenantId);
        Guid? tenantId = Guid.TryParse(tenantIdRaw, out var parsed) ? parsed : null;
        var tenantName = User.FindFirstValue(AppClaimTypes.TenantName);
        var displayName = User.Identity?.Name;

        return Ok(new AuthMeResponse(true, role, tenantId, tenantName, displayName));
    }

    private async Task SignInAsync(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            });
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed record ForgotPasswordRequest(string UsernameOrEmail);
public sealed record ResetPasswordRequest(string Token, string NewPassword);
public sealed record AuthMeResponse(bool Authenticated, string? Role, Guid? TenantId, string? TenantName, string? DisplayName);
