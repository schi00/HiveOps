# HiveArena — proving ground (simulation & self-healing validation)

HiveArena is a **separate deployable** (`src/HiveArena`) that complements HiveOps:

- **HiveOps (Strategist)**: detects incidents, proposes fixes, commits to a branch, optionally publishes `HiveArenaValidationRequestedMessage` to HiveArena (`HiveArena:Publisher` in HiveOps).
- **HiveArena (Proving ground)**: runs **deterministic scenario replay** inside an **ephemeral environment stub** (Docker/K8s orchestration is a later phase), produces **Survival / Defeat / Error** outcomes, and can **callback** to HiveOps with an LLM-readable log.

## Projects

| Project | Role |
|--------|------|
| `HiveArena.Core` | Contracts: scenarios, validation request/completed, bridge payload shapes. |
| `HiveArena.Infrastructure` | Scenario catalog, HiveOps incident bridge, ephemeral env stub, branch checkout stub, replay runner, survival reporter, HiveOps callback HTTP client. |
| `HiveArena.Api` | ASP.NET Core host: `POST /api/hivearena/scenarios/run`, `POST /api/hivearena/validation/requests`, botonera UI. |

## Configuration (HiveArena.Api)

- `HiveArena:Host:ApiKey` — optional ingress protection (`X-HiveArena-Key`).
- `HiveArena:HiveOpsBridge:*` — base URL + `X-Dev-Key` for `POST /api/support/incidents/from-sandpit`.
- `HiveArena:HiveOpsCallback:*` — optional `POST {BaseUrl}/api/internal/hivearena/validation-completed` with `X-HiveArena-Key: {SharedSecret}`.

## Configuration (HiveOps.Api / Workers)

- `HiveArena:Publisher:Enabled` — publish validation requests after auto-fix commit.
- `HiveArena:Publisher:HiveArenaBaseUrl` — e.g. `https://localhost:5010`.
- `HiveArena:Publisher:HiveArenaApiKey` — must match HiveArena `Host:ApiKey` when set.
- `HiveOps:HiveArenaInbound:SharedSecret` — must match HiveArena `HiveOpsCallback:SharedSecret` for callbacks.

## Transport note

Cross-service flow uses **HTTP today** as an incremental step toward **durable outbox/inbox** described in the master plan. MediatR handlers remain **in-process per service**; only the transport is remote.

## Sandpit

`HiveOps.Sandpit.Ui` remains for legacy local DB fault injection. New simulation UX should target **HiveArena** (`HiveArena.Api`).
