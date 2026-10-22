using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using FluentValidation;
using Serilog;
using HiveOps.Api.HealthChecks;
using HiveOps.Api.Hubs;
using HiveOps.Api.Logging;
using HiveOps.Api.Middleware;
using HiveOps.Api.Services;
using HiveOps.Api.Validation;
using HiveOps.Application.Configuration;
using HiveOps.Agents;
using HiveOps.Agents.Support;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Services;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure;
using HiveOps.Infrastructure.ApiDrift;
using HiveOps.Infrastructure.Services;
using HiveOps.Workers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// -- Structured Logging with Serilog ------------------------------------------
builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .MinimumLevel.Information()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [Tenant:{TenantId}] [Corr:{CorrelationId}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            "logs/HiveOps-.txt",
            rollingInterval: RollingInterval.Day,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [Tenant:{TenantId}] [Corr:{CorrelationId}] {Message:lj}{NewLine}{Exception}",
            retainedFileCountLimit: 7)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("ApplicationName", "HiveOps")
        .Enrich.With(services.GetRequiredService<TenantIdEnricher>())
        .Enrich.With(services.GetRequiredService<CorrelationIdEnricher>())
        .ReadFrom.Configuration(context.Configuration);
});

// -- Rate Limiting -------------------------------------------------------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("onboarding-policy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? httpContext.Request.Headers.Host.ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }
        )
    );
});

// -- HttpContext accessor for enrichers -----------------------------------------
builder.Services.AddHttpContextAccessor();

// -- Serilog enrichers --------------------------------------------------------
builder.Services.AddSingleton<TenantIdEnricher>();
builder.Services.AddSingleton<CorrelationIdEnricher>();

// -- Infrastructure services --------------------------------------------------
builder.Services.AddHiveOpsInfrastructure(builder.Configuration);
builder.Services.AddHiveOpsAgents();

// -- ASP.NET Core -------------------------------------------------------------
builder.Services.AddControllers();

var deploymentForJwt = builder.Configuration.GetSection(HiveOpsDeploymentOptions.SectionPath).Get<HiveOpsDeploymentOptions>()
    ?? new HiveOpsDeploymentOptions();
var jwtFromConfig = builder.Configuration["Jwt:Key"];
string jwtKey;
if (string.IsNullOrWhiteSpace(jwtFromConfig))
{
    if (deploymentForJwt.Mode == HiveOpsDeploymentMode.SelfHosted)
        throw new InvalidOperationException("Jwt:Key is required when HiveOps:Deployment:Mode is SelfHosted.");
    if (builder.Environment.IsDevelopment()
        || (deploymentForJwt.Mode == HiveOpsDeploymentMode.Ephemeral && deploymentForJwt.AllowInsecureJwtForTests))
        jwtKey = HiveOpsSecurityDefaults.InsecureDevelopmentJwtKey;
    else
        throw new InvalidOperationException("Jwt:Key must be set, or run in Development with a supported deployment profile.");
}
else
{
    jwtKey = jwtFromConfig;
    if (jwtKey == HiveOpsSecurityDefaults.InsecureDevelopmentJwtKey)
    {
        if (deploymentForJwt.Mode == HiveOpsDeploymentMode.SelfHosted)
            throw new InvalidOperationException("Jwt:Key must not use the insecure development default when Mode is SelfHosted.");
        if (!builder.Environment.IsDevelopment()
            && !(deploymentForJwt.Mode == HiveOpsDeploymentMode.Ephemeral && deploymentForJwt.AllowInsecureJwtForTests))
            throw new InvalidOperationException("Jwt:Key must not use the insecure development default outside Development or unsecured Ephemeral test runs.");
    }
}

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "HiveOps";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "HiveOpsUsers";

// HIVE shared identity: the suite authority (hivecrew-api / accounts.hivecorp.com.ar)
// issues tokens with org_id + product claims. Accept them alongside HiveOps-issued
// tokens; the org claim is mapped to tenant_id and the organization is
// auto-provisioned on first use (see OnTokenValidated below).
var hiveIdentityKey = builder.Configuration["HiveIdentity:Key"];
var hiveIdentityIssuer = builder.Configuration["HiveIdentity:Issuer"] ?? "https://accounts.hivecorp.com.ar";
var hiveIdentityAudience = builder.Configuration["HiveIdentity:Audience"] ?? "hive-products";
var hiveIdentityEnabled = !string.IsNullOrWhiteSpace(hiveIdentityKey);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "HiveOps.auth";
        options.LoginPath = "/dashboard/";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddJwtBearer(options =>
    {
        var issuers = new List<string> { jwtIssuer };
        var audiences = new List<string> { jwtAudience };
        var signingKeys = new List<SecurityKey> { new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)) };
        if (hiveIdentityEnabled)
        {
            issuers.Add(hiveIdentityIssuer);
            audiences.Add(hiveIdentityAudience);
            signingKeys.Add(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(hiveIdentityKey)));
        }

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuers = issuers,
            ValidAudiences = audiences,
            IssuerSigningKeys = signingKeys
        };

        if (hiveIdentityEnabled)
        {
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    if (context.Principal?.Identity is not System.Security.Claims.ClaimsIdentity identity
                        || !identity.IsAuthenticated)
                        return;

                    // Only HIVE-account tokens need mapping: they carry "org_id"
                    // instead of the HiveOps-native "tenant_id".
                    if (identity.HasClaim(c => c.Type == HiveOps.Api.Authentication.AppClaimTypes.TenantId))
                        return;

                    var orgIdClaim = identity.FindFirst("org_id")?.Value;
                    if (!Guid.TryParse(orgIdClaim, out var orgId))
                    {
                        context.Fail("HIVE token is missing a valid org_id claim.");
                        return;
                    }

                    // Product gate: tokens only entitle the products listed in
                    // their "product" claims — require "ops" to enter HiveOps.
                    var products = identity.FindAll("product").Select(c => c.Value).ToList();
                    if (products.Count > 0 && !products.Contains("ops", StringComparer.OrdinalIgnoreCase))
                    {
                        context.Fail("HIVE token does not include a HiveOps entitlement.");
                        return;
                    }

                    var orgName = identity.FindFirst("org_name")?.Value;
                    var db = context.HttpContext.RequestServices
                        .GetRequiredService<HiveOps.Infrastructure.Persistence.AppDbContext>();

                    var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == orgId,
                        context.HttpContext.RequestAborted);
                    if (tenant is null)
                    {
                        // JIT provisioning: first HIVE sign-in materializes the
                        // organization here with the same id as the account org.
                        tenant = new HiveOps.Domain.Entities.Tenant
                        {
                            Id = orgId,
                            Name = string.IsNullOrWhiteSpace(orgName) ? $"org-{orgId:N}" : orgName,
                            ApiKey = $"tenant-{Guid.NewGuid():N}",
                            IsActive = true,
                            Plan = HiveOps.Domain.Entities.PlanTier.Starter
                        };
                        db.Tenants.Add(tenant);
                        await db.SaveChangesAsync(context.HttpContext.RequestAborted);
                    }
                    else if (!tenant.IsActive)
                    {
                        context.Fail("Organization is inactive.");
                        return;
                    }

                    identity.AddClaim(new System.Security.Claims.Claim(
                        HiveOps.Api.Authentication.AppClaimTypes.TenantId, tenant.Id.ToString("D")));
                    identity.AddClaim(new System.Security.Claims.Claim(
                        HiveOps.Api.Authentication.AppClaimTypes.TenantName, tenant.Name));
                }
            };
        }
    })
    .AddScheme<HiveOps.Api.Authentication.ApiKeyAuthenticationOptions, HiveOps.Api.Authentication.ApiKeyAuthenticationHandler>(
        HiveOps.Api.Authentication.ApiKeyAuthScheme.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "HiveOps API", Version = "v1" });
    c.AddSecurityDefinition("ApiKey", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Name = "X-Api-Key",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Tenant API key"
    });
});

// -- SignalR -------------------------------------------------------------------
builder.Services.AddSignalR();
builder.Services.AddScoped<SupervisionNotificationService>();
builder.Services.AddScoped<ISupervisionNotifier>(sp => sp.GetRequiredService<SupervisionNotificationService>());
builder.Services.AddScoped<IndustrySettingsService>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
builder.Services.AddHttpClient<IWhatsAppAccessTokenValidator, MetaWhatsAppAccessTokenValidator>();
builder.Services.AddScoped<OutboundWebhookService>();
builder.Services.AddScoped<IOutboundWebhookService>(sp => sp.GetRequiredService<OutboundWebhookService>());
builder.Services.AddHttpClient("OutboundWebhook")
    .AddHttpMessageHandler<ApiContractObserverHandler>();

// -- API Drift Detection - Register handler for named HttpClients
builder.Services.AddScoped<ApiContractObserverHandler>();
builder.Services.AddHttpClient("ApiDriftDetection")
    .AddHttpMessageHandler<ApiContractObserverHandler>();

// -- HiveArena (optional proving-ground callbacks / publisher config) ------------
builder.Services.Configure<HiveOpsHiveArenaInboundOptions>(builder.Configuration.GetSection(HiveOpsHiveArenaInboundOptions.SectionName));

// -- Incident Auto Workflow Services --------------------------------------------
builder.Services.Configure<IncidentAutoWorkflowOptions>(builder.Configuration.GetSection(IncidentAutoWorkflowOptions.SectionName));
builder.Services.Configure<DatabaseBackupOptions>(builder.Configuration.GetSection(DatabaseBackupOptions.SectionName));
builder.Services.AddSingleton<DatabaseBackupService>();
builder.Services.AddSingleton<IIncidentMessageHistoryService, IncidentMessageHistoryService>();
builder.Services.AddSingleton<IApprovalWorkflowService, ApprovalWorkflowService>();
builder.Services.AddSingleton<IncidentEmailService>();
builder.Services.AddSingleton<KbArticleGenerator>();

// -- Background Workers --------------------------------------------------------
builder.Services.AddHostedService<AuthBootstrapHostedService>();
builder.Services.AddHostedService<TenantCacheWarmerHostedService>();
builder.Services.AddHostedService<IncidentAutoWorkflowService>();
builder.Services.AddSingleton<IMessageQueueService, BackgroundMessageQueueService>();
builder.Services.AddHostedService(sp => (BackgroundMessageQueueService)sp.GetRequiredService<IMessageQueueService>());

// -- Validation (FluentValidation) ----------------------------------------------
builder.Services.AddScoped<IValidator<IncomingMessage>, IncomingMessageValidator>();

// -- Resilience (Polly) ---------------------------------------------------------
builder.Services.AddSingleton<ResilienceService>();

// -- Health checks -------------------------------------------------------------
builder.Services
    .AddHealthChecks()
    .AddCheck<SystemHealthCheck>("database", tags: ["startup", "ready", "live"])
    .AddCheck<TenantDatabaseHealthCheck>("tenant_database", tags: ["tenant"]);

var app = builder.Build();

// -- Request pipeline ----------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

// -- Middleware stack ----------------------------------------------------------
app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
app.UseMiddleware<ValidationMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();
app.UseSerilogRequestLogging();

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    var isWebhook = context.Request.Path.StartsWithSegments("/webhook", StringComparison.OrdinalIgnoreCase) ||
                    context.Request.Path.StartsWithSegments("/webhooks", StringComparison.OrdinalIgnoreCase);

    if (!isWebhook)
    {
        await next();
        return;
    }

    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebhookTraffic");
    logger.LogInformation("Webhook inbound {Method} {Path}", context.Request.Method, context.Request.Path);
    await next();
    logger.LogInformation("Webhook outbound {StatusCode} {Method} {Path}", context.Response.StatusCode, context.Request.Method, context.Request.Path);
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<TenantLogContextMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<SupervisionHub>("/hubs/supervision");
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new() { Predicate = (check) => check.Tags.Contains("ready") });
app.MapHealthChecks("/health/live", new() { Predicate = (check) => check.Tags.Contains("live") });

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
