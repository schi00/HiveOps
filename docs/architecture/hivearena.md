# HiveArena — proving ground (simulation & self-healing validation)

HiveArena is a **separate deployable** (`src/HiveArena`) that complements HiveOps:

- **HiveOps (Strategist)**: detects incidents, proposes fixes, commits to a branch, optionally publishes `HiveArenaValidationRequestedMessage` to HiveArena (`HiveArena:Publisher` in HiveOps).
- **HiveArena (Proving ground)**: clones the proposed branch into an **ephemeral filesystem environment** (isolated temp root, destroyed after the run; Docker/K8s orchestration is a later phase), runs a real **build + test replay** (`dotnet build`, then `dotnet test` per test project), produces **Survival / Defeat / Error** outcomes, and can **callback** to HiveOps with an LLM-readable log including the failing `dotnet` output tail.

## Projects

| Project | Role |
|--------|------|
| `HiveArena.Core` | Contracts: scenarios, validation request/completed, bridge payload shapes. |
| `HiveArena.Infrastructure` | Scenario catalog, HiveOps incident bridge, ephemeral environment manager, real git branch checkout, build/test replay runner, survival reporter, HiveOps callback HTTP client. |
| `HiveArena.Api` | ASP.NET Core host: `POST /api/hivearena/scenarios/run`, `POST /api/hivearena/validation/requests`, botonera UI. |

## Configuration (HiveArena.Api)

- `HiveArena:Host:ApiKey` — optional ingress protection (`X-HiveArena-Key`).
- `HiveArena:HiveOpsBridge:*` — base URL + `X-Dev-Key` for `POST /api/support/incidents/from-sandpit`.
- `HiveArena:HiveOpsCallback:*` — optional `POST {BaseUrl}/api/internal/hivearena/validation-completed` with `X-HiveArena-Key: {SharedSecret}`.
- `HiveArena:ProvingGround:WorkspacesRoot` — root for ephemeral env dirs (defaults to `%TEMP%/hivearena`).
- `HiveArena:ProvingGround:GitTimeoutSeconds` / `BuildTimeoutSeconds` / `TestTimeoutSeconds` — defaults 240/600/900.
- `HiveArena:ProvingGround:RunTestsWhenPresent` — `false` makes the build gate the only check.
- `HiveArena:ProvingGround:ExcludedTestProjectPatterns` — project name fragments to skip (e.g. `["IntegrationTests"]` when the environment lacks their external dependencies such as a live database).

## Configuration (HiveOps.Api / Workers)

- `HiveArena:Publisher:Enabled` — publish validation requests after auto-fix commit.
- `HiveArena:Publisher:HiveArenaBaseUrl` — e.g. `https://localhost:5010`.
- `HiveArena:Publisher:HiveArenaApiKey` — must match HiveArena `Host:ApiKey` when set.
- `HiveOps:HiveArenaInbound:SharedSecret` — must match HiveArena `HiveOpsCallback:SharedSecret` for callbacks.

## Transport note

Cross-service flow uses **HTTP today** as an incremental step toward **durable outbox/inbox** described in the master plan. MediatR handlers remain **in-process per service**; only the transport is remote.

## Sandpit

`HiveOps.Sandpit.Ui` remains for legacy local DB fault injection. New simulation UX should target **HiveArena** (`HiveArena.Api`).
