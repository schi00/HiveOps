# Infrastructure Layer Instructions

## Scope
Applies to src/SaaSBot.Infrastructure.

## Data and Persistence
- Keep EF Core configuration aligned with SQL schema expectations.
- Any query filter, interceptor, or tenant context change requires extra caution.
- Preserve compatibility of vector search related SQL behavior.

## Integrations
- External service adapters should be replaceable through interfaces.
- Fail gracefully when optional AI or embedding services are unavailable.
- Keep caching defaults safe for local development.

## Dependency Injection
- Register services with clear lifetime choices.
- Avoid hidden service locator patterns.
