# HiveOps Secrets Mapping and Local Overrides

This document explains how secrets are resolved and how to configure them locally without AWS.

## Resolution Order (Fallback)
1. Environment variables (double underscore supported):
   - Example: `SemanticKernel__OpenRouter__ApiKey`
2. AWS Secrets Manager (enabled when `Secrets:UseAws=true`)
3. appsettings.json / appsettings.Development.json (values should remain empty in repo)

## Keys and Suggested AWS Paths
- `SemanticKernel:OpenRouter:ApiKey`
  - AWS: `/hiveops/api/{env}/SemanticKernel/OpenRouter/ApiKey`
- `SemanticKernel:Embeddings:ApiKey`
  - AWS: `/hiveops/api/{env}/SemanticKernel/Embeddings/ApiKey`
- `WhatsApp:ApiKey`
  - AWS: `/hiveops/api/{env}/WhatsApp/ApiKey`
- `WhatsApp:AppSecret`
  - AWS: `/hiveops/api/{env}/WhatsApp/AppSecret`
- `Admin:ApiKey`
  - AWS: `/hiveops/api/{env}/Admin/ApiKey`
- `Email:Password`
  - AWS: `/hiveops/api/{env}/Email/Password`
- `Ci:HmacSecret` (for CI callbacks)
  - AWS: `/hiveops/api/{env}/Ci/HmacSecret`

Replace `{env}` with `dev`, `staging`, or `prod`.

## Local Development (No AWS)
- Ensure `Secrets:UseAws=false` in `appsettings.Development.json` (default).
- Set environment variables for any needed secrets:
  - PowerShell:
    - `$env:SemanticKernel__OpenRouter__ApiKey = 'sk-...'`
    - `$env:WhatsApp__AppSecret = '...'
    - `$env:Ci__HmacSecret = 'test-hmac'`

## Verifying Secret Sources (Debug/Admin)
- In Development, as Admin, call:
  - `GET /api/admin/tenants/secrets/source`
- The endpoint returns the source for selected keys (Vault/Env/Config/None). It never returns secret values.

## Notes
- Do not commit real secrets to the repository.
- Rotation: Prefer AWS Secrets Manager rotation for tokens/keys where applicable.
