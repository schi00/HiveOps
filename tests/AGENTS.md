# Test Layer Instructions

## Scope
Applies to tests.

## Test Strategy
- Prefer fast, deterministic tests.
- Add unit tests for plugin and routing logic changes.
- Add integration tests for API auth, tenant isolation, and webhook flows when behavior changes.

## Test Quality
- Use explicit Arrange-Act-Assert structure.
- Keep test names behavior-oriented.
- Avoid brittle assertions based on incidental text unless intentional.

## Execution
- Run only impacted test projects first.
- Expand to broader runs if shared infrastructure or cross-cutting behavior changed.
