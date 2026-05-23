## Why

`netclaw init` is the first-impression experience for every new Netclaw
operator, and it has grown into a 12-step linear wizard that walks
through provider selection, security posture, feature selection,
channel pickers and per-channel sub-flows, search backend, browser
automation, identity, external skills, skill feeds, exposure mode, and
a final health check. This is the longest single point of abandonment
for new installs. After the `section-editor-abstraction` change
introduced reentrancy and the `netclaw-config-command` change moved
ongoing configuration to a menu-driven editor, the init wizard's
purpose is now strictly bootstrap: produce a minimum-viable config
that lets the operator reach `netclaw chat` as quickly as possible.
This change cuts the wizard down to three prompts — provider,
identity, posture — and routes operators to `netclaw config` for
everything else. It also makes the existing-config detection behavior
explicit (refuse with a helpful message; offer `--force` for a backed-up
reset) instead of leaving re-runs as undefined behavior.

Source PRDs: `PRD-004-cli-onboarding-and-config.md`,
`PRD-001-netclaw-mvp.md`.

## What Changes

- Trim `netclaw init` to three steps + a terminal write/health-check:
  - **Step 1: Provider** — reuse existing `ProviderStepViewModel`
    (refactored to `ISectionEditor` in Change A) end-to-end.
  - **Step 2: Identity** — trimmed to agent name, user name (what the
    agent calls the operator), and timezone. Drop the webhook URL
    prompt, the workspaces-directory prompt, and the communication-style
    prompt. Defaults remain available for the dropped values.
  - **Step 3: Security Posture** — reuse existing
    `SecurityPostureStepViewModel` (refactored in Change A). The
    posture choice applies the posture-default `Tools.AudienceProfiles`
    mapping in-memory before the terminal write; operators tune
    per-audience later via `netclaw config → Audience Profiles`.
  - **Terminal**: write merged config and run the existing health-check.
- Remove from `netclaw init` the following step viewmodels (the
  corresponding `ISectionEditor` implementations introduced in Change B
  remain in `netclaw config`): `ChannelPickerStepViewModel`,
  `ChannelsStepViewModel`, `FeatureSelectionStepViewModel`,
  `SearchStepViewModel`, `SlackStepViewModel`, `DiscordStepViewModel`,
  `MattermostStepViewModel`, `ExposureModeStepViewModel`,
  `BrowserAutomationStepViewModel`, `ExternalSkillsStepViewModel`,
  `SkillFeedsStepViewModel`. The classes are not deleted (they live on
  as section editors); only their participation in the init step list
  is removed.
- Add a post-flight screen inside Termina that confirms what was set,
  reports health-check pass/fail, and points operators at
  `netclaw config` for further configuration. On Termina teardown, the
  same one-line nudge prints to stderr so it remains visible after the
  TUI clears: `Setup complete. Run \`netclaw chat\` to start, or
  \`netclaw config\` to configure channels, webhooks, search, and
  more.`
- Add explicit existing-config detection at `netclaw init` entry. When
  `netclaw.json` exists and `--force` was not passed, the command
  renders a refusal screen (TTY) or prints to stderr (non-TTY)
  pointing operators at `netclaw config` for edits or
  `netclaw init --force` to reset. Exit zero in TTY-confirmed
  acknowledgement; exit non-zero in non-TTY usage so CI catches the
  surprise.
- Add `netclaw init --force` behavior: when an existing config is
  present, the command opens a type-to-confirm backup screen. On
  confirm, `netclaw.json` is renamed to `netclaw.json.bak.<unix-ts>`
  and `secrets.json` is renamed to `secrets.json.bak.<unix-ts>`. The
  wizard then proceeds as a fresh first-run. Operators must re-enter
  credentials; the .bak files are preserved for manual recovery.
- Revise `tests/smoke/tapes/init-wizard.tape` and its assertion
  script to exercise the three-step flow (provider + identity +
  posture) plus the post-flight screen. The tape shortens from
  ~150 lines to ~50.
- Delete `tests/smoke/tapes/init-wizard-reverse-proxy.tape` and its
  assertion. Reverse-proxy coverage migrates to
  `config-exposure-mode.tape` introduced in Change B.
- Add two new smoke tapes covering the new init UX:
  - `init-existing-config-refuse.tape` — pre-stage a `netclaw.json`,
    run `netclaw init`, assert refusal message + zero exit.
  - `init-force-reset.tape` — pre-stage a `netclaw.json`, run
    `netclaw init --force`, type "reset" to confirm, complete the
    short flow, assert `.bak.*` files exist and new config is
    written.
- Update PRD-004 to reflect the simplified-init + `netclaw config`
  shape: the original "reentrant init dashboard" wording is replaced
  with the documented two-command split.

**In scope (MVP):** trimming the wizard to provider + identity +
posture, the post-flight screen and stderr nudge, the existing-config
refusal and `--force` reset paths, revising the existing init tape,
deleting the reverse-proxy init tape, and adding two new init tapes
covering the refuse and force paths.

**Out of scope:** any behavioral change to `netclaw config` (it
already exists from the previous change); deleting the existing init
step viewmodel classes (they continue to back the section editors in
`netclaw config`); migrating identity-related setup that today lives
inside the trimmed Identity step (workspaces directory, communication
style — these continue to use their existing defaults silently for
MVP; operators wanting to change them edit the file directly until
a future Identity section editor lands); changes to PRD-002 or
posture defaults.

## Capabilities

### Modified Capabilities

- `netclaw-onboarding`: the init wizard's collected inputs SHALL be
  trimmed to provider, identity (agent name + user name + timezone),
  and security posture. The wizard SHALL detect existing config at
  entry and refuse (or offer `--force` reset). The wizard SHALL show
  a post-flight screen pointing operators at `netclaw config`.

## Impact

**Affected systems:**

- CLI entry point (`Netclaw.Cli.Program`) gains the existing-config
  detection branch and the `--force` flag.
- Init wizard step list (`Netclaw.Cli.Tui.Wizard.WizardOrchestrator`
  composition) is reduced to three viewmodels.
- `IdentityStepViewModel` is trimmed (no class removal; field set is
  reduced). The viewmodel continues to satisfy the `ISectionEditor`
  contract introduced in Change A.
- Init smoke tape (`tests/smoke/tapes/init-wizard.tape`) is rewritten;
  reverse-proxy tape is deleted; two new init tapes added.
- PRD-004 is updated to match the simplified-init + `netclaw config`
  shape.

**Security and operational impact:**

- Existing-config refusal prevents accidental re-runs from blasting
  through an existing install. The `--force` path explicitly backs up
  both `netclaw.json` and `secrets.json` to timestamped `.bak.*`
  files; operators retain a manual recovery path. The force path
  requires a type-to-confirm because the operation moves credentials
  out of the active file (forcing re-entry).
- Trimming Identity drops the in-wizard webhook URL prompt. The
  outbound-webhook surface was already available via `netclaw config →
  Outbound Webhooks` (Change B); operators with active webhook
  configurations are not affected (their existing webhook entries
  remain). Operators on a fresh install no longer set up a webhook
  during init; they do so in `netclaw config` post-bootstrap.
- The simplified init reduces the time-to-first-chat for new
  operators. No new network surface, no new persistence schema, no
  new daemon contract change.
- Posture's audience-profile cascade continues to be applied on init
  (Personal posture sets all features enabled; Team and Enterprise
  set audience-appropriate defaults). Operators on Team or Enterprise
  who used to walk the feature-selection step now get the same
  posture-default mapping written non-interactively and can tune via
  `netclaw config → Audience Profiles`.
- No change to the daemon. No change to existing CLI subcommands
  (`netclaw provider`, `netclaw model`, `netclaw mcp`).
