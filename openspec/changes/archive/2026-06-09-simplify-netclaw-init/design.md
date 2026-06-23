## Context

This change narrows `netclaw init` to what it is now supposed to be:
bootstrap. The old draft assumed re-run refusal plus `init --force`, and it
treated Team/Public feature configuration as something silently derived from
posture. The locked decisions are more specific:

- Identity stays owned by init.
- `netclaw config` owns post-install editing.
- Team and Public posture flows continue into Enabled Features.
- Personal skips Enabled Features.
- Existing installs get an explicit action menu, not a plain refusal and
  not a hidden force flag.

## Goals / Non-Goals

**Goals:**

- Make first-run init a short bootstrap flow.
- Preserve Identity ownership inside init.
- Handle existing installs through an explicit menu.
- Remove `init --force` from the plan.
- Keep posture values to Personal / Team / Public.
- Keep Enabled Features separate from Audience Profiles.

**Non-Goals:**

- Making init the main post-install editor.
- Adding Enterprise posture.
- Putting Audience Profiles or MCP permissions inside init.
- Designing inline config repair for broken bootstrap state.

## Decisions

### D1. Existing installs get a menu, not refusal-plus-flag

When `netclaw init` detects an existing install, it opens a menu with these
four choices:

- Redo identity setup
- Open configuration editor
- Start over from scratch
- Cancel

Alternative considered: refuse and point to `netclaw config`, with
`--force` for reset. Rejected because the user explicitly locked the menu
shape instead.

### D2. Scratch reset is a two-stage destructive flow

`Start over from scratch` opens a dialog with:

- Reset setup only
- Full reset
- Cancel

Either destructive path then requires double confirmation before mutation.

### D3. Identity remains init-owned

Existing-install identity edits stay in init via `Redo identity setup`.
This branch does not move Identity into `netclaw config`.

### D4. Team/Public posture continues into Enabled Features

Security Posture remains separate from Enabled Features.

- Personal: posture flow ends without Enabled Features.
- Team/Public: posture flow automatically continues into Enabled Features.

Alternative considered: keep silently applying runtime defaults with no
Enabled Features step. Rejected because the user explicitly locked the
continuation behavior.

### D5. Audience Profiles stays out of init bootstrap

Audience Profiles is a post-install curated editor in `netclaw config`.
Init does not expose per-audience access editing.

### D6. Post-flight points to config for ongoing changes

Successful bootstrap ends with a message directing the operator to
`netclaw chat` to start and `netclaw config` for ongoing settings.

## Risks / Trade-offs

- Existing-install init now has more branching than the simple refusal
  draft. Mitigation: the branches are explicit and operator-centered.
- Identity remaining in init means two different commands remain part of
  the operator journey. Mitigation: this is the locked ownership split.
- Double confirmation adds a little friction to reset. Mitigation: that is
  intentional for destructive actions.

## Migration Plan

1. Land the bootstrap-only init rewrite.
2. Existing installs reaching `netclaw init` see the existing-install menu.
3. Ongoing settings move to `netclaw config`; identity changes remain in
   init.

## Open Questions

None. The menu wording and ownership split are locked.
