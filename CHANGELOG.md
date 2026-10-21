# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

#### AI Support Engineer foundation (see `docs/plans/ai-support-engineer-master-plan.md`)
- **Multi-source root-cause investigation**: `IRootCauseHypothesisService` / `RootCauseHypothesisService` crosses the tenant schema, read-only diagnostic SELECTs, application logs and recent git commits to produce a persisted `RootCauseBundle` (`Incident.RootCauseJson`) with a recommended remediation track (`Playbook` | `DatabaseFix` | `CodeFix` | `Escalate`) and a post-fix `VerificationQuery`
- **`ITargetLogCollector` / `TargetDatabaseLogCollector`**: discovers log tables in the tenant database (`Logs`, `AppLogs`, `ErrorLogs`, `Serilog`, ...) via `INFORMATION_SCHEMA` and returns recent errors as `AppLogEvidence`
- **`IGitService.GetRecentCommitsForFilesAsync`**: `TenantGitService` runs `git log` over suspicious file paths and returns `GitCommitEvidence` for the hypothesis prompt
- **Deterministic L1/L2 playbooks**: `TenantPlaybook` entity + `IPlaybookRunner` / `PlaybookRunner` execute tenant-declared runbooks (`SqlTemplate` | `HttpWebhook` | `StoredProcedure`) with real `SqlParameter`s, transactional dry-run, `MaxAffectedRows` guard and optional `VerificationSqlQuery`; managed via `PlaybooksController` (`GET/POST/PUT/DELETE /api/support/playbooks`, `POST /api/support/playbooks/{id}/dry-run`)
- **Row-level reversible remediation**: `IRowLevelSnapshotService` / `RowLevelSnapshotService` captures before/after JSON per affected row and generates exact inverse SQL by primary key (`IncidentRemediationSnapshot`), enabling one-click undo; exposed via `GET /api/support/incidents/{id}/snapshots` and `POST /api/support/incidents/{id}/snapshots/{snapshotId}/revert`
- **Cryptographic audit pack**: `IIncidentAuditLedger` / `IncidentAuditLedger` appends SHA-256 hash-chained `IncidentAuditEntry` records per incident (`Triage`, `Hypothesis`, `DryRun`, `Approval`, `Execution`, `Verification`, `Rollback`, `ConfirmedClosed`) with `VerifyChainAsync` integrity check; exposed via `GET /api/support/incidents/{id}/audit-chain`
- **Outcome metering & readiness**: `IOutcomeMeteringService` / `OutcomeMeteringService` counts verified autonomous resolutions per tenant (basis for resolution-based billing); `TenantReadinessController` exposes `GET /api/support/readiness/check` (validates DB + Git + KB + channel connectivity) and `GET /api/support/readiness/outcomes`
- **Autonomous SWE loop**: `CodePatchAndTestGenerator` generates the code fix together with a regression test reproducing the bug; `InternalHiveArenaCallbacksController` pushes the validated fix branch and opens a real PR via `IDeploymentService.CreatePullRequestAsync`, persisting `Incident.PullRequestUrl` and `Incident.ArenaValidationStatus`
- `IncidentResolutionMode.Playbook` (value `6`)
- New `Incident` fields: `RootCauseJson`, `VerificationQuery`, `VerificationPassed`, `PullRequestUrl`, `ArenaValidationStatus`; new entities `IncidentRemediationSnapshot`, `TenantPlaybook`, `IncidentAuditEntry` (all tenant-scoped with query filters)

### Changed
- `IncidentAutoWorkflowService`: after triage it runs the multi-source investigation; when the hypothesis recommends the playbook track, the deterministic playbook executes before free-form SQL/code generation, and the post-fix verification query gates `VerificationPassed` before a resolution is counted
- `PipelineDeploymentService`: implements `CreatePullRequestAsync` for `github` / `azdo` providers

### Database Migration
```bash
sqlcmd -S <server> -d <database> -i db/V1.2__AiSupportEngineerFoundation.sql
```

## [1.1.0] - 2026-05-07

### Added

#### End-to-End Support Agent for Tenant Applications
- **Multi-channel ingestion**: `/webhooks/ticketing` (HMAC `X-HiveOps-Signature`) and generic channel webhooks create conversations + incidents in a shared lifecycle
- **`IChannelDispatcher` / `TicketingChannel` / `IncidentChannelNotifier`**: responses go back through the originating channel (ticket comments, WhatsApp) instead of a fixed channel
- **`MultimodalMediaProcessor`**: downloads inbound media, vision extraction for images, optional audio transcription; `ConversationMessage.MediaId`/`MediaMimeType` persisted

#### Conversational Orchestration
- `AgentToolCatalog` now exposes the real support tools (`analyze_incident`, `provide_incident_info`, `get_incident_status`, `run_db_diagnostic`, `approve_fix`, `reject_fix`, `search_kb`, `escalate_to_human`) with a dispatching `ExecutePlannerToolAsync`
- `PendingInfo` loop: user replies are appended to the incident and re-triaged instead of reclassified
- Free-text resolution: "ya funciona / se solucionó" → `Closed`; "sigue fallando / no funciona" → `Open` when an incident is `Resolved`; ticket comments support CONFIRMAR/REABRIR
- Button actions `btn_confirm_resolved:{id}` / `btn_reopen_incident:{id}` in `AgentRulesEngine`
- `IncidentQuery` answers real incident status (short id + localized state)

#### Governed Remediation
- `IncidentTriageApplier`: non-empty `missingInfo` always transitions to `PendingInfo` (prevents orphaned `InProgress`+bot incidents)
- `ITargetDatabaseDiagnosticService`: read-only `INFORMATION_SCHEMA` discovery + TOP-50 SELECT diagnostics against the tenant database (read-uncommitted, 15s timeout, 30min schema cache)
- `SqlStatementParser`: literal-aware statement splitting, `UPDATE` alias resolution (`UPDATE p SET ... FROM dbo.Products p`), schema/bracket support
- `DatabaseBackupService`: selective backup of affected rows + transactional restore
- Anti-hallucination gate: UPDATE literals not evidenced in user messages/description force `AwaitingApproval` instead of auto-applying
- Approval flow unified across worker/dashboard/chat: pending SQL persisted as `IncidentAttachment(SqlScript)`, `ConversationState.AwaitingIncidentApproval` synchronized, `deploy_fix` executes via `ITenantDataFixerService`
- `TenantDataFixerService`: honors already-resolved tenant scopes, rejects tenant mismatches, executes catalog transactions through `Database.CreateExecutionStrategy()` (SQL Server retry-compatible), uses dedicated tenant connection in SelfHosted mode
- Code-fix path: tenant workspace resolution, `UnifiedDiffPatchApplier` with exact SEARCH/REPLACE, `dotnet build` gate with LLM reprompt before commit
- `PipelineDeploymentService`: `github`/`azdo`/`none` providers
- `KbArticleGenerator`: generates `EmbeddingVector` via `IEmbeddingService`, tenant-aware scoped lookup, drafts stay unpublished for review

### Fixed
- Planner prompt header no longer references e-commerce purchases; routing heuristics expanded for recurring-failure phrases ("sigue mal", "no funciona", "incorrecto", "roto")
- `SafetyValidator.HasSpecificWhereCondition` no longer rejects every UPDATE/DELETE (real tautology detection, comment stripping)
- `IncidentAutoWorkflowService`: unsupported categories escalate to engineer and persist instead of retry-looping every 30s
- `IncidentCategoryMapper`: `ConfigError` now maps to `Database` so corrupt stored values reach remediation
- `ApprovalWorkflowService` + `KbArticleGenerator`: scoped services load incidents by PK with `IgnoreQueryFilters()` then set tenant context (fixes "Incident not found" in new DI scopes)
- `KbArticleGenerator` status requirement relaxed for eager generation at `Resolved`; `SaveChangesAsync` now runs before KB generation
- `SqlStatementParser.SplitStatements`/`ExtractWhereClause` no longer split on `;` inside T-SQL string literals
- LLM retry backoff on proposals and planner to avoid exhausting provider TPM windows
- `WhatsApp:AllowUnresolvedTenantFallback` (default `false`) closes the "first active tenant" isolation gap
- `AnalyzeIncidentAsync` no longer misclassifies reassignment requests as human escalation

### Changed
- `Incident`/`ConversationMessage`/`KbArticle` entities extended (channel fields, media fields, fingerprint, triage confidence, resolution mode, embedding)
- `Worker` LLM calls moved behind `IDynamicConnectionStringResolver`-backed tenant resolution (no hardcoded connection strings)
- New configuration documented in [`docs/operations.md`](docs/operations.md): `Ticketing:*`, `Media:*`, `DeployGit:*`, `WhatsApp:AllowUnresolvedTenantFallback`, `IncidentAutoWorkflow:*`

### Database Migration
```bash
sqlcmd -S <server> -d <database> -i db/AddIncidentChannelFields.sql
sqlcmd -S <server> -d <database> -i db/AddConversationMessageMediaFields.sql
sqlcmd -S <server> -d <database> -i db/migrations/V1.1__IncidentEffectivenessAndKbEmbedding.sql
```

## [1.0.0] - 2026-05-02

### Added

#### API Drift Detection System
- **Automatic schema monitoring** for external API calls (MercadoLibre, Shopify, TiendaNube, Custom)
- **SHA256-based schema hashing** with recursive key extraction for nested objects and arrays
- **Fail-safe DelegatingHandler** that never interrupts user transactions
- **Learning mode** for automatic registration of new endpoints
- **Real-time incident creation** on drift detection with High severity
- **SignalR notifications** to supervision dashboard

#### New Entities and Enums
- `ApiDependency` entity with tenant-scoped storage
- `ApiProvider` enum: MercadoLibre, Shopify, TiendaNube, Custom
- `ApiDependencyStatus` enum: Healthy, DriftDetected, Learning, Error

#### Infrastructure Components
- `JsonSchemaHasher` - Efficient schema fingerprinting
- `ApiContractObserverHandler` - HTTP pipeline interceptor with memory optimization
- `IIncidentService` / `IncidentService` - Internal incident creation

#### API Endpoints
- `GET /api/api-dependencies` - List all API dependencies with filtering
- `GET /api/api-dependencies/{id}` - Get single dependency details
- `POST /api/api-dependencies` - Register new API dependency
- `PUT /api/api-dependencies/{id}` - Update dependency configuration
- `DELETE /api/api-dependencies/{id}` - Remove dependency
- `POST /api/api-dependencies/{id}/recheck` - Force immediate recheck

#### UI Components
- New dashboard page: `/dashboard/api-inventory.html`
- Visual status badges (Healthy/Learning/Drift/Error)
- Provider filtering and statistics
- Add/Remove API dependency forms
- Loading spinner and error handling

#### Auto-Workflow Integration
- `ApiDriftEventHandler` - MediatR notification handler
- `ApiContractChangedEvent` - Domain event with Old/New hash comparison
- Foundation for v1.1 self-healing with LLM-generated mappers

### Security
- Strict tenant isolation via `ITenantContext`
- Admin query filtering with `IgnoreQueryFilters()`
- No storage of API values (only schema keys hashed)
- Truncated schema snapshots to prevent storage abuse

### Performance
- Memory-efficient stream buffering for large payloads
- Single-pass JSON parsing for hash computation
- Indexed queries by (TenantId, EndpointUrl)
- Zero-overhead fail-safe error handling

### Changed
- Updated `IncidentAutoWorkflowService` to handle `Infrastructure` category incidents
- Enhanced dashboard navigation with API Inventory link

### Deprecated
- None

### Removed
- None

### Fixed
- None

## [0.9.0] - Previous Release
- Base HiveOps platform with multi-tenancy
- Incident management system
- SignalR real-time notifications
- Support dashboard with dark/light themes

---

## Migration Notes

### Database Migration
Run the following SQL script before deployment:
```bash
sqlcmd -S <server> -d <database> -i db/migrations/V1.0__ApiDriftDetection.sql
```

### Breaking Changes
None. This release is backward compatible.

### New Dependencies
None. Uses existing .NET 9 and EF Core dependencies.
