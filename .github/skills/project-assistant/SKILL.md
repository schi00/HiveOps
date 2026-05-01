---
name: project-assistant
description: "Use when: you need project-specific assistance — code reviews, test creation, PR writing, refactors. Keywords: code-review, tests, PR, refactor, conventions."
---

Project Assistant

Summary
- Provides repository-focused code reviews, test suggestions, PR description templates, and refactor recommendations.

How to use
- Trigger: include trigger phrases in the description (e.g., "code-review", "tests", "PR") or call the chat slash command.
- Provide context: files changed, code snippets, or PR link.

Usage examples
- "Use when: review PR modifying src/api/** — summarize issues, prioritize failures, and propose fixes."
- "Use when: add tests for src/module.py — generate test skeleton with common cases and edge cases."

Notes and best practices
- The description uses the "Use when:" pattern for discoverability.
- Avoid global `applyTo` unless the instruction applies to all files.
- If you want strict rules (hooks, tool restrictions, or specific globs), request an update.
