# HiveOps workspace instructions

## Objective

- Keep changes production-oriented for a multi-tenant SaaS bot in **.NET 9**.
- Prefer small, focused edits that preserve existing behavior unless a change is explicitly requested.

## Global priorities

- Protect tenant isolation first.
- Do not hardcode secrets or credentials in source files.
- Keep API contracts stable when possible.
- Add or update tests when changing business logic.

## Deployment modes

- **`HiveOps:Deployment:Mode`**: `SaaS` | `SelfHosted` | `Ephemeral` (see [`docs/operations.md`](docs/operations.md)).
- **`HiveOps:Deployment:Billing`**: `None` skips Stripe; `Stripe` requires configuration and optional SDK build flag.
- **`HiveOps:Deployment:FailClosedTenantConnectionInSelfHosted`**: optional strict mode for dedicated DB per tenant (throws instead of defaulting to `DefaultConnection` when per-tenant encrypted connection string is missing or resolver cache is cold).

## Working rules

- Prefer project-level commands from repository root.
- Validate with targeted tests before proposing broad test runs.
- If touching more than one layer, document assumptions in PR notes or commit message.

## Repository context

- API entry point: [`src/HiveOps.Api`](src/HiveOps.Api)
- Domain logic / agents: [`src/HiveOps.Agents`](src/HiveOps.Agents)
- Application layer: [`src/HiveOps.Application`](src/HiveOps.Application)
- Persistence and integrations: [`src/HiveOps.Infrastructure`](src/HiveOps.Infrastructure)
- Automated tests: [`tests`](tests)
- SQL bootstrap scripts: [`db`](db)
- Docker / Compose: [`deploy`](deploy)
- Operations runbook: [`docs/operations.md`](docs/operations.md)

## Definition of done

- Build succeeds for impacted projects.
- Relevant tests pass.
- No accidental changes to generated artifacts or local-only files.
