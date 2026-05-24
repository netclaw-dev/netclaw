## Why

`netclaw init` is now explicitly the first-run bootstrap command and then
rarely used again. The earlier planning still treated it as a re-runnable
general editor with a `--force` reset path. That contradicts the locked
product split:

- `netclaw init` is for bootstrap.
- `netclaw config` is the main post-install settings surface.
- Identity remains `netclaw init` owned.

This change rewrites init around that split. It trims init to the minimum
bootstrap flow, removes `init --force` from planning, and makes existing-
install behavior an explicit menu that either redoes identity, hands off to
the configuration editor, offers a guarded reset flow, or cancels.

Source PRDs: `PRD-004-cli-onboarding-and-config.md`,
`PRD-001-netclaw-mvp.md`.

## What Changes

- Trim first-run `netclaw init` to a bootstrap flow that gets operators to
  a runnable install quickly.
- Keep posture values to `Personal`, `Team`, and `Public` only.
- Keep Security Posture, Enabled Features, and Audience Profiles as
  separate concepts.
- First-run posture flow behavior:
  - `Personal` skips Enabled Features.
  - `Team` and `Public` automatically continue into Enabled Features.
- Enabled Features remains deployment-wide runtime enablement, not a
  per-audience policy surface.
- Identity remains owned by init, not by `netclaw config`.
- On an existing install, `netclaw init` SHALL open a menu with exactly:
  - `Redo identity setup`
  - `Open configuration editor`
  - `Start over from scratch`
  - `Cancel`
- `Open configuration editor` routes to `netclaw config`.
- `Start over from scratch` opens a second dialog with exactly:
  - `Reset setup only`
  - `Full reset`
  - `Cancel`
  followed by a double confirmation before any destructive action.
- Remove `init --force` from planning entirely.
- Keep the post-flight messaging focused on the split:
  bootstrap is complete, use `netclaw chat` to start and `netclaw config`
  for ongoing settings.

**In scope (MVP):** bootstrap-first init flow, existing-install menu,
guarded scratch-reset flow with double confirmation, posture and enabled-
features behavior aligned to the locked decisions, and init smoke coverage
updated to match.

**Out of scope:** turning init back into the main settings surface,
recreating config editing inline, `--force`, Enterprise posture, or moving
Identity into `netclaw config`.

## Capabilities

### Modified Capabilities

- `netclaw-onboarding`: bootstrap-only init flow, explicit existing-install
  menu, guarded scratch reset, and locked posture/enabled-features split.

## Impact

**Affected systems:**

- CLI init entry handling.
- Init wizard step composition.
- Existing-install branching screens.
- Init smoke tapes and assertions.

**Security and operational impact:**

- Existing installs are not silently re-walked.
- Destructive reset behavior is explicit, menu-driven, and double-
  confirmed.
- Identity remains under init ownership instead of becoming a second config
  surface.
