# OpenProse Workflows

Automated multi-agent workflows for Netclaw, written in [OpenProse](https://prose.md).

## Available Workflows

### `security-audit.prose` -- Security Audit

Runs a comprehensive security audit against the Netclaw codebase using 4 specialized agents and 8 parallel scan categories.

**Frameworks:** OWASP Top 10 for LLM Applications (2025), Netclaw SEC-001 through SEC-009 (PRD-002)

**Usage:**

```bash
prose run .prose/security-audit.prose
```

To focus on a specific area:

```bash
prose run .prose/security-audit.prose --input focus_area=prompt-injection
```

**Focus areas:** `all` (default), `prompt-injection`, `tool-grants`, `acl`, `shell-path`, `credentials`, `self-modification`, `exposure`, `webhooks`

**What it does:**

1. **Phase 1 -- Scan:** 8 parallel scans covering prompt injection resistance, tool grant permissiveness, ACL policy completeness, shell/path policy coverage, credential exposure, self-modification boundaries, exposure mode safety, and webhook/persistence safety.
2. **Phase 2 -- Critique:** Validates findings against Netclaw's 13 built-in defense layers to filter false positives and adjust severities.
3. **Phase 3 -- Fix:** Produces before/after code proposals for each confirmed vulnerability.
4. **Phase 4 -- Report:** Generates a structured report with OWASP LLM Top 10 coverage matrix, SEC control compliance, defense-in-depth assessment, and maturity model evaluation.

## Directory Structure

- `*.prose` -- Workflow source files (checked in)
- `runs/` -- Runtime output from workflow executions (git-ignored)
