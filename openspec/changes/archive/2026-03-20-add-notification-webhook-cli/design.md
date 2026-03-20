## Context

Netclaw already has an outbound operational notification path and a hardened
configuration contract for webhook targets, but operators still manage that
array by editing JSON directly. The CLI already follows a repo pattern for this
kind of offline configuration management: `provider`, `mcp`, and related
commands mutate `netclaw.json` and `secrets.json` directly, while daemon-only
features are routed over SignalR or HTTP.

This change stays in that same offline-management lane. It does not change actor
boundaries, session routing, persistence schemas, or daemon startup behavior.
The only runtime-facing behavior is an explicit operator-invoked webhook probe.

Source PRDs: `PRD-001` (primary), `PRD-002`, `PRD-004`

## Goals / Non-Goals

**Goals:**
- Add an offline plain-CLI surface for listing, adding, removing, and testing
  notification webhook targets.
- Reuse the existing notification validation rules so bad config is rejected
  before files are written or probes are sent.
- Keep secret-bearing webhook URLs and headers out of normal CLI output and out
  of `netclaw.json`.
- Preserve fail-closed behavior while giving operators remediation-first errors.

**Non-Goals:**
- No new daemon API, actor messages, or persistence changes.
- No TUI workflow for notification webhook management.
- No hot reload of notification config into the running daemon.
- No richer notification payload templating, preview rendering, or routing.

## Decisions

### Decision: Add an offline `netclaw notification webhook` command group

The command surface will be plain CLI and offline, following the same style as
`netclaw provider` and `netclaw mcp`. The subcommands are `list`, `add`,
`remove`, and `test`.

Targets are stored as an array, not a keyed object, so the CLI needs two stable
selection modes: explicit `Name` when present and zero-based `index` for every
entry. `list` will always print the index, optional name, a redacted URL display,
and whether headers are configured, but never full webhook URLs or header
values.

Rationale: keeping the workflow offline avoids coupling a config-management task
to daemon liveness, and index-or-name addressing works with both legacy entries
and newly added named entries.

Alternative considered: add daemon-only notification management endpoints.
Rejected because the feature is fundamentally local config management and should
work before the daemon is healthy.

### Decision: Stage config mutations in memory and prefer orphaned secrets over broken config

The CLI will load `netclaw.json` and `secrets.json`, build an in-memory mutated
view, validate the resulting notification config, and only then write files.
Webhook URLs and header values will be written to `secrets.json`; the base config
will contain only non-secret target properties such as `Name`.

Write ordering will bias toward safety:
- Add/update-style writes store secrets first, then base config.
- Remove writes remove base config first, then delete matching secrets.

Rationale: if a write fails mid-operation, it is safer to leave behind an
unused secret than to persist a live webhook target whose required secret is
missing.

Alternative considered: write `netclaw.json` first for every operation.
Rejected because it can leave a configured target in place without its secret
overlay.

### Decision: CLI reads merged state and normalizes legacy base-config secrets

The CLI will read both base config and secrets overlay into a merged in-memory
view for list/remove/test behavior. If a legacy hand-edited config still stores
webhook URLs or headers in `netclaw.json`, CLI-managed operations normalize that
state by moving those values into `secrets.json` and removing them from base
config.

Rationale: operators need stable behavior even when starting from older or
hand-edited configs. Automatic normalization avoids preserving an insecure layout
once the CLI has touched the feature.

Alternative considered: fail on legacy layout and require manual cleanup first.
Rejected because it creates unnecessary friction during incident-time use.

### Decision: Reuse `NotificationConfigValidator` for both writes and probe setup

The CLI will synthesize the post-mutation notification config and validate it
using the shared validator already used by daemon startup and `netclaw doctor`.
The `test` command will also validate the selected target before sending a
probe.

Rationale: this keeps the write path, startup path, and diagnostics path aligned
and prevents a new command from becoming a bypass around the hardened config
rules.

Alternative considered: lightweight argument validation in the CLI only.
Rejected because it would drift from daemon behavior and weaken fail-closed
guarantees.

### Decision: `test` sends a single bounded HTTP probe directly from the CLI

The probe command will send one explicit HTTP POST from the CLI using the
selected target's effective URL, headers, and timeout settings. It will not use
the daemon, notification background service, retries, or deduplication.

The command will report safe diagnostics only: target identity, HTTP status,
timeout, and a trimmed response snippet when available. Secret values and full
webhook paths remain redacted.

Rationale: an operator-invoked probe should be deterministic and easy to reason
about. Reusing runtime retry behavior would add duplicate requests and blur the
difference between configuration testing and real notification delivery.

Alternative considered: route the probe through `WebhookNotificationService`.
Rejected because that would require daemon coupling and would test more runtime
plumbing than the operator is trying to verify.

## Risks / Trade-offs

- [Risk] Name-based targeting can be ambiguous if multiple entries share the
  same optional `Name`. -> Mitigation: require the operator to use `--index`
  when a supplied name matches more than one target.
- [Risk] Separate config and secrets files cannot be updated transactionally with
  the current helpers. -> Mitigation: stage all changes in memory, validate
  before writing, and use safe write ordering that prefers orphaned secrets.
- [Risk] The probe command performs a real outbound POST and may trigger the
  remote system. -> Mitigation: use a minimal probe payload, make the command
  explicit (`test` only), and avoid automatic retries.
- [Risk] Legacy hand-edited configs may keep webhook URLs or headers in
  `netclaw.json`. -> Mitigation: keep doctor warnings, and make CLI-managed
  writes normalize those secrets into `secrets.json`.

## Migration Plan

1. Add notification webhook command parsing and help text in `Netclaw.Cli`.
2. Extract or add config helpers for reading, selecting, and rewriting
    `Notifications.Webhooks` across base config and secrets overlay.
3. Normalize legacy base-config secrets into the overlay during CLI-managed
   operations.
4. Wire shared validation into add/remove/test flows so invalid targets fail
   before any write or probe.
5. Add direct HTTP probe behavior and safe response formatting.
6. Add tests for list/add/remove/test behaviors, including secret handling and
   failure paths.
7. Update operator docs for the new command surface.

## Open Questions

- Should `add` require a name for newly created targets, or should unnamed
  targets remain first-class for parity with the raw config format?
- Should the probe payload shape be standardized now for future automation, or
  remain an internal CLI-only contract until a broader notification UX exists?
