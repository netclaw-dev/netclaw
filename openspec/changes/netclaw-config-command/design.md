## Context

**UI wireframes:** every page introduced by this change is mocked in
`docs/ui/TUI-002-netclaw-config-wireframes.md` (dashboard, all 12 section
editors, list editor templates T1–T8, doctor results page, daemon
restart nudge). Implementors SHALL treat TUI-002 as the visual contract;
this design document explains decisions and trade-offs around it.

The `section-editor-abstraction` change (predecessor) introduced the
`ISectionEditor` contract, the `SectionEditorRegistry`, the merge-on-save
plumbing, and the single-step `WizardOrchestrator` mode. It refactored
Provider, Identity, and Posture into reentrant section editors but did
not introduce any new user-facing command. The linear `netclaw init`
wizard still owns the only path to configuration changes today,
including for sections that operators routinely tweak post-install
(search provider, channels, exposure mode, webhooks, skill feeds,
external skill directories, audience profiles, browser automation).

This change introduces `netclaw config` as the canonical menu-driven
editor for those sections, composes ten new `ISectionEditor`
implementations (plus reuses the three from Change A indirectly through
the dashboard), introduces the multi-value `ListEditor<T>` component,
and hardens the menu registry audit so the menu and its editors cannot
drift apart in subsequent work. The buggy feature-selection step from
#1150 is removed and its responsibility moves to the new
`AudienceProfilesSectionEditor`, with a smoke tape that exercises
arrow-nav and toggle keystrokes.

## Goals / Non-Goals

**Goals:**

- Ship a menu-driven, reentrant TUI editor for the ten sections
  operators actually want to change post-install, with doctor-blessed
  saves and merge-on-save preserving every unrelated section.
- Reuse the Change A abstraction without forking: every editor in this
  change is an `ISectionEditor` instance and runs inside the existing
  `WizardOrchestrator` (now in single-step mode).
- Establish the generic list editor + item editor pattern so future
  multi-value sections inherit add/edit/remove UX without re-inventing
  it.
- Close #1150 by replacing the broken feature-selection step with the
  Audience Profiles editor, exercised by a tape that drives the
  failing keystrokes from the bug report.
- Activate the menu registry audit's full contract: every editor in
  the registry must have a smoke tape and a round-trip xUnit test,
  enforced at CI time.

**Non-Goals:**

- Simplifying the init wizard (third change).
- Hot-reloading the running daemon on config change.
- Editing inbound webhook route files from the TUI.
- Refactoring `netclaw provider`/`model`/`mcp` CLI subcommands.
- Identity changes post-install (renaming the agent stays a file-edit
  task for MVP).
- Editing telemetry, logging, memory tuning, session timeouts,
  sub-agent timeouts, shell hard-deny patterns, or scheduling on/off
  from the TUI (file-edit only).
- Export/import config bundle or factory reset commands.
- Installing Playwright from the TUI (instructions sub-page only).

## Decisions

### D1. Dashboard is a single Termina page with a flat registry

`ConfigDashboardPage` walks `SectionEditorRegistry.All()` once and
renders the editors in registration order, grouped by `Category` only
for visual presentation. The registry stays flat; the audit, the
round-trip test base class, and the smoke-tape lookup all key off
`SectionId`. Twelve editors render comfortably in a standard 80×24
terminal without scrolling.

Alternative considered: a tree-structured registry with first-class
parent/child nodes. Rejected because every "tree" need today is
satisfied by a `Category` string tag and a heavier structure would
complicate the audit, the registry resolution, and the round-trip
tests for no current benefit.

### D2. Sub-page items via modal sub-orchestrators, not nested pages

When the `ListEditor<T>` opens a sub-page (e.g. Outbound Webhooks edit
form), the host invokes a fresh `WizardOrchestrator` in single-step mode
on the sub-page's viewmodel. The sub-orchestrator's Save returns its
result to the parent list; the parent list updates in-memory state and
re-renders. This keeps step lifecycle uniform across the whole config
command and avoids a separate "nested page" lifecycle.

Alternative considered: a stack-of-pages model in Termina layout
where the sub-page is part of the same rendering pass. Rejected
because the sub-orchestrator model already exists from Change A and
adding a parallel stack would split the lifecycle.

### D3. Doctor blessing is per-editor on save, never inline-per-field

When a section editor saves, the host builds the candidate merged
config in memory, resolves only that editor's `RelevantDoctorChecks`,
and runs them against the candidate. The dashboard's "Run full doctor"
item is the only entry point for cross-section checks. Per-field
validation lives in the editor's own form (e.g. URL parsing) and is
distinct from doctor blessing.

Alternative considered: per-field inline validation backed by doctor.
Rejected because doctor checks are designed to operate over complete
config sections, not single fields; running them on every keystroke
would produce confusing transient errors as the operator fills in
related fields.

### D4. List editor `+ Add` row as a list member, not a separate action bar

`ListEditor<T>` renders `+ Add <noun>` as the last row of the list
itself. Navigation is uniform (arrow keys move through items, Enter
activates) and there is no modal handoff between "list section" and
"action section." The `+ Add` row is visually distinct (different
glyph, no status) so operators do not mistake it for a data row.

Alternative considered: a fixed action bar at the list bottom with
explicit `[ Add ]`, `[ Edit ]`, `[ Remove ]` buttons. Rejected
because every TUI list editor we model on (lazygit, k9s, git rebase
interactive) uses inline rows for adds, and the modal handoff
between list and action bar adds keystrokes for no benefit.

### D5. Inline `d`/`y` confirm for list deletes; modal confirm for credential removal

List deletes (`d` on a focused item) get a single-key inline
`Remove? [y/N]` prompt because the cost of an accidental delete is low
(operator re-adds the item from memory). Credential removal uses a
default-Cancel modal confirm because the cost is higher (operator
must re-enter or rotate the credential externally). Both confirm
patterns are inherited from Change A's secret-handling contract.

### D6. New schema section for `BrowserAutomation`

The schema gains `BrowserAutomation { Enabled: bool,
PlaywrightVersion?: string }` as a top-level section with `Enabled`
defaulting to `false` so existing configs validate without a fix
pass. A matching `BrowserAutomationConfig.cs` lives in
`Netclaw.Configuration`. The browser-automation step today writes
its state into `McpServers` indirectly; this change formalizes the
section so the editor and doctor check have a stable home.

Alternative considered: keep using `McpServers` as the implicit
home. Rejected because conflating browser-automation with MCP
server config makes both harder to reason about; the doctor check
needs to look in one place.

### D7. Audit promotion from soft-warn to hard-fail in this change

In Change A the menu-registry audit allowed missing tape files
without failing (the `netclaw config` command did not exist yet). In
this change the command exists, so the audit's tape-existence check
flips to hard-fail. New section editors added in future PRs cannot
ship without a tape and a round-trip test.

Alternative considered: keep tape-existence as soft-warn. Rejected
because the contract is only as strong as its weakest enforced rule;
soft-warn drifts into "we'll get to it" which is exactly the failure
mode the audit exists to prevent.

### D8. Daemon-restart nudge is a stderr line, not a screen

After a save-and-quit, Termina tears down and the operator returns to
the shell. The nudge prints to stderr after Termina exits so it
remains on screen even after the TUI clears. It is suppressed when
no writes occurred or when the daemon is not running, to avoid
nagging.

Alternative considered: render the nudge as a final post-flight screen
inside Termina. Rejected because the operator may dismiss the screen
without reading it; a stderr line persists in the scroll buffer.

### D9. `config-no-init.tape` covers the refusal path

The refuse-when-no-config behavior is exercised by its own tape and
assertion. This avoids overloading any single section-editor tape with
the refusal scenario and keeps the audit's "tape per registered
editor" rule clean (the refusal tape is not associated with any
registry entry).

### D10. Editor file layout under `Tui/Sections/<Section>/`

Each section editor lives in its own folder under
`src/Netclaw.Cli/Tui/Sections/`. Chat-channel editors get a
`Channels/` parent folder, webhooks get a `Webhooks/` parent. The
folder layout mirrors the menu's visual grouping for discoverability
while keeping the registry flat.

## Risks / Trade-offs

- [CI runtime increase] Twelve new smoke tapes plus the no-init refusal
  tape add roughly 5–10 minutes to PR-gating smoke runs. → Mitigation:
  smoke tapes are inherently parallelizable; if the wall-clock cost
  becomes a problem, parallelize tape execution before reducing
  coverage.

- [Audit false positives during partial PRs] During implementation a
  contributor may add a section editor before its tape lands.
  → Mitigation: the audit's failure message names the missing artifact
  explicitly. The convention is "tape and round-trip test land in the
  same commit as the editor." PR review enforces it.

- [Schema migration ergonomics] Adding `BrowserAutomation` as a new
  top-level section is one of the few schema additions in this work.
  → Mitigation: `"Enabled": false` default lets existing configs
  validate; `SchemaFixResolver` auto-inserts the missing key on
  next `netclaw doctor --fix` run. Per CLAUDE.md schema sync rule,
  the schema and `BrowserAutomationConfig.cs` ship in the same PR.

- [Reuse of existing channel-audience UX] The new chat-channel
  section editors host the existing `channel-audience-tui`
  cycling behavior, which is established and tested. → Mitigation:
  the section editors compose the existing TUI components rather
  than re-implementing them; the channel-audience-tui requirements
  remain authoritative.

- [Doctor checks that probe network endpoints] `SkillFeedsDoctorCheck`
  and Slack/Discord/Mattermost `Test Connection` actions reach out
  to remote services. → Mitigation: probing is warn-only or
  user-initiated. Doctor errors that block save are local-only
  (schema validity, key/backend pairing, etc.).

- [Audience Profiles editor's keystroke contract] If Termina's
  `SelectionListNode` has a latent bug (which #1150 implies), arrow
  nav and Space toggle may misbehave. → Mitigation: the
  `config-audience.tape` smoke tape drives exactly those keystrokes
  and the assertion verifies the resulting state. If the underlying
  component is broken at the Termina level, this tape will fail and
  the bug must be fixed before merge.

- [Removed feature-selection step on re-run] Operators who currently
  rely on the feature-selection step in `netclaw init` lose it.
  → Mitigation: PRD-004 and the `feature-selection-wizard` spec
  delta document the relocation. The new Audience Profiles editor
  is reachable from one menu entry away. Migration text in the PR
  description points operators at the new path.

- [Multi-instance editing] Two concurrent `netclaw config` processes
  on the same install would both load → merge → write to the same
  `netclaw.json` and `secrets.json`. → Mitigation: out of MVP scope;
  semantics are last-write-wins per the file's atomic tmp-rename
  write. Documented as a known limitation. File locks are deferred
  until there is concrete evidence of operators running multiple
  TUI editors simultaneously.

- [Test Connection partial failure shape] Slack/Discord/Mattermost
  Test Connection actions probe several capabilities (auth, channel
  access, DM access). Some sub-probes may succeed while others
  fail. → Mitigation: the result banner SHALL render one line per
  sub-probe with its own status glyph (`✓ Bot token valid`,
  `✗ Channel C01ABCDE not in workspace`). Network timeouts SHALL
  render as `⚠ probe timed out` rather than a fatal failure, since
  the operator may have a transient network issue. Test Connection
  is advisory only; it never blocks the editor's Save.

## Migration Plan

This change ships net-new behavior (`netclaw config`) plus a single
behavior removal (the feature-selection step in init). Migration
considerations:

1. Land the change. `netclaw init` no longer shows the
   feature-selection step on re-run; existing `netclaw.json` keeps
   its feature-flag values untouched.
2. Operators who want to change feature flags post-install run
   `netclaw config → Audience Profiles → <audience>`.
3. The new `BrowserAutomation` schema section is auto-inserted by
   `netclaw doctor --fix` on existing installs (or appears
   automatically when `netclaw config` runs over an existing
   config that lacks it — the merge writer creates the section
   with `Enabled: false` when the operator opens the editor).
4. Daemon restart is required for live config changes to take
   effect; the stderr nudge instructs operators to restart when
   relevant.

Rollback: revert the change. `netclaw config` disappears from the
CLI surface. The feature-selection step returns to `netclaw init`
on re-run. The audit returns to Change A's soft-warn tape-existence
behavior. `netclaw.json` values written by `netclaw config` remain
valid against the schema and continue to be respected at runtime.

## Open Questions

None at execution time. All architectural decisions are locked above.
