using FluentAssertions;
using HiveOps.Application.Support;
using HiveOps.Domain.Enums;
using Xunit;

namespace HiveOps.UnitTests.Application;

public sealed class IncidentCategoryMapperTests
{
    [Theory]
    [InlineData("CodeBug", IncidentCategory.Code)]
    [InlineData("DbCorruption", IncidentCategory.Database)]
    [InlineData("ConfigError", IncidentCategory.Database)]
    [InlineData("Performance", IncidentCategory.Infrastructure)]
    [InlineData("Security", IncidentCategory.Infrastructure)]
    [InlineData("Other", IncidentCategory.Other)]
    public void ToDomain_maps_triage_strings(string triage, IncidentCategory expected) =>
        IncidentCategoryMapper.ToDomain(triage).Should().Be(expected);

    [Fact]
    public void ToTriageLabel_roundtrips_core_buckets() =>
        IncidentCategoryMapper.ToTriageLabel(IncidentCategory.Code).Should().Be("CodeBug");
}
