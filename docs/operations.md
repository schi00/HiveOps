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
