# HiveOps operations runbook

This document describes how to run HiveOps in **SaaS-style local dev**, **self-hosted Docker Compose**, and **ephemeral CI** (GitHub Actions preview workflow).

## Configuration model

| Setting | Description |
|---------|----------------|
| `HiveOps:Deployment:Mode` | `SaaS` (default), `SelfHosted`, or `Ephemeral` |
| `HiveOps:Deployment:Billing` | `None` (no Stripe calls) or `Stripe` (requires `Stripe:ApiKey` and optional `STRIPE_SDK` build) |
| `HiveOps:Deployment:AllowInMemoryConversationState` | When `true` with `Mode=Ephemeral`, uses in-memory conversation state instead of Redis |
| `HiveOps:Deployment:AllowInsecureJwtForTests` | Ephemeral/CI only: allows the development JWT signing key |
| `HiveOps:Deployment:AllowEmptyAdminApiKeyInEphemeral` | Ephemeral only: allows empty `Admin:ApiKey` (not recommended) |
| `HiveOps:Deployment:FailClosedTenantConnectionInSelfHosted` | When `true` with `Mode=SelfHosted`, missing per-tenant `EncryptedConnectionString` or a cold resolver cache throws instead of falling back to `DefaultConnection` (use only for dedicated DB per tenant) |

Secrets must come from environment variables or your secret store; do not commit real passwords.

### Operation matrix (summary)

| Variable / concern | SaaS (production) | Self-hosted | Ephemeral (CI) |
|--------------------|-----------------|--------------|----------------|
| `HiveOps:Deployment:Mode` | `SaaS` | `SelfHosted` | `Ephemeral` |
| `Jwt:Key` | Required (secret / vault) | Required (env / vault); never the dev default | Dev default allowed only if `AllowInsecureJwtForTests=true` |
| `Admin:ApiKey` | Required | Required | Required unless `AllowEmptyAdminApiKeyInEphemeral=true` (tests may set `HIVEOPS_EPHEMERAL_ADMIN_KEY`) |
| `Billing` | `Stripe` or `None` per product | Typically `None` unless you sell with Stripe | Must be `None` |
| Redis | Managed / HA | Container or local service | Redis `services:` in GitHub Actions |
| SQL password in CI | N/A | N/A | Repository secret `HIVEOPS_CI_SQL_PASSWORD` (see preview workflow) |

**Pull requests from forks:** GitHub does not expose repository secrets to workflows from forks the same way; the preview job will fail the “Verify CI SQL secret” step until secrets are available (e.g. run workflow only on same-repo PRs, or use a dedicated approach for forks).

---

## SaaS / local development (Windows or Linux)

1. Install **.NET 9 SDK**, **SQL Server** (or use Docker only for SQL), and **Redis** locally.
2. Copy connection settings. Example **non-secret** shape:

| Variable | Example value (adjust host and password) |
|----------|------------------------------------------|
| `ConnectionStrings__DefaultConnection` | `Server=localhost,1433;Database=Hive;User Id=sa;Password=<YOUR_SA_PASSWORD>;TrustServerCertificate=True;MultipleActiveResultSets=true` |
| `Redis__ConnectionString` | `localhost:6379` |
| `Jwt__Key` | At least 32 random characters in **Production**; in Development the app may fall back only when `Mode` is not `SelfHosted` |
| `Admin__ApiKey` | Long random string for admin API routes |
| `Git__RepoPath` | Writable directory for a single shared git working copy when tenants do not set `DeployGit.GitRepositoryUrl` |
| `Git__WorkspacesRoot` | Parent directory for per-tenant clones in **SelfHosted** mode when `DeployGit.GitRepositoryUrl` is set (see runbook section below) |
| `HiveOps__DataProtectionKeysPath` | Writable directory for Data Protection keys |

3. Apply database schema and seeds using the bootstrap scripts (see **Database bootstrap** below).
4. Run the API from the repository root:

```bash
dotnet run --project src/HiveOps.Api/HiveOps.Api.csproj
```

5. Verify: `GET http://localhost:<port>/health/ready` should return HTTP 200.

---

## Self-hosted (Docker Compose)

From the repository root:

```bash
docker compose -f deploy/docker-compose.yml up -d --build sqlserver redis
```

Wait until SQL Server is ready, then bootstrap the `Hive` database (password must match `MSSQL_SA_PASSWORD` in `deploy/docker-compose.yml` or your override):

**Linux / macOS (bash):**

```bash
export HIVEOPS_SQL_PASSWORD='HiveOps-Local-SA-2026-Strong!'
export HIVEOPS_SQL_HOST=127.0.0.1
export HIVEOPS_SQL_PORT=1433
chmod +x db/bootstrap.sh
./db/bootstrap.sh
```

**Windows (PowerShell):**

```powershell
$env:HIVEOPS_SQL_PASSWORD = 'HiveOps-Local-SA-2026-Strong!'
$env:HIVEOPS_SQL_HOST = '127.0.0.1'
$env:HIVEOPS_SQL_PORT = '1433'
.\db\bootstrap.ps1
```

Start the API container:

```bash
docker compose -f deploy/docker-compose.yml up -d api
```

Verify: `curl -f http://localhost:8080/health/ready`

The compose file sets `HiveOps__Deployment__Mode=SelfHosted` and `Billing=None` by default.

### Self-hosted operator configuration (models, DB, Git)

| Concern | Behavior |
|---------|----------|
| Deployment mode in UI | Authenticated clients may call `GET /api/deployment/info` to read `{ "mode": "SaaS" \| "SelfHosted" \| "Ephemeral" }` (no secrets). The dashboard uses this to show self-hosted-only panels. |
| LLM model for corrections / planner | When `HiveOps:Deployment:Mode` is `SelfHosted`, `KernelFactory` uses `TenantConfiguration.Llm.Model` as the OpenRouter model id (same stack as global Semantic Kernel). Persist via existing PATCH `/api/tenants/{id}/config/llm` or the dashboard. Temperature / max tokens apply on self-hosted via planner and incident analysis paths. |
| Dedicated SQL per tenant | `PUT /api/admin/tenants/{tenantId}/dedicated-database` with JSON `{ "connectionString": "<plain>" }` encrypts (Data Protection purpose `HiveOps.TenantConnectionString`, base64 stored) into `Tenants.EncryptedConnectionString`, probes connectivity with a short timeout, then invalidates the connection-string cache. Send `null` or empty string to clear and fall back to the catalog DB (unless fail-closed is enabled). **Only allowed when `Mode=SelfHosted`.** `GET` the same path returns `{ "configured": true \| false }` without exposing secrets. Admin JWT (`Admin`/`SuperAdmin`) or `X-Admin-Key` applies as for other `/api/admin/*` routes. |
| Git per tenant | When self-hosted **and** `DeployGit.GitRepositoryUrl` is set for the tenant, `IGitService` operations use a clone under `Git:WorkspacesRoot/{tenantId:N}` (lazy `git clone` / `fetch`). If the URL is empty, behavior falls back to the single working copy `Git:RepoPath` (same as SaaS). Default branch for merge/diff follows `DeployGit.GitDefaultBranch` (fallback `main`). The host needs `git` on `PATH`; HTTPS auth for private GitHub repos must use a credential helper or similar—never commit secrets. |

---

## Omnichannel support and auto-workflow (soporte omnicanal)

Configuration for the HiveCrew support-agent scenario (see [`docs/plans/hivecrew-support-agent-plan.md`](plans/hivecrew-support-agent-plan.md)): WhatsApp and Ticketera as inbound/outbound channels, incident auto-workflow, media transcription, and deploy pipelines.

| Setting | Description |
|---------|-------------|
| `Ticketing:BaseUrl` | Ticketera API base URL. Outbound adapter posts `POST {BaseUrl}/api/tickets/{ticketId}/comments`. When empty, ticketing sends are a logged no-op. |
| `Ticketing:ApiKey` | Bearer token sent on outbound ticket comments. Provide via env var / secret store, never committed. |
| `Ticketing:InboundSecret` | Shared secret for HMAC-SHA256 validation of inbound `POST /webhooks/tickets` (`X-HiveOps-Signature: sha256=<hex>`). Required in non-dev environments. |
| `Ticketing:SkipWebhookSignatureValidation` | **Development only**: skips inbound ticket signature validation when the environment is Development. Never enable in production. |
| `WhatsApp:AllowUnresolvedTenantFallback` | Default `false`. When `false`, an inbound WhatsApp message that cannot be matched to a tenant (by destination number / `phone_number_id`) is acknowledged with `200 { received: true, processed: false }` and **not** processed, preserving tenant isolation. Set `true` only for dev / single-tenant deployments where falling back to the first active tenant is acceptable. |
| `Media:EnableAudioTranscription` | Default `false`. When `true`, inbound WhatsApp audio is transcribed before processing. |
| `Media:TranscriptionEndpoint` | Transcription API endpoint (OpenAI-compatible `audio/transcriptions`). Required when transcription is enabled. |
| `Media:TranscriptionModel` | Transcription model id (default `whisper-1`). |
| `Media:TranscriptionApiKey` | API key for the transcription endpoint. Secret / env only. |
| `WhatsApp:GraphApiBaseUrl` | Meta Graph API base (default `https://graph.facebook.com/v22.0`), used for media download and outbound sends. |
| `DeployGit:Provider` | `github` \| `azdo` \| `none` (default `none` = stub, no pipeline dispatch). |
| `DeployGit:ApiToken` | PAT / workflow token for the provider. Secret ref / env var only — never commit. For `github` also set `DeployGit:Owner`, `DeployGit:Repo`, `DeployGit:WorkflowId`; for `azdo` set `DeployGit:Organization`, `DeployGit:Project`, `DeployGit:DefinitionId`. |
| `IncidentAutoWorkflow:Enabled` | Enables the incident auto-workflow hosted service (`HiveOps.Workers`, hosted by the API process). |
| `IncidentAutoWorkflow:PollingIntervalSeconds` | Worker polling interval (default `30`). |
| `IncidentAutoWorkflow:MaxConcurrentIncidents` | Max incidents processed in parallel (default `5`). |
| `IncidentAutoWorkflow:CodeLineThreshold` / `IncidentAutoWorkflow:DbRecordThreshold` | Human-approval thresholds for code fixes and data fixes (default `3` each). |

**Tenant resolution per webhook route:**

- `POST /webhooks/whatsapp` — the path is exempt from `TenantResolutionMiddleware`; the tenant is resolved inside the controller from the WhatsApp **destination number** (`display_phone_number`) or `phone_number_id`. With `WhatsApp:AllowUnresolvedTenantFallback=false` an unresolved message is acknowledged but not processed.
- `POST /webhooks/{channelType}` (generic channel) and `POST /webhooks/tickets` (Ticketera) — tenant is resolved by `TenantResolutionMiddleware` via the `X-Api-Key` header; unresolved requests get `401` before reaching the controller.

**Ticket comment signals:** on an incident in `Resolved` status, an inbound `comment` event acts as the user confirmation (no buttons on ticketing): phrases like *confirmar / ya funciona / se solucionó* close the incident (`{ resolved: "closed" }`), phrases like *reabrir / sigue fallando / persiste* reopen it to `Open` (`{ resolved: "reopened" }`). Other comments append to the incident and re-open `PendingInfo` incidents.

---

## HiveArena proving ground (post-fix validation)

HiveArena (`src/HiveArena`) validates proposed code fixes in an **ephemeral filesystem environment**: it clones the candidate branch/commit, runs `dotnet build`, then `dotnet test` per discovered test project, and reports **Survival / Defeat / Error** back to HiveOps with an LLM-readable report (including the failing `dotnet` output tail). Requires `git` and `dotnet` on PATH of the HiveArena host.

| Setting | Description |
|---------|-------------|
| `HiveArena:Host:ApiKey` | Optional ingress key; requests must send `X-HiveArena-Key`. |
| `HiveArena:HiveOpsBridge:*` | Base URL + `X-Dev-Key` for synthetic incidents (`POST /api/support/incidents/from-sandpit`). |
| `HiveArena:HiveOpsCallback:BaseUrl` / `SharedSecret` | Callback to `POST {BaseUrl}/api/internal/hivearena/validation-completed` with `X-HiveArena-Key`; must match HiveOps `HiveOps:HiveArenaInbound:SharedSecret`. |
| `HiveArena:ProvingGround:WorkspacesRoot` | Root for ephemeral env dirs (default `%TEMP%/hivearena`); deleted after each run. |
| `HiveArena:ProvingGround:GitTimeoutSeconds` / `BuildTimeoutSeconds` / `TestTimeoutSeconds` | Defaults `240` / `600` / `900`. |
| `HiveArena:ProvingGround:RunTestsWhenPresent` | `false` → build gate only. |
| `HiveArena:ProvingGround:ExcludedTestProjectPatterns` | Project name fragments to skip, e.g. `["IntegrationTests"]` when the environment lacks their external dependencies (live Postgres, Redis). |
| `HiveArena:ProvingGround:SetupCommand` | Optional command run in the checked-out workspace before build — e.g. `docker compose -f deploy/test-env.yml up -d --wait` to provision dependencies (DB, cache). Failure → outcome `Error`. |
| `HiveArena:ProvingGround:TeardownCommand` | Optional command always run after the replay (e.g. `docker compose -f deploy/test-env.yml down -v`). |
| `HiveArena:ProvingGround:TestEnvironmentVariables` | Env vars injected into `dotnet test` (e.g. `ConnectionStrings__Default` pointing at the provisioned database, `ASPNETCORE_ENVIRONMENT`). |

**HiveOps side** (`HiveOps.Api` / `HiveOps.Workers`): `HiveArena:Publisher:Enabled` publishes a validation request after each auto-fix commit; `RepoUrl` names the repository under validation (single-repo MVP — per-tenant repo URLs are a later phase).

**Code-fix workspace:** in `SelfHosted` mode the worker clones `runtimeConfiguration.deployGit.gitRepositoryUrl` per tenant into `Git:WorkspacesRoot`; in other modes it falls back to `Git:RepoPath` (e.g. a sandbox clone at `c:/temp/hiveops-work`). Git push uses the machine's git credentials (credential manager); workflow dispatch uses `DeployGit:ApiToken` via env var `DeployGit__ApiToken`.

**Validation loop:** a `Defeat`/`Error` callback reopens the incident to `Open`+`bot` (the arena report stays in the message history as evidence for the next fix attempt); after 3 failed validations the incident escalates to `engineer` instead of looping.

---

## AI Support Engineer layer (root cause, playbooks, snapshots, audit)

Operational surface added on top of the auto-workflow (plan: [`docs/plans/ai-support-engineer-master-plan.md`](plans/ai-support-engineer-master-plan.md)). Requires migration [`db/V1.2__AiSupportEngineerFoundation.sql`](../db/V1.2__AiSupportEngineerFoundation.sql) (idempotent; already listed in `db/manifest.order.txt`).

| Endpoint | Description |
|----------|-------------|
| `GET /api/support/playbooks` | List the effective tenant's playbooks |
| `POST /api/support/playbooks` | Create a playbook (`SqlTemplate` \| `HttpWebhook` \| `StoredProcedure`) |
| `PUT /api/support/playbooks/{id}` / `DELETE /api/support/playbooks/{id}` | Update / deactivate (`IsActive = false`) |
| `POST /api/support/playbooks/{id}/dry-run` | Validate parameters + transactional dry-run without committing |
| `GET /api/support/incidents/{id}/snapshots` | Row-level before/after JSON snapshots for an incident's remediations |
| `POST /api/support/incidents/{id}/snapshots/{snapshotId}/revert` | One-click undo: executes the inverse SQL by primary key |
| `GET /api/support/incidents/{id}/audit-chain` | SHA-256 hash-chained audit entries + `VerifyChainAsync` integrity result |
| `GET /api/support/readiness/check` | One-call tenant connectivity check (DB + Git + KB + channels) |
| `GET /api/support/readiness/outcomes` | Verified autonomous-resolution counts per tenant (billing basis) |

**Processing order inside `IncidentAutoWorkflowService`:** triage → multi-source root-cause investigation (schema + diagnostic SELECTs + app logs + `git log`) → if the hypothesis recommends a matched playbook, it executes (respecting `RequiresApproval`) before any free-form SQL/code generation → the incident's `VerificationQuery` re-runs post-fix and only `RowCount == 0` sets `VerificationPassed = true`, which is what outcome metering counts.

**Safety notes:** playbook `SqlTemplate` parameters bind via real `SqlParameter`s (never string interpolation); dry-run enforces `MaxAffectedRows` inside a rolled-back transaction; every step appends to the incident audit ledger, so the chain can be presented as an evidence pack.

---

## Ephemeral CI (GitHub Actions)

Workflow: [`.github/workflows/preview.yml`](../.github/workflows/preview.yml).

- **Runner:** `ubuntu-22.04` (aligned with Microsoft `packages-microsoft-prod.deb` for `sqlcmd` / `mssql-tools18`).
- **Secret:** create **`HIVEOPS_CI_SQL_PASSWORD`** under *Settings → Secrets and variables → Actions* (strong password; SQL Server complexity rules apply). The same value is used for `MSSQL_SA_PASSWORD`, bootstrap (`HIVEOPS_SQL_PASSWORD`), and the `HIVEOPS_EPHEMERAL_SQL` connection string.
- Starts SQL Server and Redis service containers, installs `sqlcmd`, runs `db/bootstrap.sh`, builds the solution, and runs tests with filter `Category=EphemeralSmoke`.
- Optional: **`HIVEOPS_EPHEMERAL_ADMIN_KEY`** overrides the default admin API key used by [`EphemeralSqlWebApplicationFactory`](../tests/HiveOps.IntegrationTests/EphemeralSqlWebApplicationFactory.cs) during smoke tests.

---

## Database bootstrap

Ordered scripts are listed in [`db/manifest.order.txt`](../db/manifest.order.txt). The bootstrap scripts execute each file in order via `sqlcmd`.

The first script, [`db/create_hive_support_bot.sql`](../db/create_hive_support_bot.sql), **drops and recreates** the `Hive` database. Do not run bootstrap against a production instance unless that is intentional.

Prerequisites: **sqlcmd** (Microsoft SQL Server Command Line Utilities 18) and ODBC Driver 18 for SQL Server.

---

## Backup and restore (minimal)

**Backup** (PowerShell example; set `HIVEOPS_SQL_PASSWORD` first):

```powershell
.\db\backup.ps1 -BackupPath C:\Backups\hive-full.bak
```

**Restore** (destructive for the `Hive` database):

```powershell
.\db\restore.ps1 -BackupPath C:\Backups\hive-full.bak
```

After restore, restart the API and verify `/health/ready`.

---

## Smoke test API key (bootstrap script)

The script [`db/create_hive_support_bot.sql`](../db/create_hive_support_bot.sql) creates a tenant with API key **`deportes-api-key-12345678`** (see that file for the exact seed). Ephemeral smoke tests use this key against `/api/inbox/conversations`.
