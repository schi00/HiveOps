# Database Script Instructions

## Scope
Applies to db.

## SQL Script Rules
- Keep scripts idempotent whenever feasible.
- Avoid destructive operations unless explicitly requested.
- Preserve tenant data safety and row-level security assumptions.

## Schema Evolution
- Prefer additive changes and guarded ALTER statements.
- Index changes should be justified by query patterns.
- Keep defaults and constraints explicit.

## Seed Data
- Keep seed data realistic and consistent with current domain model.
- Do not include real secrets or personal data.
