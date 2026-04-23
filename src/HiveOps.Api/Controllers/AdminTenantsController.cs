using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Api.Services;
using HiveOps.Api.Utilities;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/admin/tenants")]
public sealed class AdminTenantsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly TenantContext _tenantContext;
    private readonly IndustrySettingsService _industrySettings;
    private readonly IWhatsAppAccessTokenValidator _whatsAppAccessTokenValidator;

    public AdminTenantsController(
        AppDbContext db,
        IConfiguration configuration,
        TenantContext tenantContext,
        IndustrySettingsService industrySettings,
        IWhatsAppAccessTokenValidator whatsAppAccessTokenValidator)
    {
        _db = db;
        _configuration = configuration;
        _tenantContext = tenantContext;
        _industrySettings = industrySettings;
        _whatsAppAccessTokenValidator = whatsAppAccessTokenValidator;
    }

    [HttpGet]
    public async Task<IActionResult> ListTenants(CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");

        var tenants = await _db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.WhatsAppNumber,
                t.IsActive,
                t.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(tenants);
    }

    [HttpGet("{tenantId:guid}/settings")]
    public async Task<IActionResult> GetSettings(Guid tenantId, CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");

        _tenantContext.SetTenant(tenantId);

        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        var settings = TenantSettingsJson.Parse(tenant.ConfigJson);
        var defaults = _industrySettings.BuildDefaults(tenant.Industry);
        settings = _industrySettings.MergeMissingValues(settings, defaults);

        var businessConfig = await _db.BusinessConfigs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);

        if (businessConfig is not null)
        {
            settings.Phrases.WelcomeMessage = businessConfig.WelcomeMessage ?? settings.Phrases.WelcomeMessage;
            settings.Phrases.FallbackMessage = businessConfig.FallbackMessage ?? settings.Phrases.FallbackMessage;
        }

        return Ok(new
        {
            tenant.Id,
            tenant.Name,
            settings
        });
    }

    [HttpPut("{tenantId:guid}/settings")]
    public async Task<IActionResult> SaveSettings(Guid tenantId, [FromBody] TenantAdminSettings settings, CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");

        _tenantContext.SetTenant(tenantId);

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        tenant.ConfigJson = TenantSettingsJson.Stringify(settings);

        var businessConfig = await _db.BusinessConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);

        if (businessConfig is null)
        {
            businessConfig = new BusinessConfig
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                WelcomeMessage = settings.Phrases.WelcomeMessage,
                FallbackMessage = settings.Phrases.FallbackMessage,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.BusinessConfigs.Add(businessConfig);
        }
        else
        {
            businessConfig.WelcomeMessage = settings.Phrases.WelcomeMessage;
            businessConfig.FallbackMessage = settings.Phrases.FallbackMessage;
            businessConfig.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Settings saved.", tenantId });
    }

    [HttpPost("{tenantId:guid}/sync/run")]
    public async Task<IActionResult> TriggerSync(Guid tenantId, CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");
        return BadRequest("Catalog sync is no longer supported.");
    }

    [HttpPost("{tenantId:guid}/whatsapp/access-token")]
    public async Task<IActionResult> ValidateAndSaveWhatsAppAccessToken(
        Guid tenantId,
        [FromBody] ValidateWhatsAppAccessTokenRequest request,
        CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");
        if (request is null || string.IsNullOrWhiteSpace(request.AccessToken))
            return BadRequest("Access token is required.");

        _tenantContext.SetTenant(tenantId);

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        var validation = await _whatsAppAccessTokenValidator.ValidateAsync(request.AccessToken, ct);
        if (!validation.IsValid)
            return BadRequest(new { valid = false, message = validation.ErrorMessage ?? "Invalid access token." });

        var settings = TenantSettingsJson.Parse(tenant.ConfigJson);
        settings.WhatsApp.ApiKey = request.AccessToken.Trim();
        settings.WhatsApp.ApiKeyUpdatedAtUtc = DateTimeOffset.UtcNow;
        settings.WhatsApp.Provider = string.IsNullOrWhiteSpace(settings.WhatsApp.Provider) ? "MetaCloud" : settings.WhatsApp.Provider;
        settings.WhatsApp.PhoneNumber = string.IsNullOrWhiteSpace(settings.WhatsApp.PhoneNumber)
            ? tenant.WhatsAppNumber ?? string.Empty
            : settings.WhatsApp.PhoneNumber;
        settings.WhatsApp.Enabled = !string.IsNullOrWhiteSpace(settings.WhatsApp.PhoneNumber);

        tenant.ConfigJson = TenantSettingsJson.Stringify(settings);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            valid = true,
            message = "Access token validated and saved.",
            subjectId = validation.SubjectId,
            subjectName = validation.SubjectName,
            updatedAtUtc = settings.WhatsApp.ApiKeyUpdatedAtUtc
        });
    }

    [HttpGet("{tenantId:guid}/users")]
    public async Task<IActionResult> ListUsers(Guid tenantId, CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");

        var tenantExists = await _db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, ct);
        if (!tenantExists) return NotFound();

        var users = await _db.AppUsers
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.Role == AppRoles.Tenant)
            .OrderBy(u => u.Username)
            .Select(u => new TenantUserDto(u.Id, u.Username, u.Email, u.IsActive, u.CreatedAt, u.UpdatedAt))
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPost("{tenantId:guid}/users")]
    public async Task<IActionResult> CreateUser(Guid tenantId, [FromBody] CreateTenantUserRequest request, CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");
        if (request is null) return BadRequest("Payload is required.");
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Username, email and password are required.");
        if (request.Password.Length < 6)
            return BadRequest("Password must be at least 6 characters.");

        var tenantExists = await _db.Tenants.AnyAsync(t => t.Id == tenantId, ct);
        if (!tenantExists) return NotFound();

        var username = request.Username.Trim();
        var email = request.Email.Trim();

        var duplicate = await _db.AppUsers
            .AsNoTracking()
            .AnyAsync(u => u.Username == username || u.Email == email, ct);
        if (duplicate)
            return Conflict("Username or email already exists.");

        var user = new AppUser
        {
            Username = username,
            Email = email,
            PasswordHash = PasswordSecurity.HashPassword(request.Password),
            Role = AppRoles.Tenant,
            TenantId = tenantId,
            IsActive = request.IsActive
        };

        _db.AppUsers.Add(user);
        await _db.SaveChangesAsync(ct);

        return Ok(new TenantUserDto(user.Id, user.Username, user.Email, user.IsActive, user.CreatedAt, user.UpdatedAt));
    }

    [HttpPut("{tenantId:guid}/users/{userId:guid}")]
    public async Task<IActionResult> UpdateUser(Guid tenantId, Guid userId, [FromBody] UpdateTenantUserRequest request, CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");
        if (request is null) return BadRequest("Payload is required.");

        var user = await _db.AppUsers
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId && u.Role == AppRoles.Tenant, ct);
        if (user is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(request.Username))
            user.Username = request.Username.Trim();
        if (!string.IsNullOrWhiteSpace(request.Email))
            user.Email = request.Email.Trim();
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            if (request.Password.Length < 6)
                return BadRequest("Password must be at least 6 characters.");
            user.PasswordHash = PasswordSecurity.HashPassword(request.Password);
        }

        user.IsActive = request.IsActive;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(new TenantUserDto(user.Id, user.Username, user.Email, user.IsActive, user.CreatedAt, user.UpdatedAt));
    }

    [HttpDelete("{tenantId:guid}/users/{userId:guid}")]
    public async Task<IActionResult> DeleteUser(Guid tenantId, Guid userId, CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");

        var user = await _db.AppUsers
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId && u.Role == AppRoles.Tenant, ct);
        if (user is null) return NotFound();

        _db.AppUsers.Remove(user);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("metrics/bot")]
    public async Task<IActionResult> GetBotMetrics([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");

        var conversations = _db.Conversations.IgnoreQueryFilters().AsNoTracking();
        var messages = _db.ConversationMessages.IgnoreQueryFilters().AsNoTracking();

        if (tenantId.HasValue)
        {
            conversations = conversations.Where(c => c.TenantId == tenantId.Value);
            messages = messages.Where(m => m.TenantId == tenantId.Value);
        }

        var active = await conversations.CountAsync(c => c.Status == HiveOps.Domain.Enums.ConversationStatus.Active, ct);
        var awaitingHuman = await conversations.CountAsync(c => c.Status == HiveOps.Domain.Enums.ConversationStatus.AwaitingHuman, ct);

        var fallbackCount = await messages.CountAsync(m =>
            m.Role == HiveOps.Domain.Enums.MessageRole.Assistant
            && (m.Content.Contains("No entend") || m.Content.Contains("No entendí") || m.Content.Contains("reformular")), ct);

        var last30Days = DateTimeOffset.UtcNow.AddDays(-30);
        var responsePairs = await messages
            .Where(m => m.CreatedAt >= last30Days)
            .OrderBy(m => m.ConversationId)
            .ThenBy(m => m.CreatedAt)
            .Select(m => new { m.ConversationId, m.Role, m.CreatedAt })
            .ToListAsync(ct);

        double avgResponseSeconds = 0;
        var responseSamples = 0;
        var perDay = new Dictionary<string, double>(StringComparer.Ordinal);

        var groupedByConv = responsePairs.GroupBy(x => x.ConversationId);
        foreach (var conv in groupedByConv)
        {
            DateTimeOffset? lastUserMessage = null;
            foreach (var item in conv)
            {
                if (item.Role == HiveOps.Domain.Enums.MessageRole.User)
                {
                    lastUserMessage = item.CreatedAt;
                    continue;
                }

                if (item.Role == HiveOps.Domain.Enums.MessageRole.Assistant && lastUserMessage.HasValue)
                {
                    var seconds = Math.Max(0, (item.CreatedAt - lastUserMessage.Value).TotalSeconds);
                    avgResponseSeconds += seconds;
                    responseSamples++;
                    var day = item.CreatedAt.Date.ToString("yyyy-MM-dd");
                    perDay[day] = perDay.TryGetValue(day, out var current) ? current + 1 : 1;
                    lastUserMessage = null;
                }
            }
        }

        avgResponseSeconds = responseSamples == 0 ? 0 : avgResponseSeconds / responseSamples;

        var fallbackPerDayRaw = await messages
            .Where(m => m.CreatedAt >= last30Days && m.Role == HiveOps.Domain.Enums.MessageRole.Assistant)
            .Where(m => m.Content.Contains("No entend") || m.Content.Contains("No entendí") || m.Content.Contains("reformular"))
            .GroupBy(m => m.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var fallbackPerDay = fallbackPerDayRaw
            .Select(x => new MetricPointDto(x.Date.ToString("yyyy-MM-dd"), x.Count))
            .ToList();

        var responsesPerDay = perDay
            .OrderBy(k => k.Key)
            .Select(k => new MetricPointDto(k.Key, k.Value))
            .ToList();

        return Ok(new AdminBotMetricsDto(active, awaitingHuman, fallbackCount, avgResponseSeconds, responsesPerDay, fallbackPerDay));
    }

    [HttpGet("{tenantId:guid}/catalog/attributes")]
    public async Task<IActionResult> GetCatalogAttributes(Guid tenantId, CancellationToken ct)
    {
        if (!IsAdminRequest()) return Unauthorized("Admin credentials required.");

        _tenantContext.SetTenant(tenantId);

        var tenantExists = await _db.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId, ct);

        if (!tenantExists)
            return NotFound();

        return BadRequest("Catalog management is no longer supported.");
    }

    private bool IsAdminRequest()
    {
        if (User.Identity?.IsAuthenticated == true && User.IsInRole(AppRoles.Admin))
            return true;

        var configured = _configuration["Admin:ApiKey"];
        if (string.IsNullOrWhiteSpace(configured)) return false;

        if (!Request.Headers.TryGetValue("X-Admin-Key", out var key)) return false;
        return string.Equals(configured, key.ToString(), StringComparison.Ordinal);
    }
}

public sealed record ValidateWhatsAppAccessTokenRequest(string AccessToken);

public sealed record TenantUserDto(
    Guid Id,
    string Username,
    string Email,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateTenantUserRequest(string Username, string Email, string Password, bool IsActive = true);
public sealed record UpdateTenantUserRequest(string? Username, string? Email, string? Password, bool IsActive);

public sealed record MetricPointDto(string Label, double Value);

public sealed record AdminBotMetricsDto(
    int ActiveConversations,
    int AwaitingHumanConversations,
    int FallbackCount,
    double AvgResponseSeconds,
    IReadOnlyList<MetricPointDto> ResponsesPerDay,
    IReadOnlyList<MetricPointDto> FallbacksPerDay);
