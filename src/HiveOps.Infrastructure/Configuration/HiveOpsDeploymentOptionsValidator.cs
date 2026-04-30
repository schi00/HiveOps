using HiveOps.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HiveOps.Infrastructure.Configuration;

public sealed class HiveOpsDeploymentOptionsValidator : IValidateOptions<HiveOpsDeploymentOptions>
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public HiveOpsDeploymentOptionsValidator(IConfiguration configuration, IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, HiveOpsDeploymentOptions options)
    {
        if (options.Mode == HiveOpsDeploymentMode.Ephemeral && options.Billing != HiveOpsBillingMode.None)
            return ValidateOptionsResult.Fail("HiveOps:Deployment:Billing must be None when Mode is Ephemeral.");

        if (options.FailClosedTenantConnectionInSelfHosted &&
            options.Mode != HiveOpsDeploymentMode.SelfHosted)
            return ValidateOptionsResult.Fail("HiveOps:Deployment:FailClosedTenantConnectionInSelfHosted is only valid when Mode is SelfHosted.");

        var adminKey = _configuration["Admin:ApiKey"];
        if (options.Mode == HiveOpsDeploymentMode.SelfHosted &&
            string.IsNullOrWhiteSpace(adminKey))
            return ValidateOptionsResult.Fail("Admin:ApiKey is required when HiveOps:Deployment:Mode is SelfHosted.");

        if (options.Mode == HiveOpsDeploymentMode.Ephemeral &&
            string.IsNullOrWhiteSpace(adminKey) &&
            !options.AllowEmptyAdminApiKeyInEphemeral)
            return ValidateOptionsResult.Fail("Admin:ApiKey is required for Ephemeral unless HiveOps:Deployment:AllowEmptyAdminApiKeyInEphemeral is true.");

        if (_environment.IsProduction() &&
            options.Mode == HiveOpsDeploymentMode.SaaS &&
            string.IsNullOrWhiteSpace(adminKey))
            return ValidateOptionsResult.Fail("Admin:ApiKey is required for SaaS in Production.");

        var jwtKey = _configuration["Jwt:Key"];

        if (options.Mode == HiveOpsDeploymentMode.SelfHosted)
        {
            if (string.IsNullOrWhiteSpace(jwtKey))
                return ValidateOptionsResult.Fail("Jwt:Key is required when HiveOps:Deployment:Mode is SelfHosted.");
            if (jwtKey == HiveOpsSecurityDefaults.InsecureDevelopmentJwtKey)
                return ValidateOptionsResult.Fail("Jwt:Key must not use the insecure development default when Mode is SelfHosted.");
            if (jwtKey.Length < 32)
                return ValidateOptionsResult.Fail("Jwt:Key must be at least 32 characters when Mode is SelfHosted.");
        }

        if (options.Mode == HiveOpsDeploymentMode.Ephemeral && !options.AllowInsecureJwtForTests)
        {
            if (string.IsNullOrWhiteSpace(jwtKey))
                return ValidateOptionsResult.Fail("Jwt:Key is required for Ephemeral unless AllowInsecureJwtForTests is true.");
            if (jwtKey == HiveOpsSecurityDefaults.InsecureDevelopmentJwtKey)
                return ValidateOptionsResult.Fail("Jwt:Key must not use the insecure development default for Ephemeral unless AllowInsecureJwtForTests is true.");
            if (jwtKey.Length < 32)
                return ValidateOptionsResult.Fail("Jwt:Key must be at least 32 characters for Ephemeral unless AllowInsecureJwtForTests is true.");
        }

        if (_environment.IsProduction() && options.Mode == HiveOpsDeploymentMode.SaaS)
        {
            if (string.IsNullOrWhiteSpace(jwtKey))
                return ValidateOptionsResult.Fail("Jwt:Key is required for SaaS in Production.");
            if (jwtKey == HiveOpsSecurityDefaults.InsecureDevelopmentJwtKey)
                return ValidateOptionsResult.Fail("Jwt:Key must not use the insecure development default in Production.");
            if (jwtKey.Length < 32)
                return ValidateOptionsResult.Fail("Jwt:Key must be at least 32 characters in Production.");
        }

        if (options.Billing == HiveOpsBillingMode.Stripe)
        {
            var stripeKey = _configuration["Stripe:ApiKey"];
            if (string.IsNullOrWhiteSpace(stripeKey))
                return ValidateOptionsResult.Fail("Stripe:ApiKey is required when HiveOps:Deployment:Billing is Stripe.");
        }

        return ValidateOptionsResult.Success;
    }
}
