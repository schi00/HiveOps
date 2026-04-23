using Microsoft.AspNetCore.Authentication.Cookies;
using FluentValidation;
using Serilog;
using HiveOps.Api.HealthChecks;
using HiveOps.Api.Hubs;
using HiveOps.Api.Middleware;
using HiveOps.Api.Services;
using HiveOps.Api.Validation;
using HiveOps.Agents;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure;
using HiveOps.Workers;

var builder = WebApplication.CreateBuilder(args);

// -- Structured Logging with Serilog ------------------------------------------
builder.Host.UseSerilog((context, configuration) =>
{
    configuration
        .MinimumLevel.Information()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            "logs/HiveOps-.txt",
            rollingInterval: RollingInterval.Day,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
            retainedFileCountLimit: 7)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("ApplicationName", "HiveOps")
        .ReadFrom.Configuration(context.Configuration);
});

// -- Infrastructure services --------------------------------------------------
builder.Services.AddHiveOpsInfrastructure(builder.Configuration);
builder.Services.AddHiveOpsAgents();

// -- ASP.NET Core -------------------------------------------------------------
builder.Services.AddControllers();
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
    });
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

// -- Background Workers --------------------------------------------------------
builder.Services.AddHostedService<AuthBootstrapHostedService>();
builder.Services.AddSingleton<IMessageQueueService, BackgroundMessageQueueService>();
builder.Services.AddHostedService(sp => (BackgroundMessageQueueService)sp.GetRequiredService<IMessageQueueService>());

// -- Validation (FluentValidation) ----------------------------------------------
builder.Services.AddScoped<IValidator<IncomingMessage>, IncomingMessageValidator>();

// -- Resilience (Polly) ---------------------------------------------------------
builder.Services.AddSingleton<ResilienceService>();

// -- Health checks -------------------------------------------------------------
builder.Services
    .AddHealthChecks()
    .AddCheck<SystemHealthCheck>("database", tags: ["startup", "ready", "live"]);

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
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<SupervisionHub>("/hubs/supervision");
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new() { Predicate = (check) => check.Tags.Contains("ready") });
app.MapHealthChecks("/health/live", new() { Predicate = (check) => check.Tags.Contains("live") });

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
