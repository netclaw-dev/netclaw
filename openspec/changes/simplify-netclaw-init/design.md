## Context

**UI wireframes:** every page introduced by this change — the three
init steps, the post-flight screen, the existing-config refusal
(Init.E1), and the force-reset backup confirm (Init.E2) — is mocked
in `docs/ui/TUI-003-simplified-init-wireframes.md`. Implementors SHALL
treat TUI-003 as the visual contract for this change. The companion
TUI-002 mocks `netclaw config`, which is the destination operators are
nudged toward at post-flight.

The `section-editor-abstraction` change (Change A) refactored Provider,
Identity, and Posture step viewmodels into reentrant `ISectionEditor`s
and switched the wizard's terminal write to merge-on-save. The
`netclaw-config-command` change (Change B) introduced
`netclaw config` and the ten section editors that now own the
configuration surfaces previously walked by the init wizard. With both
changes landed, `netclaw init` is the only piece left that still
treats configuration as a single big linear flow.

This change trims the wizard to provider + identity + posture so new
operators reach `netclaw chat` after three prompts, and makes the
existing-config-on-re-run behavior explicit (refuse + offer `--force`)
instead of the prior undefined behavior. The wizard's previous
breadth — Slack/Discord/Mattermost setup, ACL, search, browser
automation, MCP servers, exposure mode, channel audience configuration,
feature toggles, external skills, skill feeds, webhook URL — moves
entirely to `netclaw config`. None of those surfaces are deleted; they
just leave the init step list.

## Goals / Non-Goals

**Goals:**

- Reduce time-to-first-chat for new operators: three prompts after
  provider selection (provider auth + model selection are part of the
  Provider step's existing sub-flow).
- Make re-running `netclaw init` over an existing install a
  well-defined operation: refuse with helpful pointers by default, and
  offer `--force` for a backed-up reset.
- Preserve the existing posture-default cascade: Personal / Team /
  Enterprise still drive the initial `Tools.AudienceProfiles` mapping
  written at init time.
- Migrate the reverse-proxy exposure-mode init tape coverage to the
  `netclaw config` smoke tape introduced in Change B.

**Non-Goals:**

- Deleting any `ISectionEditor` class that lived as an init step. The
  classes survive as `netclaw config` editors after Change B.
- Renaming or re-architecting `netclaw config`.
- Changing posture-default mappings.
- Introducing an Identity section editor in `netclaw config`. Renaming
  the agent post-install remains a file-edit (or `init --force`) task
  for MVP.
- Hot-reload of the running daemon on init completion.

## Decisions

### D1. Step list reduced to three; classes preserved

The init wizard's `WizardOrchestrator` step composition is reduced from
the current 12-entry list to exactly three: Provider, Identity,
Posture. The other `ISectionEditor` implementations (Search, Slack,
Discord, Mattermost, Exposure, AudienceProfiles, OutboundWebhooks,
InboundWebhooks, ExternalSkills, SkillFeeds, BrowserAutomation) remain
registered in the registry and reachable via `netclaw config` —
they're just not part of `netclaw init`'s step list.

Alternative considered: delete the step viewmodel classes that
weren't on the init list. Rejected because they ARE the section
editors `netclaw config` runs; the same class serves both. Keeping
one class per editable section is the whole point of the
`ISectionEditor` abstraction.

### D2. Existing-config detection refuses by default, allows `--force`

Re-running `netclaw init` over an existing install in the current
code is undefined behavior. After Change A's merge-on-save plus
`ExistingConfig` pre-population, a naive re-run would silently
re-walk the wizard and re-write whatever the operator typed. That's
confusing — `netclaw init` is named for "initial setup," not "edit."
The right behavior is:

- Default: refuse with a clear message pointing at `netclaw config`
  for live edits.
- Force: explicit `--force` flag triggers a type-to-confirm backup
  and proceeds as a fresh first-run. Backup is rename-aside
  (`netclaw.json.bak.<ts>`); operators retain manual recovery.

Alternative considered: have `netclaw init` re-running over existing
config auto-launch `netclaw config`. Rejected because it conflates
two commands; an operator typing `netclaw init` after install
expects setup behavior, not menu-edit behavior. Refusing is clearer.

### D3. Trimmed Identity step preserves three fields, defaults the rest

`IdentityStepViewModel`'s field set drops to agent name + user name
+ timezone. The previously-prompted fields (webhook URL,
communication style, workspaces directory) use their existing
defaults and are not exposed in init. Operators wanting to change
them post-install edit `netclaw.json` directly until a future
Identity section editor lands.

Alternative considered: add a "Show advanced fields" affordance in
the trimmed Identity step. Rejected because it re-introduces the
"long wizard" feel; the explicit out-of-MVP file-edit path is the
right scope discipline.

### D4. Post-flight nudge in Termina + stderr after teardown

The post-flight screen inside Termina confirms what was set, reports
health-check pass/fail, and prints the next-step nudge ("Run
`netclaw chat` to start, or `netclaw config` to configure ..."). On
Termina teardown the same one-line nudge prints to stderr so it
remains visible after the TUI clears. This dual-path matches Change
B's daemon-restart nudge pattern.

Alternative considered: just print the nudge to stderr after exit
without a Termina screen. Rejected because operators benefit from
seeing setup-complete confirmation while the TUI is still up; the
stderr line is a fallback for cases where the operator's terminal
emulator wipes the screen on Termina exit.

### D5. Reverse-proxy tape migrates to config, not deleted outright

`init-wizard-reverse-proxy.tape` exercises an exposure-mode flow
that today lives inside the init wizard. With exposure mode moved
to `netclaw config`, the equivalent flow is `config-exposure-mode.tape`
(introduced in Change B). This change deletes the init-side tape
because its coverage is fully owned by the config-side tape. Net
tape count for exposure-mode regression coverage remains 1.

### D6. New init tapes for refuse-and-force paths

The refuse path and the `--force` reset path need explicit smoke
coverage, otherwise a future change could regress them silently.
Two new tapes:

- `init-existing-config-refuse.tape` — pre-stages a config and
  asserts refusal text + exit zero on TTY confirm.
- `init-force-reset.tape` — pre-stages a config, runs `--force`,
  types `reset` to confirm, completes the short flow, asserts the
  .bak files exist and a fresh `netclaw.json` was written.

Both are short tapes (likely <40 lines each). The new init tape
total is 3 (down from the current 2: one is revised, one is deleted,
two are added).

### D7. PRD-004 update lands in this change

PRD-004's "reentrant init dashboard" wording was authored before this
sequence of changes locked the simplified-init + `netclaw config`
split. The wording is updated in this change to match the shipped
shape; cross-references to issues #455 (closed in Change A) and
#1150 (closed in Change B) are added.

## Risks / Trade-offs

- [Behavior change for re-runs] Operators who have been
  re-running `netclaw init` to tweak config (against the prior
  undefined behavior) will be refused after this change. →
  Mitigation: the refusal message names `netclaw config` and
  `netclaw init --force` explicitly. Documentation update in
  PRD-004 references the new behavior. Existing-config detection
  is consistent across TTY and non-TTY contexts.

- [Posture-default writes happen non-interactively now] Operators on
  Team or Enterprise postures no longer walk a feature-selection
  step at init. They see the defaults applied automatically and can
  override per-audience later. → Mitigation: the Change B Audience
  Profiles editor is the documented place to tune; PRD-004 names it.

- [Identity field loss for new installs] New operators no longer
  set webhook URL, communication style, or workspaces directory at
  init. → Mitigation: defaults are reasonable; webhook URL belongs
  in Outbound Webhooks (Change B's section editor); workspaces
  directory and communication style are file-edit-only for MVP and
  documented as such in PRD-004.

- [.bak files accumulate on repeated forces] Each `--force` reset
  creates a new pair of timestamped .bak files. After many forces
  the directory could grow. → Mitigation: this is the operator's
  responsibility; the .bak files are theirs to manage. The
  type-to-confirm gate ensures forced resets are deliberate, so
  accumulation is bounded by intentional operator action.

- [CI surprise on non-TTY re-runs] Existing CI scripts that called
  `netclaw init` non-interactively over a populated config would
  silently re-walk previously. After this change they exit non-zero.
  → Mitigation: the new behavior is the safe one. Any CI that was
  relying on undefined re-run behavior was already buggy; the
  non-zero exit makes the breakage visible. Migration is to call
  `netclaw config` (programmatic CLI use is via the
  CLI subcommands `netclaw provider/model/mcp`, not `netclaw config`).

## Migration Plan

1. Land Changes A and B before this change.
2. Land this change. Existing operators on Personal posture: their
   re-runs now refuse cleanly. Existing operators on Team or
   Enterprise: same. Operators wanting to edit anything use
   `netclaw config`; operators wanting a clean slate use
   `netclaw init --force`.
3. PRD-004 update is part of this change's PR.
4. The CHANGELOG / release notes call out the simplified-init
   behavior change so operators are not surprised on upgrade.

Rollback: revert this change. The wizard returns to its 12-step
linear form. Existing-config detection disappears (re-runs go back
to undefined behavior). The two new init tapes are deleted; the
init-wizard-reverse-proxy tape returns. `netclaw config` remains
available as long as Change B remains.

## Open Questions

None at execution time. All architectural decisions are locked above.
