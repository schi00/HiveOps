using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Infrastructure.Multitenancy;

public sealed class TenantLookupService : ITenantLookupService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantLookupService> _logger;

    public TenantLookupService(IConfiguration configuration, ILogger<TenantLookupService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string MasterConnectionString =>
        _configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing 'DefaultConnection' connection string.");

    public async Task<Guid?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Tenants WHERE Id = @id AND IsActive = 1";
        cmd.Parameters.AddWithValue("@id", tenantId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is Guid found)
            return found;
        if (result is not null && Guid.TryParse(result.ToString(), out var parsed))
            return parsed;

        return null;
    }

    public async Task<Guid?> FindByApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Tenants WHERE ApiKey = @apiKey AND IsActive = 1";
        cmd.Parameters.AddWithValue("@apiKey", apiKey);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is Guid found)
            return found;
        if (result is not null && Guid.TryParse(result.ToString(), out var parsed))
            return parsed;

        return null;
    }

    public async Task<Guid?> FindByWhatsAppNumberAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Tenants WHERE WhatsAppNumber = @phone AND IsActive = 1";
        cmd.Parameters.AddWithValue("@phone", phoneNumber);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is Guid found)
            return found;
        if (result is not null && Guid.TryParse(result.ToString(), out var parsed))
            return parsed;

        return null;
    }
}
