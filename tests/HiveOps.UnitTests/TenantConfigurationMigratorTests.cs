using FluentAssertions;
using HiveOps.Application.Configuration;
using HiveOps.Application.Models;

namespace HiveOps.UnitTests;

public sealed class TenantConfigurationMigratorTests
{
    [Fact]
    public void EnsureMigrated_legacy_v0_bumps_to_current()
    {
        var settings = new TenantAdminSettings { SchemaVersion = 0, RuntimeConfiguration = new TenantConfiguration() };
        TenantConfigurationMigrator.EnsureMigrated(ref settings);
        settings.SchemaVersion.Should().Be(TenantConfigurationSchema.Current);
        settings.RuntimeConfiguration.Should().NotBeNull();
        settings.RuntimeConfiguration!.Llm.Model.Should().Be("gpt-4");
        settings.RuntimeConfiguration.DeployGit.GitDefaultBranch.Should().Be("main");
    }

    [Fact]
    public void HydrateEffective_sets_configuration_schema_echo()
    {
        var settings = new TenantAdminSettings();
        var cfg = TenantConfigurationMigrator.HydrateEffective(settings);
        cfg.ConfigurationSchemaVersion.Should().Be(TenantConfigurationSchema.Current);
    }

    [Fact]
    public void TenantConfigurationValidator_accepts_default_canonical()
    {
        var cfg = TenantConfiguration.CreateDefault();
        TenantConfigurationValidator.Validate(cfg).Should().BeEmpty();
    }

    [Fact]
    public void TenantConfigurationValidator_rejects_bad_temperature()
    {
        var cfg = TenantConfiguration.CreateDefault();
        cfg.Llm.Temperature = 99;
        TenantConfigurationValidator.Validate(cfg).Should().NotBeEmpty();
    }
}
