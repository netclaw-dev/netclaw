## Why

The eval harness must tell a model failure from an infrastructure failure. A run of `netclaw chat -p` could not do this. The session actor gave a provider outage and a model that ignores a runtime stop the same category. The CLI kept no category.

## What Changes

- Add `ErrorCategory.ModelBehavior`. The session actor uses it for a repeated tool call after the runtime refused that call (the terminal cycle stop) and for repeated empty replies that end with the finish reason Stop or Length. Repeated empty replies with no finish reason are `StreamFailure`. With another finish reason, such as `content_filter`, they are `ProviderFailure`.
- Print `[turn:failed] category=<model|infra> kind=<Kind>` on stderr for a failed turn or a failed run.
- Write a `TURN_FAILED: category=... kind=...` record to the session log before `TURN_COMPLETED`.
- Add `errorCategory` and `errorKind` to the `--json` envelope of a failed turn.
- Map only `ModelBehavior` to `model`. Map every other kind, and a failed turn with no error, to `infra`.
- **BREAKING**: A cancelled run (SIGINT or SIGTERM) now exits 1 with `kind=Cancelled`. Before, it exited 0 with no output.
- Each terminal path sets the exit code first, then stops the host.
- The CLI replaces line breaks in error text on stderr, so error text cannot forge a token line.

Out of scope: the harness verdict mapping, sub-agent failures, and the Slack and Discord text for `ModelBehavior`.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-cli`: Add the requirement that a failed headless turn reports its error category.

## Impact

- `LlmSessionActor`, `SessionProtocol.ErrorCategory`, and `HeadlessChannel`.
- The wire does not change. The DTO carries the category name. An older CLI reads the new name as `Unknown`, which is `infra`.
- Documentation: `docs/spec/SPEC-004-cli-contract.md` and the `netclaw-operations` system skill.
