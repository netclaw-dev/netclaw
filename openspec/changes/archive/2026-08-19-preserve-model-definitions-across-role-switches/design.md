## Context

Model metadata is currently embedded in three runtime role entries. Those entries are both the operator's durable configuration and the runtime consumer shape, so assigning a different model destroys metadata belonging to the previous model. Existing deployments and the current stable Docker image use this legacy shape.

## Goals / Non-Goals

**Goals:**

- Store model-owned metadata once in named definitions and make roles reference definitions.
- Keep manual JSON editing obvious: property absence means runtime detection, with no tombstones.
- Run legacy configuration without an eager write, and migrate deterministically on explicit mutation/fix.
- Resolve and validate references before persistence and runtime client construction.

**Non-Goals:**

- Downgrade compatibility after the configuration has been migrated.
- Automatic conflict resolution or model-definition garbage collection.
- Changes to provider discovery or actor/persistence protocols.

## Decisions

### New canonical shape

`Models.Definitions` is a dictionary of operator-chosen names to complete `ModelReference` values. `Models.Roles` contains `Main`, `Fallback`, and `Compaction` definition-name references. Runtime code receives the existing resolved `ModelSelection`, keeping actor and chat-client boundaries unchanged.

This is preferred over a hidden metadata cache or role-entry tombstones because it gives manual editors one visible source of truth and preserves property absence as runtime detection.

### Dual-shape reader, single-shape writer

A shared configuration resolver accepts either the complete legacy inline shape or the complete named shape. Mixed shapes, missing definitions, duplicate/invalid names, and conflicting migration candidates fail loudly. Daemon startup reads legacy configuration without rewriting it. CLI/TUI writes and `doctor --fix` migrate legacy input atomically before applying the requested mutation.

The schema accepts both complete shapes during the compatibility window. New writers emit only the named shape.

### Deterministic legacy migration

Each distinct case-insensitive `(Provider, ModelId)` becomes one definition. A deterministic slug is derived from provider and model ID, with a stable numeric suffix for name collisions. When multiple legacy roles identify the same model, their optional metadata must agree; otherwise migration fails with the conflicting role names and fields.

### Upgrade smoke

The smoke harness creates a disposable directory/volume, runs the latest stable image to produce or consume a legacy configuration, stops it, builds a uniquely tagged local image, and starts that image against the same isolated volume. Assertions verify startup, legacy resolution, explicit migration, and preservation after role switching. Cleanup removes only resources carrying the test's unique label/name.

## Risks / Trade-offs

- **Older binaries cannot read the named shape after migration** → document that rollback requires restoring the pre-migration backup; migration writes atomically and retains a backup.
- **Dual-shape support can become permanent complexity** → centralize it in one resolver and have every writer emit only the canonical shape.
- **Conflicting legacy roles could be silently merged** → reject conflicts and report exact fields/roles.
- **Docker smoke could touch operator state** → require an absolute temporary path created by the harness and unique container/image names; never use default Netclaw volumes.
- **Stable image availability/network failures** → make the Docker upgrade scenario explicit and fail with actionable diagnostics; unit migration fixtures remain mandatory offline proof.

## Migration Plan

1. Ship a reader that supports both shapes and schema validation for both.
2. Verify an untouched legacy stable configuration starts with the new daemon.
3. On the first explicit model/config write or `doctor --fix`, validate, back up, migrate, re-resolve, then atomically persist.
4. Document rollback as restoring the generated legacy backup before running an older image.

## Open Questions

- The exact stable tag is resolved from the release manifest at smoke execution time rather than hard-coded.
