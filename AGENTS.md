# SaaSBot Workspace Instructions

## Objective
- Keep changes production-oriented for a multi-tenant SaaS bot in .NET 9.
- Prefer small, focused edits that preserve existing behavior unless a change is explicitly requested.

## Global Priorities
- Protect tenant isolation first.
- Do not hardcode secrets or credentials in source files.
- Keep API contracts stable when possible.
- Add or update tests when changing business logic.

## Working Rules
- Prefer project-level commands from repository root.
- Validate with targeted tests before proposing broad test runs.
- If touching more than one layer, document assumptions in PR notes or commit message.

## Repository Context
- API entry point: src/SaaSBot.Api
- Domain logic orchestration: src/SaaSBot.Agents
- Persistence and integrations: src/SaaSBot.Infrastructure
- Automated tests: tests
- SQL bootstrap scripts: db

## Definition of Done
- Build succeeds for impacted projects.
- Relevant tests pass.
- No accidental changes to generated artifacts or local-only files.
