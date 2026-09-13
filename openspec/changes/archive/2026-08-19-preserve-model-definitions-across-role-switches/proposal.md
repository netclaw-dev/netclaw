## Why

PRD-004 and PRD-005 allow operators to select models and override provider-reported capabilities, but the current `Models.Main` / `Fallback` / `Compaction` entries combine role assignment with model-owned metadata. Switching a role therefore destroys manually maintained context-window and modality overrides, especially for vLLM deployments that cannot report modalities.

## What Changes

- Add human-readable named model definitions whose metadata is independent of role assignment.
- Make model roles reference named definitions, so switching roles does not rewrite a definition.
- Continue accepting the existing inline role shape on upgrade and provide deterministic migration to the named shape.
- Reject ambiguous mixed or conflicting configuration instead of silently choosing a representation.
- Add isolated stable-container to locally-built-container upgrade smoke coverage using a disposable volume.
- Preserve absence of optional metadata as runtime detection; no hidden tombstone values are introduced.

In scope: configuration binding, CLI/TUI model assignment, schema, doctor/migration behavior, operational guidance, automated compatibility proof, and Docker upgrade smoke coverage.

Out of scope: automatic model discovery beyond existing probes, changing provider APIs, and supporting downgrade from the new shape to an older Netclaw binary.

## Capabilities

### New Capabilities

- `named-model-definitions`: Model-owned definitions, role references, legacy resolution, migration, and conflict behavior.

### Modified Capabilities

- `netclaw-model-providers`: Primary, fallback, and compaction assignments reference persistent model definitions.
- `netclaw-cli`: Model commands and TUI preserve model metadata across role switches and expose migration failures.
- `netclaw-testing`: Upgrade compatibility is proven with legacy configuration and an isolated container-volume smoke.

## Impact

Affected areas include `Netclaw.Configuration` model types and schema, daemon/CLI configuration binding, model CLI and TUI persistence, provider rename behavior, doctor repair, system operational guidance, and smoke tooling. Startup remains fail-closed for invalid references. Existing inline deployments remain readable and runnable without an eager startup rewrite.

Security impact is limited to configuration integrity: unresolved or conflicting role references fail before persistence or runtime client construction. Operationally, configuration is migrated only by an explicit writing/fix operation, and the upgrade smoke never mounts the operator's real Netclaw home.
