# Agents Layer Instructions

## Scope
Applies to src/SaaSBot.Agents.

## Orchestration Guidelines
- Keep routing and intent handling deterministic and debuggable.
- Preserve conversation state transitions and handoff thresholds.
- Avoid hidden side effects in plugins.

## Plugin Design
- Keep plugin functions small and single-purpose.
- Return concise user-facing text; keep formatting predictable.
- Prefer explicit dependency injection over static helpers.

## Reliability
- On failures, keep fallback behavior user-safe and actionable.
- Ensure supervision escalation paths remain functional.
