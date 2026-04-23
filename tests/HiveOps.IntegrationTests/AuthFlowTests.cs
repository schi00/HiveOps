using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HiveOps.Application.Models;

namespace HiveOps.IntegrationTests;

public sealed class AuthFlowTests : IClassFixture<DashboardWebApplicationFactory>
{
    private readonly DashboardWebApplicationFactory _factory;

    public AuthFlowTests(DashboardWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TenantLogin_Should_Enable_Dashboard_Without_Header_Based_Credentials()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { username = "tenant_alpha", password = "123456" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await client.GetFromJsonAsync<AuthMeDto>("/api/auth/me");
        me.Should().NotBeNull();
        me!.Authenticated.Should().BeTrue();
        me.Role.Should().Be("Tenant");
        me.TenantName.Should().Be("Alpha");

        var summary = await client.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard/summary");
        summary.Should().NotBeNull();
        summary!.TotalSales.Should().Be(150);
    }

    [Fact]
    public async Task AdminLogin_Should_Enable_Admin_Tenant_List_Without_Admin_Header()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin00", password = "123456" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await client.GetFromJsonAsync<AuthMeDto>("/api/auth/me");
        me.Should().NotBeNull();
        me!.Authenticated.Should().BeTrue();
        me.Role.Should().Be("Admin");

        var tenants = await client.GetFromJsonAsync<List<AdminTenantDto>>("/api/admin/tenants");
        tenants.Should().NotBeNull();
        tenants!.Should().HaveCount(2);
        tenants.Should().Contain(t => t.Name == "Alpha");
        tenants.Should().Contain(t => t.Name == "Beta");
    }

    [Fact]
    public async Task ForgotAndResetPassword_Should_Allow_Login_With_NewPassword()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });

        var forgotResponse = await client.PostAsJsonAsync("/api/auth/forgot-password", new { usernameOrEmail = "tenant_alpha" });
        forgotResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Token is no longer returned in the response — it is sent via email.
        // The FakeEmailService captures the reset link for test verification.
        var token = _factory.FakeEmail.ExtractToken();
        token.Should().NotBeNullOrWhiteSpace("the fake email service should have captured the reset link");

        var resetResponse = await client.PostAsJsonAsync("/api/auth/reset-password", new
        {
            token,
            newPassword = "654321"
        });
        resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { username = "tenant_alpha", password = "654321" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_Should_Be_Able_To_Rotate_WhatsApp_ApiKey_From_Settings()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin00", password = "123456" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var tenants = await client.GetFromJsonAsync<List<AdminTenantDto>>("/api/admin/tenants");
        tenants.Should().NotBeNull();
        var tenantId = tenants![0].Id;

        var settingsEnvelope = await client.GetFromJsonAsync<TenantSettingsEnvelope>($"/api/admin/tenants/{tenantId}/settings");
        settingsEnvelope.Should().NotBeNull();

        var settings = settingsEnvelope!.Settings;
        settings.WhatsApp.Enabled = true;
        settings.WhatsApp.PhoneNumber = "+15556689857";
        settings.WhatsApp.Provider = "MetaCloud";
        settings.WhatsApp.ApiKey = "WA_ROTATE_TOKEN_1";
        settings.WhatsApp.ApiKeyUpdatedAtUtc = DateTimeOffset.UtcNow;

        var saveFirst = await client.PutAsJsonAsync($"/api/admin/tenants/{tenantId}/settings", settings);
        saveFirst.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterFirstSave = await client.GetFromJsonAsync<TenantSettingsEnvelope>($"/api/admin/tenants/{tenantId}/settings");
        afterFirstSave.Should().NotBeNull();
        afterFirstSave!.Settings.WhatsApp.ApiKey.Should().Be("WA_ROTATE_TOKEN_1");

        settings.WhatsApp.ApiKey = "WA_ROTATE_TOKEN_2";
        settings.WhatsApp.ApiKeyUpdatedAtUtc = DateTimeOffset.UtcNow;

        var saveSecond = await client.PutAsJsonAsync($"/api/admin/tenants/{tenantId}/settings", settings);
        saveSecond.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterSecondSave = await client.GetFromJsonAsync<TenantSettingsEnvelope>($"/api/admin/tenants/{tenantId}/settings");
        afterSecondSave.Should().NotBeNull();
        afterSecondSave!.Settings.WhatsApp.ApiKey.Should().Be("WA_ROTATE_TOKEN_2");
    }

    [Fact]
    public async Task Admin_Should_Validate_And_Save_WhatsApp_AccessToken_Only_When_Valid()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin00", password = "123456" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var tenants = await client.GetFromJsonAsync<List<AdminTenantDto>>("/api/admin/tenants");
        tenants.Should().NotBeNull();
        var tenantId = tenants!.First().Id;

        var invalidResponse = await client.PostAsJsonAsync($"/api/admin/tenants/{tenantId}/whatsapp/access-token", new
        {
            accessToken = "INVALID_TOKEN"
        });
        invalidResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var settingsAfterInvalid = await client.GetFromJsonAsync<TenantSettingsEnvelope>($"/api/admin/tenants/{tenantId}/settings");
        settingsAfterInvalid.Should().NotBeNull();
        settingsAfterInvalid!.Settings.WhatsApp.ApiKey.Should().NotBe("INVALID_TOKEN");

        var validResponse = await client.PostAsJsonAsync($"/api/admin/tenants/{tenantId}/whatsapp/access-token", new
        {
            accessToken = "VALID_META_TOKEN"
        });
        validResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var settingsAfterValid = await client.GetFromJsonAsync<TenantSettingsEnvelope>($"/api/admin/tenants/{tenantId}/settings");
        settingsAfterValid.Should().NotBeNull();
        settingsAfterValid!.Settings.WhatsApp.ApiKey.Should().Be("VALID_META_TOKEN");
    }

    [Fact]
    public async Task Admin_Should_List_And_Replace_Catalog_Attribute_Definitions()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin00", password = "123456" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var tenants = await client.GetFromJsonAsync<List<AdminTenantDto>>("/api/admin/tenants");
        tenants.Should().NotBeNull();
        var tenantId = tenants!.First().Id;

        var initial = await client.GetFromJsonAsync<List<CatalogAttributeDefinitionDto>>($"/api/admin/tenants/{tenantId}/catalog/attributes");
        initial.Should().NotBeNull();

        var payload = new List<CatalogAttributeDefinitionDto>
        {
            new("color", "Color", "text", true, true, 0),
            new("material", "Material", "text", true, true, 1),
            new("temporada", "Temporada", "text", false, true, 2)
        };

        var save = await client.PutAsJsonAsync($"/api/admin/tenants/{tenantId}/catalog/attributes", payload);
        save.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterSave = await client.GetFromJsonAsync<List<CatalogAttributeDefinitionDto>>($"/api/admin/tenants/{tenantId}/catalog/attributes");
        afterSave.Should().NotBeNull();
        afterSave!.Should().HaveCount(3);
        afterSave.Should().Contain(x => x.AttributeKey == "color" && x.DisplayName == "Color" && x.IsFilterable);
        afterSave.Should().Contain(x => x.AttributeKey == "temporada" && !x.IsFilterable && x.IsSearchable);

        var replacePayload = new List<CatalogAttributeDefinitionDto>
        {
            new("color", "Color principal", "text", true, true, 0)
        };

        var replace = await client.PutAsJsonAsync($"/api/admin/tenants/{tenantId}/catalog/attributes", replacePayload);
        replace.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterReplace = await client.GetFromJsonAsync<List<CatalogAttributeDefinitionDto>>($"/api/admin/tenants/{tenantId}/catalog/attributes");
        afterReplace.Should().NotBeNull();
        afterReplace!.Should().HaveCount(1);
        var firstAttribute = afterReplace![0];
        firstAttribute.AttributeKey.Should().Be("color");
        firstAttribute.DisplayName.Should().Be("Color principal");
    }

    [Fact]
    public async Task Admin_Should_Create_Update_And_Delete_Tenant_User()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin00", password = "123456" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var tenants = await client.GetFromJsonAsync<List<AdminTenantDto>>("/api/admin/tenants");
        tenants.Should().NotBeNull();
        var tenantId = tenants!.First().Id;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"tenant_new_{suffix}";
        var email = $"{username}@HiveOps.local";

        var createResponse = await client.PostAsJsonAsync($"/api/admin/tenants/{tenantId}/users", new
        {
            username,
            email,
            password = "123456",
            isActive = true
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await createResponse.Content.ReadFromJsonAsync<TenantUserDto>();
        created.Should().NotBeNull();
        created!.Username.Should().Be(username);

        var updateResponse = await client.PutAsJsonAsync($"/api/admin/tenants/{tenantId}/users/{created.Id}", new
        {
            username = $"{username}_edit",
            email,
            password = "654321",
            isActive = false
        });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await updateResponse.Content.ReadFromJsonAsync<TenantUserDto>();
        updated.Should().NotBeNull();
        updated!.Username.Should().Be($"{username}_edit");
        updated.IsActive.Should().BeFalse();

        var list = await client.GetFromJsonAsync<List<TenantUserDto>>($"/api/admin/tenants/{tenantId}/users");
        list.Should().NotBeNull();
        list!.Should().Contain(x => x.Id == created.Id && x.Username == $"{username}_edit" && !x.IsActive);

        var deleteResponse = await client.DeleteAsync($"/api/admin/tenants/{tenantId}/users/{created.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var listAfterDelete = await client.GetFromJsonAsync<List<TenantUserDto>>($"/api/admin/tenants/{tenantId}/users");
        listAfterDelete.Should().NotBeNull();
        listAfterDelete!.Should().NotContain(x => x.Id == created.Id);
    }

    [Fact]
    public async Task Admin_Should_Get_Bot_Metrics()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin00", password = "123456" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var metrics = await client.GetFromJsonAsync<AdminBotMetricsDto>("/api/admin/tenants/metrics/bot");
        metrics.Should().NotBeNull();
        metrics!.ActiveConversations.Should().BeGreaterThan(0);
        metrics.FallbackCount.Should().BeGreaterThanOrEqualTo(0);
    }

    private sealed record AuthMeDto(bool Authenticated, string? Role, Guid? TenantId, string? TenantName, string? DisplayName);
    private sealed record AdminTenantDto(Guid Id, string Name, string? WhatsAppNumber, bool IsActive, DateTimeOffset CreatedAt);
    private sealed record TenantSettingsEnvelope(Guid Id, string Name, TenantAdminSettings Settings);
    private sealed record CatalogAttributeDefinitionDto(
        string AttributeKey,
        string DisplayName,
        string DataType,
        bool IsFilterable,
        bool IsSearchable,
        int SortOrder);

    private sealed record TenantUserDto(
        Guid Id,
        string Username,
        string Email,
        bool IsActive,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record AdminBotMetricsDto(
        int ActiveConversations,
        int AwaitingHumanConversations,
        int FallbackCount,
        double AvgResponseSeconds,
        List<MetricPointDto> ResponsesPerDay,
        List<MetricPointDto> FallbacksPerDay);

    private sealed record MetricPointDto(string Label, double Value);
}