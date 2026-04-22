---
name: semantic-kernel-skills
description: Semantic Kernel Skills
---

## Semantic Kernel Skills
- **Annotation:** Every skill method must have `[KernelFunction]` and a detailed `[Description]` in English for the LLM to understand its purpose.
- **Statelessness:** Plugins should be stateless; rely on injected services from `SaaSBot.Infrastructure` for data persistence.
- **Multi-tenancy:** Ensure every database skill filters queries by `TenantId`. Never perform a broad `SELECT` without a tenant filter.
- **Logging:** Use `ILogger` within skills to trace agent execution steps, especially when interacting with the VadiSuite SQL database.
- **Error Handling:** Return structured error messages in JSON format when database operations fail, including the error type and a brief description.
- **Documentation:** Document each skill's parameters and return values in the `[Description]` attribute, including expected data types and constraints.
