# API Drift Detection System - Architecture Documentation

## Overview

The API Drift Detection System is a governance feature that monitors outgoing HTTP calls to external APIs, detects schema changes, and automatically creates incidents when contract drift is detected.

## System Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                         API Drift Detection Flow                       │
└─────────────────────────────────────────────────────────────────────┘

   Outbound HTTP Request
            │
            ▼
   ┌─────────────────┐
   │ HttpClient      │
   │ Pipeline        │
   └────────┬────────┘
            │
            ▼
   ┌──────────────────────┐     ┌──────────────────┐
   │ ApiContractObserver  │────▶│ JsonSchemaHasher │
   │ Handler (Delegating) │     │ (SHA256 + Keys)  │
   └────────┬─────────────┘     └──────────────────┘
            │
            ▼
   ┌──────────────────────┐
   │ Compare with DB      │
   │ (ApiDependency)      │
   └────────┬─────────────┘
            │
     ┌──────┴──────┐
     │             │
     ▼             ▼
┌──────────┐  ┌──────────┐
│ Match    │  │ Drift    │
│ (Healthy)│  │ Detected │
└────┬─────┘  └────┬─────┘
     │             │
     │             ▼
     │      ┌──────────────────┐
     │      │ ApiContract      │
     │      │ ChangedEvent     │
     │      │ (MediatR)        │
     │      └────────┬─────────┘
     │               │
     │               ▼
     │      ┌──────────────────┐
     │      │ ApiDrift         │
     │      │ EventHandler     │
     │      └────────┬─────────┘
     │               │
     │               ▼
     │      ┌──────────────────┐
     │      │ IIncidentService │
     │      │ CreateInternal   │
     │      │ IncidentAsync    │
     │      └────────┬─────────┘
     │               │
     ▼               ▼
┌─────────────────────────────┐
│      SignalR Notification    │
│  (Real-time dashboard update)│
└─────────────────────────────┘
```

## Key Components

### 1. JsonSchemaHasher

**Location:** `src/HiveOps.Infrastructure/ApiDrift/JsonSchemaHasher.cs`

- Extracts JSON keys recursively (nested objects and arrays)
- Generates deterministic SHA256 hash
- Schema format: sorted comma-separated key paths (e.g., `data.id,data.name,data.items[].price`)

**Why SHA256?**
- Fast computation
- Low collision probability
- Standard algorithm well-supported in .NET

**Example:**
```json
{"user": {"id": 1, "name": "John"}}
```
Produces hash of: `user.id,user.name`

### 2. ApiContractObserverHandler

**Location:** `src/HiveOps.Infrastructure/ApiDrift/ApiContractObserverHandler.cs`

**Fail-Safety Architecture:**
```csharp
try {
    await ProcessResponseAsync(request, response, cancellationToken);
} catch (Exception ex) {
    // Log but NEVER interrupt the original HTTP response
    _logger.LogError(ex, "[API_DRIFT] Fail-safe: Drift detection failed...");
}
```

**Memory Optimization:**
- Uses `ReadAsStreamAsync()` instead of `ReadAsStringAsync()`
- Buffers content in `MemoryStream` for reuse
- Allows original consumer to read response after hash computation

### 3. ApiDependency Entity

**Key Fields:**
- `TenantId`: Multi-tenancy isolation
- `ProviderName`: Enum (MercadoLibre, Shopify, TiendaNube, Custom)
- `ExpectedSchemaHash`: Stored SHA256 for comparison
- `Status`: Learning, Healthy, DriftDetected, Error
- `LastResponseSchema`: Snapshot for debugging (truncated to 4000 chars)

### 4. Event Flow (MediatR)

**ApiContractChangedEvent** → **ApiDriftEventHandler** → **IncidentService**

- Event contains: OldHash, NewHash, Provider, Endpoint, JsonSnippet
- Handler creates High severity Infrastructure incident
- SignalR notifies supervision dashboard in real-time

### 5. UI Dashboard

**Location:** `src/HiveOps.Api/wwwroot/dashboard/api-inventory.html`

**Features:**
- Visual status indicators (Healthy/Learning/Drift)
- Provider filtering (MercadoLibre, Shopify, TiendaNube, Custom)
- Manual recheck capability
- Add new API dependency form

## Security Considerations

### Tenant Isolation
- `ITenantContext` enforced in all queries
- Admin users use `IgnoreQueryFilters()` but still filter by `TenantId`
- RLS (Row-Level Security) via `TenantSessionContext` interceptor

### Data Protection
- Only schema keys are hashed (no values)
- `LastResponseSchema` truncated to prevent excessive storage
- No API keys or secrets stored in drift detection tables

## Performance Characteristics

| Metric | Target | Implementation |
|--------|--------|----------------|
| Memory per request | < 2x payload | Stream buffering with MemoryStream |
| Hash computation | O(n) keys | Single-pass JSON parsing |
| DB lookup | < 5ms | Indexed by (TenantId, EndpointUrl) |
| Fail-safe overhead | ~0ms | Try-catch with no retry logic |

## Learning Mode

When a new endpoint is detected:
1. Automatically registers with current hash
2. Status = Learning
3. On next request, graduates to Healthy
4. Subsequent requests trigger drift detection

## Integration with Incident Auto-Workflow

**Self-Healing v1.1 (Future):**
- IncidentAutoWorkflowService detects `[API_DRIFT]` prefix
- Generates LLM prompt with Old/New hash comparison
- Creates approval workflow for auto-generated C# mapper

## Troubleshooting

### No drift detected
- Check if `Content-Type: application/json`
- Verify tenant context is resolved
- Review logs for `[API_DRIFT]` prefix

### False positives
- Endpoints with dynamic keys may trigger drift
- Consider excluding non-deterministic fields
- Adjust hash computation if needed

### Performance issues
- Large payloads (>10MB) are buffered in memory
- Consider skipping drift detection for file downloads
- Monitor `MemoryStream` allocations

## Deployment Notes

1. Run migration: `V1.0__ApiDriftDetection.sql`
2. No configuration changes required
3. No environment variables needed
4. System operates in "Learning mode" for new endpoints automatically

## Changelog

- **v1.0** (2026-05-02): Initial release
  - Schema drift detection with SHA256 hashing
  - Fail-safe DelegatingHandler
  - Real-time incident creation
  - Dashboard UI for management
  - SignalR notifications
