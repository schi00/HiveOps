namespace HiveOps.Domain.Models;

public enum TenantResolutionSource
{
    Header = 1,
    JwtClaim = 2,
    ApiKey = 3,
    WhatsAppNumber = 4
}
