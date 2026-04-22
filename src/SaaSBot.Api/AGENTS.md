# API Instructions

## Scope
Applies to src/SaaSBot.Api.

## Endpoint and Middleware Rules
- Preserve middleware order unless the change explicitly targets pipeline behavior.
- New endpoints must define auth requirements and expected tenant resolution mechanism.
- Prefer clear HTTP status codes and consistent error payloads.

## Auth and Security
- Do not log credentials, tokens, or API keys.
- Keep cookie and role behavior backward compatible unless requested.
- Validate input models defensively.

## Operational Readiness
- Keep health check endpoint working.
- Keep launch settings and appsettings usable for local development.
- If webhook behavior changes, document payload assumptions in controller comments.
