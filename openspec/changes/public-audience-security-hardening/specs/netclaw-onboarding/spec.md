## MODIFIED Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`. The onboarding
wizard SHALL collect Slack credentials, provider configuration, ACL inputs,
search backend, browser automation, memory provider selection, MCP server
configuration, and exposure mode selection. On completion, the wizard SHALL
run a health check to verify the baseline configuration is functional.

The wizard SHALL NOT write `AGENTS.md` to disk during identity file
generation. AGENTS.md is binary-controlled firmware loaded from embedded
resources at runtime. The wizard SHALL continue to write `SOUL.md` and
`TOOLING.md` as operator-mutable identity files.

#### Scenario: First-time setup

- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** guided setup collects provider, Slack, ACL, search, browser
  automation, memory, and exposure mode inputs
- **AND** writes a runnable baseline configuration
- **AND** writes SOUL.md and TOOLING.md to `~/.netclaw/identity/`
- **AND** does NOT write AGENTS.md (or writes a reference-only stub)

#### Scenario: Identity files written on completion

- **WHEN** the wizard completes and writes config
- **THEN** `SOUL.md` is written from the embedded SOUL template
- **AND** `TOOLING.md` is written from the embedded TOOLING template
- **AND** `AGENTS.md` is NOT written from a template
