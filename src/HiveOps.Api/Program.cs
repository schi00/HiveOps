using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
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
builder.Services.AddHttpClient("OutboundWebhook");

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
