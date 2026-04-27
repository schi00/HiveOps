using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HiveOps.Api.Authentication;
using HiveOps.Api.Services;
using HiveOps.Application.Models;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.IntegrationTests;

public sealed class DashboardWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"HiveOps_Integration_{Guid.NewGuid():N}";
    public FakeEmailService FakeEmail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Git:RepoPath"] = Path.GetTempPath(),
                ["Admin:ApiKey"] = "test-admin-api-key-1234567890-abcdef",
                ["HiveOps:DataProtectionKeysPath"] = Path.Combine(Path.GetTempPath(), "hiveops-dp-keys")
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove ALL DbContext-related registrations to avoid provider conflicts
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                         || d.ServiceType == typeof(AppDbContext)
                         || (d.ServiceType.FullName?.Contains("DbContextOptions") == true))
                .ToList();
            foreach (var d in dbDescriptors) services.Remove(d);
            services.RemoveAll(typeof(IEmailService));
            services.RemoveAll(typeof(IWhatsAppAccessTokenValidator));
            services.RemoveAll(typeof(IMessagingChannel));
            services.RemoveAll(typeof(StackExchange.Redis.IConnectionMultiplexer));
            services.RemoveAll(typeof(IConversationStateManager));
            services.AddSingleton<IEmailService>(FakeEmail);
            services.AddSingleton<IWhatsAppAccessTokenValidator, FakeWhatsAppAccessTokenValidator>();
            services.AddSingleton<IMessagingChannel, FakeMessagingChannel>();
            services.AddSingleton<IConversationStateManager, InMemoryConversationStateManager>();
            services.RemoveAll(typeof(IGitService));
            services.AddScoped<IGitService, FakeGitService>();
            services.RemoveAll(typeof(ITenantLookupService));
            services.AddScoped<ITenantLookupService, FakeTenantLookupService>();
            services.RemoveAll(typeof(IDynamicConnectionStringResolver));
            services.AddSingleton<IDynamicConnectionStringResolver, FakeDynamicConnectionStringResolver>();

            // Use InMemory for tests to avoid parallel schema creation conflicts
            services.AddDbContext<AppDbContext>((sp, options) =>
            {
                options.UseInMemoryDatabase(_databaseName);
                var saveChangesInterceptor = sp.GetRequiredService<HiveOps.Infrastructure.Persistence.TenantSaveChangesInterceptor>();
                options.AddInterceptors(saveChangesInterceptor);
            });

            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            Seed(db);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            var tenantContext = new TenantContext();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(_databaseName)
                .Options;

            using var db = new AppDbContext(options, tenantContext);
            db.Database.EnsureDeleted();
        }
    }

    private static void Seed(AppDbContext db)
    {
        if (db.Tenants.Any())
            return;

        var alphaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var betaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var alphaConversationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");
        var betaConversationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1");
        var alphaInteractiveConversationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2");
        var alphaSettings = new TenantAdminSettings
        {
            BotBehavior = new BotBehaviorSettings
            {
                AssistantName = "Vadi",
                Tone = "Claro",
                ResponseLanguage = "es-AR",
                PreferDeterministicRouting = true
            },
            Phrases = new PhraseSettings
            {
                WelcomeMessage = "Hola, soy Vadi.",
                FallbackMessage = "No entendí del todo tu consulta. ¿Podrías reformularla?",
                HumanHandoffMessage = "Tu consulta está siendo atendida por nuestro equipo. Te responderemos en breve."
            },
            WhatsApp = new WhatsAppChannelSettings
            {
                Enabled = true,
                EnableInteractiveButtons = true,
                EnableInteractiveMenu = true,
                PhoneNumber = "+5491100000001",
                PhoneNumberId = "PHONE_NUMBER_ID_ALPHA",
                ApiKey = "VALID_META_TOKEN"
            }
        };
        var betaSettings = new TenantAdminSettings
        {
            BotBehavior = new BotBehaviorSettings
            {
                AssistantName = "Moda Bot",
                Tone = "Cercano",
                ResponseLanguage = "es-AR",
                PreferDeterministicRouting = true
            },
            Phrases = new PhraseSettings
            {
                WelcomeMessage = "Hola, soy Moda Bot.",
                FallbackMessage = "Contame qué prenda o estilo buscás y te ayudo.",
                HumanHandoffMessage = "Te conecto con un asesor de la tienda."
            }
        };

        db.Tenants.AddRange(
            new Tenant
            {
                Id = alphaId,
                Name = "Alpha",
                Industry = "deportes",
                ApiKey = "TENANT-ALFA-KEY",
                WhatsAppNumber = "+5491100000001",
                IsActive = true,
                ConfigJson = System.Text.Json.JsonSerializer.Serialize(alphaSettings)
            },
            new Tenant
            {
                Id = betaId,
                Name = "Beta",
                Industry = "moda",
                ApiKey = "TENANT-BETA-KEY",
                WhatsAppNumber = "+5491100000002",
                IsActive = true,
                ConfigJson = System.Text.Json.JsonSerializer.Serialize(betaSettings)
            });

        db.AppUsers.AddRange(
            new AppUser
            {
                Username = "admin00",
                Email = "admin00@HiveOps.local",
                PasswordHash = PasswordSecurity.HashPassword("123456"),
                Role = "Admin",
                IsActive = true
            },
            new AppUser
            {
                Username = "tenant_alpha",
                Email = "tenant_alpha@HiveOps.local",
                PasswordHash = PasswordSecurity.HashPassword("123456"),
                Role = "Tenant",
                TenantId = alphaId,
                IsActive = true
            },
            new AppUser
            {
                Username = "tenant_beta",
                Email = "tenant_beta@HiveOps.local",
                PasswordHash = PasswordSecurity.HashPassword("123456"),
                Role = "Tenant",
                TenantId = betaId,
                IsActive = true
            },
            new AppUser
            {
                Username = "superadmin",
                Email = "superadmin@HiveOps.local",
                PasswordHash = PasswordSecurity.HashPassword("123456"),
                Role = "SuperAdmin",
                IsActive = true
            });

        db.Incidents.AddRange(
            new Incident
            {
                Id = Guid.Parse("cccccccc-1111-1111-1111-111111111111"),
                TenantId = alphaId,
                Title = "Alpha Bug",
                Description = "Test incident alpha",
                Category = IncidentCategory.Code,
                Severity = IncidentSeverity.High,
                Status = IncidentStatus.Open,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2)
            },
            new Incident
            {
                Id = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                TenantId = betaId,
                Title = "Beta Perf Issue",
                Description = "Test incident beta",
                Category = IncidentCategory.Infrastructure,
                Severity = IncidentSeverity.Critical,
                Status = IncidentStatus.InProgress,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
            });

        db.KbArticles.AddRange(
            new KbArticle
            {
                Id = Guid.Parse("eeeeeeee-1111-1111-1111-111111111111"),
                TenantId = alphaId,
                Title = "KB Alpha",
                Content = "Knowledge base article for alpha tenant",
                Category = "Code",
                Tags = "[\"bug\",\"fix\"]",
                IsPublished = true,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
            });

        db.Conversations.AddRange(
            new Conversation { Id = alphaConversationId, TenantId = alphaId, Channel = "whatsapp", ChannelUserId = "alpha-user", Status = HiveOps.Domain.Enums.ConversationStatus.Active },
            new Conversation { Id = betaConversationId, TenantId = betaId, Channel = "whatsapp", ChannelUserId = "beta-user", Status = HiveOps.Domain.Enums.ConversationStatus.Active },
            new Conversation { Id = alphaInteractiveConversationId, TenantId = alphaId, Channel = "whatsapp", ChannelUserId = "5491155517000", Status = HiveOps.Domain.Enums.ConversationStatus.Active });

        db.SaveChanges();
    }
}

public sealed class InMemoryConversationStateManager : IConversationStateManager
{
    private readonly Dictionary<string, ConversationStateContext> _state = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task<ConversationStateContext?> GetStateAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var key = BuildKey(tenantId, conversationId);
        lock (_sync)
        {
            if (!_state.TryGetValue(key, out var context))
                return Task.FromResult<ConversationStateContext?>(null);

            return Task.FromResult<ConversationStateContext?>(Clone(context));
        }
    }

    public Task SetStateAsync(Guid tenantId, Guid conversationId, ConversationStateContext context, CancellationToken ct = default)
    {
        var key = BuildKey(tenantId, conversationId);
        lock (_sync)
        {
            context.ConversationId = conversationId;
            context.TenantId = tenantId;
            context.LastUpdatedAt = DateTimeOffset.UtcNow;
            _state[key] = Clone(context);
        }

        return Task.CompletedTask;
    }

    public async Task PauseFlowAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var current = await GetStateAsync(tenantId, conversationId, ct)
            ?? new ConversationStateContext { ConversationId = conversationId, TenantId = tenantId };
        current.PausedState = current.State;
        current.State = ConversationState.AwaitingHuman;
        await SetStateAsync(tenantId, conversationId, current, ct);
    }

    public async Task ResumeFlowAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var current = await GetStateAsync(tenantId, conversationId, ct)
            ?? new ConversationStateContext { ConversationId = conversationId, TenantId = tenantId };
        current.State = current.PausedState ?? ConversationState.Idle;
        current.PausedState = null;
        await SetStateAsync(tenantId, conversationId, current, ct);
    }

    public Task ClearAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var key = BuildKey(tenantId, conversationId);
        lock (_sync)
        {
            _state.Remove(key);
        }

        return Task.CompletedTask;
    }

    private static string BuildKey(Guid tenantId, Guid conversationId) => $"{tenantId:N}:{conversationId:N}";

    private static ConversationStateContext Clone(ConversationStateContext source)
        => new()
        {
            ConversationId = source.ConversationId,
            TenantId = source.TenantId,
            State = source.State,
            PausedState = source.PausedState,
            FailedClassificationCount = source.FailedClassificationCount,
            HasGreeted = source.HasGreeted,
            LastUpdatedAt = source.LastUpdatedAt,
            FlowData = source.FlowData.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
        };
}

public sealed class FakeWhatsAppAccessTokenValidator : IWhatsAppAccessTokenValidator
{
    public Task<WhatsAppAccessTokenValidationResult> ValidateAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.Equals(accessToken, "VALID_META_TOKEN", StringComparison.Ordinal))
            return Task.FromResult(new WhatsAppAccessTokenValidationResult(true, "123456789", "Test Business", null));

        return Task.FromResult(new WhatsAppAccessTokenValidationResult(false, null, null, "Invalid token for test validator."));
    }
}

public sealed class FakeMessagingChannel : IMessagingChannel
{
    public string ChannelName => "whatsapp";
    public List<(string UserId, string Text)> SentMessages { get; } = [];
    public List<(string UserId, string Text, IReadOnlyList<InteractiveButtonOption> Buttons)> SentButtonMessages { get; } = [];
    public List<(string UserId, string Text, string ButtonText, IReadOnlyList<InteractiveListSection> Sections)> SentListMessages { get; } = [];

    public Task SendMessageAsync(string channelUserId, string text, CancellationToken ct = default)
    {
        SentMessages.Add((channelUserId, text));
        return Task.CompletedTask;
    }

    public Task SendInteractiveMessageAsync(string channelUserId, string text, IEnumerable<string> options, CancellationToken ct = default)
    {
        SentMessages.Add((channelUserId, text));
        return Task.CompletedTask;
    }

    public Task SendButtonMessageAsync(
        string channelUserId,
        string text,
        IEnumerable<InteractiveButtonOption> buttons,
        string? footer = null,
        CancellationToken ct = default)
    {
        SentMessages.Add((channelUserId, text));
        SentButtonMessages.Add((channelUserId, text, buttons.ToList()));
        return Task.CompletedTask;
    }

    public Task SendListMessageAsync(
        string channelUserId,
        string text,
        string buttonText,
        IEnumerable<InteractiveListSection> sections,
        string? footer = null,
        CancellationToken ct = default)
    {
        SentMessages.Add((channelUserId, text));
        SentListMessages.Add((channelUserId, text, buttonText, sections.ToList()));
        return Task.CompletedTask;
    }

    public Task MarkAsReadAsync(string messageId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}

public sealed class FakeEmailService : IEmailService
{
    public string? LastResetLink { get; private set; }

    public Task SendPasswordResetEmailAsync(string toEmail, string userName, string resetLink, CancellationToken ct = default)
    {
        LastResetLink = resetLink;
        return Task.CompletedTask;
    }

    public string? ExtractToken()
    {
        if (LastResetLink is null) return null;
        var uri = new Uri(LastResetLink);
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "token")
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }
}

internal sealed class FakeGitService : IGitService
{
    public Task<string> CreateBranchAsync(string b, CancellationToken ct = default) => Task.FromResult(b);
    public Task<string> CommitAsync(string m, IEnumerable<string> f, CancellationToken ct = default) => Task.FromResult("ok");
    public Task PushAsync(string b, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetDiffAsync(string b, CancellationToken ct = default) => Task.FromResult("");
    public Task<bool> BranchExistsAsync(string b, CancellationToken ct = default) => Task.FromResult(false);
    public Task<string> MergePullRequestAsync(string b, CancellationToken ct = default) => Task.FromResult($"MERGED:{b}");
}

internal sealed class FakeDynamicConnectionStringResolver : IDynamicConnectionStringResolver
{
    public string Resolve(Guid tenantId) => "InMemory";
    public Task WarmCacheAsync(Guid tenantId, CancellationToken ct = default) => Task.CompletedTask;
    public void Invalidate(Guid tenantId) { }
}

internal sealed class FakeTenantLookupService : ITenantLookupService
{
    // Known test tenants seeded in Seed()
    private static readonly HashSet<Guid> KnownTenants =
    [
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222")
    ];

    public Task<Guid?> FindByIdAsync(Guid tenantId, CancellationToken ct = default)
        => Task.FromResult(KnownTenants.Contains(tenantId) ? (Guid?)tenantId : null);

    public Task<Guid?> FindByApiKeyAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult(apiKey == "TENANT-ALFA-KEY" ? (Guid?)Guid.Parse("11111111-1111-1111-1111-111111111111") :
                           apiKey == "TENANT-BETA-KEY" ? (Guid?)Guid.Parse("22222222-2222-2222-2222-222222222222") : null);

    public Task<Guid?> FindByWhatsAppNumberAsync(string phone, CancellationToken ct = default)
        => Task.FromResult(phone == "+5491100000001" ? (Guid?)Guid.Parse("11111111-1111-1111-1111-111111111111") :
                           phone == "+5491100000002" ? (Guid?)Guid.Parse("22222222-2222-2222-2222-222222222222") : null);
}
