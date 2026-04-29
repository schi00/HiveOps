using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;

namespace HiveOps.Api.Authentication;

public static class ApiKeyAuthScheme
{
    public const string SchemeName = "ApiKey";
}

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions { }

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly ITenantLookupService _tenantLookup;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ITenantLookupService tenantLookup)
        : base(options, logger, encoder)
    {
        _tenantLookup = tenantLookup;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var apiKeyValues))
            return AuthenticateResult.NoResult();

        var apiKey = apiKeyValues.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
            return AuthenticateResult.NoResult();

        var tenantId = await _tenantLookup.FindByApiKeyAsync(apiKey, Context.RequestAborted);
        if (!tenantId.HasValue)
            return AuthenticateResult.Fail("Invalid API key.");

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, $"apikey:{tenantId.Value}"),
            new Claim(ClaimTypes.Role, AppRoles.Tenant),
            new Claim(AppClaimTypes.TenantId, tenantId.Value.ToString("D"))
        };

        var identity = new ClaimsIdentity(claims, ApiKeyAuthScheme.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthScheme.SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
