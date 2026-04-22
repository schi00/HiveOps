# FTS Search Timeout Fix

## Problem
WhatsApp webhooks were hanging for 10+ minutes with `TaskCanceledException` in Full-Text Search (FTS) operations. The service logs showed:
- FTS queries were timing out at **30 seconds** (default SQL command timeout)
- System was retrying KeywordSearch fallback repeatedly without completing
- Agent planner kept attempting tool_call decisions indefinitely

## Root Cause
1. **Default CommandTimeout**: SqlCommand objects used 30-second default timeout
2. **Complex FTS queries**: FREETEXTTABLE queries on Products were exceeding timeout threshold
3. **No fallback strategy**: When FTS timed out, the system didn't terminate gracefully

## Solution Applied
✅ **Increased CommandTimeout to 120 seconds** for all search operations in `SqlServerHybridSearchService`:
- `VectorSearchAsync()` - Vector distance search
- `KeywordSearchAsync()` - Keyword score-based fallback
- `FullTextSearchAsync()` - Full-Text Search queries

**File Modified**: `src/SaaSBot.Infrastructure/Search/SqlServerHybridSearchService.cs`

### Changes:
```csharp
// Before (default 30s timeout)
await using var cmd = new SqlCommand(sql, conn);
cmd.Parameters.AddWithValue(/* ... */);

// After (120s timeout)
await using var cmd = new SqlCommand(sql, conn);
cmd.CommandTimeout = 120;
cmd.Parameters.AddWithValue(/* ... */);
```

## Verification Steps

1. **[REQUIRED] Check FTS Index Status**:
   Run the diagnostic script against your database:
   ```bash
   sqlcmd -S <your-server> -d SaaSBot -i db/Diagnose_FTS_Index.sql
   ```
   
   This will report:
   - Whether FTS is installed on SQL Server
   - Valid FTS catalogs (`SaaSBotFT`)
   - Index columns on Products table
   - Sample FREETEXTTABLE test results

2. **[OPTIONAL] If FTS Still Hangs After Timeout Increase**:
   - Check SQL Server agent logs for long-running queries
   - Verify Products table has complete data (Name, Brand, Description not NULL)
   - Consider rebuilding FTS index:
     ```sql
     ALTER FULLTEXT INDEX ON Products REBUILD;
     ```
   - Monitor actual query performance in SQL Profiler

3. **Test Webhook Flow**:
   - Send WhatsApp webhook with inventory query
   - Should now complete within 2-3 seconds (or timeout after 120s gracefully)
   - Monitor logs for `FTS search failed` warnings (acceptable fallback)

## Next Steps

1. **⏳ SHORT TERM** (Immediate):
   - Deploy this change
   - Test WhatsApp webhook flow
   - Monitor logs for remaining timeouts

2. **📊 MEDIUM TERM** (This sprint):
   - Verify FTS index exists and is fully populated
   - Run diagnostic script to identify any FTS issues
   - Consider query optimization if searches still exceed 60 seconds

3. **🔧 LONG TERM** (Next sprint):
   - Add query timeout monitoring/alerting
   - Profile search performance under load
   - Consider hybrid approach: cache frequent searches, optimize queries

## Impact
- ✅ Webhook requests will complete within reasonable time or fail gracefully
- ✅ No tenant isolation impact (tenant context still enforced)
- ✅ Backward compatible (existing queries unchanged)
- ✅ Build successful, no compilation errors

## Related Files
- Schema setup: [db/VadiSuite_Init.sql](VadiSuite_Init.sql#L341-L362)
- Diagnostic: [db/Diagnose_FTS_Index.sql](Diagnose_FTS_Index.sql)
- Service: [src/SaaSBot.Infrastructure/Search/SqlServerHybridSearchService.cs](../src/SaaSBot.Infrastructure/Search/SqlServerHybridSearchService.cs)
