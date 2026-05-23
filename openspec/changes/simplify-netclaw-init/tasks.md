## 1. OpenSpec planning artifacts and traceability

- [ ] 1.1 Confirm proposal, design, and spec deltas cover the trimmed
  three-step init flow, existing-config refusal, `--force` reset with
  backup, post-flight nudge, and the smoke-tape revisions.
- [ ] 1.2 Verify traceability references to `PRD-004` and `PRD-001`
  across change artifacts.
- [ ] 1.3 Run `openspec validate simplify-netclaw-init --type change`
  and resolve all issues.

## 2. CLI entry point

- [ ] 2.1 Update `Netclaw.Cli.Program` `netclaw init` dispatch to
  parse the new `--force` flag. Unknown flags produce usage error
  and non-zero exit.
- [ ] 2.2 Add existing-config detection at init entry: if
  `netclaw.json` exists and `--force` was not passed, branch to the
  refusal path (TTY screen vs non-TTY stderr).
- [ ] 2.3 Implement non-TTY refusal: print
  `Netclaw is already initialized at <path>. Run \`netclaw config\`
   to edit, or \`netclaw init --force\` to reset.` to stderr; exit
  with non-zero status.
- [ ] 2.4 Implement TTY refusal: launch Termina with a single-screen
  refusal page; default focus on `[ OK ]`; Enter or Esc exits with
  status 0.

## 3. `--force` reset path

- [ ] 3.1 When `--force` is passed and `netclaw.json` exists, launch
  Termina with the type-to-confirm backup screen. The text
  acknowledges both `netclaw.json` and `secrets.json` will be moved
  aside.
- [ ] 3.2 Default focus on `[ Cancel ]`; the `[ Reset and continue ]`
  button is enabled only when the operator types `reset` into the
  confirm input.
- [ ] 3.3 On confirm, rename `netclaw.json` →
  `netclaw.json.bak.<unix-ts>` and `secrets.json` →
  `secrets.json.bak.<unix-ts>` atomically. Generate timestamp once
  per invocation so the two files share a suffix.
- [ ] 3.4 After backup, proceed into the three-step wizard as a fresh
  first-run (`WizardContext.ExistingConfig = null`).
- [ ] 3.5 On successful post-flight, list the .bak file paths in the
  post-flight screen so the operator knows where the prior config
  went.
- [ ] 3.6 `--force` with no existing config silently behaves as plain
  `netclaw init` (no backup screen).

## 4. Wizard step list trim

- [ ] 4.1 Reduce `WizardOrchestrator`'s init-side step list to exactly
  three viewmodels: Provider, Identity, Posture. Health check remains
  the terminal step.
- [ ] 4.2 Remove from the init step list (NOT delete the classes):
  `ChannelPickerStepViewModel`, `ChannelsStepViewModel`,
  `FeatureSelectionStepViewModel`, `SearchStepViewModel`,
  `SlackStepViewModel`, `DiscordStepViewModel`,
  `MattermostStepViewModel`, `ExposureModeStepViewModel`,
  `BrowserAutomationStepViewModel`, `ExternalSkillsStepViewModel`,
  `SkillFeedsStepViewModel`. These classes continue to back
  `netclaw config` section editors per Change B.
- [ ] 4.3 Verify each removed class is still registered with the DI
  container as an `ISectionEditor` so `netclaw config` continues to
  resolve them.

## 5. Identity step trim

- [ ] 5.1 In `IdentityStepViewModel`, retain only the agent-name,
  user-name, and timezone fields when running inside the init step
  list. The class's `ISectionEditor` implementation may continue to
  expose additional fields for future post-install editing; the init
  step's view SHALL omit them.
- [ ] 5.2 Remove from the init wizard's Identity view: webhook URL
  prompt, communication-style prompt, workspaces-directory prompt.
  Their default values are preserved silently.
- [ ] 5.3 Validate fields per existing rules (agent name required, no
  whitespace; user name required; timezone validates against
  `TimeZoneInfo.FindSystemTimeZoneById`).

## 6. Posture cascade write

- [ ] 6.1 In the Posture step's `ContributeConfig` (or the wizard's
  terminal write path), apply the posture-default
  `Tools.AudienceProfiles` mapping for the selected posture
  (Personal: all features on; Team: per-audience defaults per
  posture rule; Enterprise: stricter defaults).
- [ ] 6.2 The cascade SHALL write only `Tools.AudienceProfiles`
  entries that the operator has not explicitly customized in
  `ExistingConfig`. On fresh first-run `ExistingConfig` is null, so
  the full posture default applies.

## 7. Post-flight screen

- [ ] 7.1 Add a post-flight Termina page showing: provider summary
  ("Anthropic — claude-sonnet-4-6"), identity summary ("Netclaw,
  aaron, America/Los_Angeles"), posture, health-check status.
- [ ] 7.2 If health check fails, show the failure message and a
  `[ Back to Posture ]` action that returns to the Posture step.
- [ ] 7.3 If health check passes, show a `[ Done ]` action and the
  nudge text:
  `Run \`netclaw chat\` to start, or \`netclaw config\` to configure
   channels, webhooks, search, and more.`
- [ ] 7.4 On Termina teardown after a successful Done, print the same
  one-line nudge to stderr so it remains visible after the TUI
  clears.
- [ ] 7.5 When `--force` reset was used, append the .bak file paths
  to the post-flight screen and stderr.

## 8. Smoke tape revisions

- [ ] 8.1 Rewrite `tests/smoke/tapes/init-wizard.tape` to exercise
  the three-step flow plus post-flight. Target ≤ 60 lines.
- [ ] 8.2 Rewrite `tests/smoke/assertions/init-wizard.sh` to assert
  only the bootstrap fields: provider config, models config, identity
  files (`SOUL.md`, `TOOLING.md`), posture, and doctor exit code 0
  or 2.
- [ ] 8.3 Delete `tests/smoke/tapes/init-wizard-reverse-proxy.tape`
  and `tests/smoke/assertions/init-wizard-reverse-proxy.sh`.
  Reverse-proxy coverage is owned by `config-exposure-mode.tape`
  from Change B.

## 9. New smoke tapes

- [ ] 9.1 Add `tests/smoke/tapes/init-existing-config-refuse.tape`:
  pre-stage a `netclaw.json`, run `netclaw init`, observe the TTY
  refusal screen, press Enter to acknowledge, assert exit 0.
- [ ] 9.2 Add `tests/smoke/assertions/init-existing-config-refuse.sh`:
  assert the pre-staged config is byte-identical post-run.
- [ ] 9.3 Add `tests/smoke/tapes/init-force-reset.tape`: pre-stage a
  `netclaw.json`, run `netclaw init --force`, type `reset`, confirm,
  complete the three-step flow, assert post-flight Done.
- [ ] 9.4 Add `tests/smoke/assertions/init-force-reset.sh`: assert
  (a) a `netclaw.json.bak.*` file exists with the original content,
  (b) the new `netclaw.json` reflects what the tape typed, (c)
  doctor exits 0 or 2.

## 10. Documentation

- [ ] 10.1 Update `docs/prd/PRD-004-cli-onboarding-and-config.md` to
  replace the "reentrant init dashboard" wording with the documented
  simplified-init + `netclaw config` split. List the three init steps
  and reference `netclaw config` for the rest.
- [ ] 10.2 Cross-reference issues #455 and #1150 in PRD-004's Cross-
  References section.
- [ ] 10.3 Update `feeds/skills/.system/files/netclaw-identity/SKILL.md`
  (per CLAUDE.md system-skills sync rule) so the agent knows the
  trimmed identity field set and the `netclaw config` path for
  per-audience editing. Bump `metadata.version`.
- [ ] 10.4 Update CLI `--help` text so `netclaw init --help` documents
  the trimmed flow and the `--force` flag.

## 11. Quality gates

- [ ] 11.1 `dotnet build` clean.
- [ ] 11.2 `dotnet test` clean: round-trip tests for Provider,
  Identity, Posture still pass against the trimmed Identity field
  set; menu registry audit passes (all editors registered, tapes
  exist, test classes exist).
- [ ] 11.3 `./scripts/smoke/run-smoke.sh init-wizard` passes the
  rewritten tape.
- [ ] 11.4 `./scripts/smoke/run-smoke.sh light` passes (incl. the two
  new init tapes and the 12 `netclaw config` tapes from Change B).
- [ ] 11.5 `dotnet slopwatch analyze` reports no new violations.
- [ ] 11.6 `./scripts/Add-FileHeaders.ps1 -Verify` reports clean.
- [ ] 11.7 `openspec validate simplify-netclaw-init --type change`
  passes.

## 12. Manual acceptance

- [ ] 12.1 Fresh install (no `~/.netclaw/`): `netclaw init` reaches
  working chat in ≤ 3 prompts after provider selection. Verified by
  walking through the wizard manually.
- [ ] 12.2 Re-run init over existing config without `--force`:
  refusal screen renders, Enter acknowledges, exit 0, config
  unchanged.
- [ ] 12.3 Re-run init over existing config with `--force`: confirm
  screen renders, type-to-confirm gate works, .bak files created
  with matching timestamps, fresh three-step flow runs, new config
  written.
- [ ] 12.4 Non-TTY refusal: `netclaw init > /dev/null 2>&1` over an
  existing config exits non-zero.
- [ ] 12.5 PR description references this OpenSpec change ID and
  cross-references #455 (closed in Change A) and #1150 (closed in
  Change B) as already-closed precedents.
