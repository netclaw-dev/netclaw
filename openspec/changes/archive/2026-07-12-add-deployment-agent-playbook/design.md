## Context

Netclaw currently composes `SOUL.md` and `TOOLING.md` from disk with an audience-specific embedded `AGENTS.md`. `NetclawPaths.AgentsPath` already identifies `~/.netclaw/identity/AGENTS.md`, but production prompt assembly does not read it. Sub-agents obtain operating rules through `ISystemPromptProvider.GetOperatingRules`, so the provider is the existing cross-boundary seam for consistent inheritance.

The session actor rebuilds its system prompt before each inbound turn. No new actor message, persisted state, constructor dependency, configuration property, or restart mechanism is required.

## Goals / Non-Goals

**Goals:**

- Give operators one durable deployment mission/playbook that applies to main agents and sub-agents.
- Keep Netclaw's embedded machinery rules distinct and earlier in the prompt.
- Seed and conversationally refine the playbook without overwriting operator content.
- Prove behavior with deterministic prompt contracts and focused Spark2 evals.

**Non-Goals:**

- Audience-specific playbook variants.
- Prompt text as a replacement for ACL, approval, or tool-policy enforcement.
- Automatic rewriting of existing customized playbooks.
- New configuration or persistence models.

## Decisions

### Compose through the existing prompt-provider seam

`FileSystemPromptProvider` will compose the embedded audience rules and disk playbook once, then use that result from both `GetSystemPrompt` and `GetOperatingRules`. This reuses the value already flowing through sub-agent creation and avoids parallel state or new actor plumbing.

The composed order is embedded operating core followed by a labeled deployment playbook. Personal and Team use the full embedded core; Public uses the stripped embedded core. All three append the same deployment playbook because it describes the deployed function, not audience-private data.

Alternative considered: append the file only in the main session and separately inject it into sub-agents. Rejected because two assembly paths can drift.

### Treat the playbook as trusted guidance, not enforcement

The embedded core will explicitly state the hierarchy, but prompt ordering cannot guarantee that a model ignores conflicting later text. Runtime ACL and tool-policy checks remain authoritative. Documentation will prohibit secrets and audience-private data because Public sessions receive the same mission.

Alternative considered: suppress deployment guidance for Public. Rejected because outward-facing agents still need the same mission and quality process.

### Seed a minimal scaffold and refine it in post-init chat

Fresh init writes a concise mission scaffold only when `AGENTS.md` is absent. The existing post-init conversation will separately gather operator context for `SOUL.md` and mission/workflow guidance for `AGENTS.md`, propose a playbook, and request confirmation before writing. Existing files are never overwritten by seeding or identity redo.

Alternative considered: add mission questions to the deterministic TUI wizard. Rejected because mission discovery is nuanced and the existing chat bootstrap is designed for conversational enrichment.

### Keep live-refresh behavior

No file watcher or restart is added. The session actor already reads fresh prompt layers before each inbound turn, so a confirmed playbook edit applies on the next message in the same session. Sub-agents spawned after that turn receive the refreshed content.

### Separate deterministic proof from behavioral evals

Unit and actor integration tests prove exact composition and inheritance. Two Spark2 cases prove that an unprompted mission playbook changes main-agent behavior and delegated behavior. The eval harness receives a small purpose-built fixture instead of the obsolete full embedded-rules copy.

## Risks / Trade-offs

- **Conflicting operator guidance may influence the model** → label hierarchy explicitly and rely on runtime policy for hard security.
- **One playbook can leak its own contents to Public conversations** → document that it must contain durable workflow guidance only, never secrets or private data.
- **Legacy files may contain older machinery rules** → preserve them rather than guessing which user edits are safe to remove; replace only the shipped template and eval fixture.
- **Unreadable files could silently remove mission guidance** → missing is supported, but unexpected filesystem failures must be surfaced rather than substituted.
- **Behavioral evals can be model-variable** → assert observable skill/tool/output outcomes over five Spark2 runs with the established 0.80 threshold; keep inheritance correctness deterministic.

## Migration Plan

1. Ship the new prompt composer and minimal init template.
2. Existing installs with no disk playbook continue using embedded rules until an operator creates one.
3. Existing disk files are loaded as authored and never rewritten automatically.
4. Rollback restores embedded-only prompt behavior; files remain on disk for a later upgrade.

## Open Questions

None.
