# Source Layer Instructions

## Scope
Applies to all code under src.

## Coding Expectations
- Follow existing C# style and nullable reference type usage.
- Keep dependency injection registrations explicit and readable.
- Prefer asynchronous APIs with cancellation token propagation.

## Multi-Tenant Safety
- Any data access path must remain tenant-aware.
- Avoid bypassing query filters unless explicitly required and justified.
- When adding endpoints, verify tenant resolution path is clear.

## Change Discipline
- Keep cross-project references minimal.
- If modifying shared models, check API, agents, and infrastructure impact.
