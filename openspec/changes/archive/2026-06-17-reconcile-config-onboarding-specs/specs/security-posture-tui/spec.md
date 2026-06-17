# security-posture-tui Delta Spec

Reconciles `openspec/specs/security-posture-tui/spec.md` to shipped code as of
the `simplify-netclaw-init` refactor. Two requirements are corrected; one new
post-install requirement is added.

---

## MODIFIED Requirements

### Requirement: Security posture selection step

The wizard SHALL present an interactive step where the user selects a
deployment posture (Personal, Team, or Public) with explanatory text for
each option.

#### Scenario: User selects Personal posture

- **GIVEN** the wizard is at the SecurityPosture step
- **WHEN** the user selects "Personal"
- **THEN** deployment posture is set to Personal in WizardContext
- **AND** shell execution mode defaults to HostAllowed
- **AND** audience profiles are seeded with Personal-posture defaults

#### Scenario: User selects Team posture

- **GIVEN** the wizard is at the SecurityPosture step
- **WHEN** the user selects "Team"
- **THEN** deployment posture is set to Team in WizardContext
- **AND** shell execution mode defaults to Off
- **AND** audience profiles are seeded with Team-posture defaults

#### Scenario: User selects Public posture

- **GIVEN** the wizard is at the SecurityPosture step
- **WHEN** the user selects "Public"
- **THEN** deployment posture is set to Public in WizardContext
- **AND** shell execution mode defaults to Off
- **AND** audience profiles are seeded with Public-posture defaults

> **Rationale:** The posture step writes `DeploymentPosture`, `ShellExecutionMode`,
> and `AudienceProfiles` into `WizardContext`. Channel and DM audience defaults are
> NOT applied here; they are derived from `WizardContext.SelectedPosture` by the
> channel-picker step (e.g. `SlackStepViewModel.OnLeave`) when it builds
> `ChannelEntry` records. Removing the old per-posture DM/channel assertions
> prevents false specification of where those values originate.

---

### Requirement: Posture step position in wizard flow

The SecurityPosture step SHALL appear after the Provider step and before the
Feature Selection step in the wizard flow. The Provider step combines LLM
provider selection and authentication/chat-service configuration; there is no
separate ChatServices step. For non-Personal postures, the Feature Selection
step SHALL appear immediately after SecurityPosture so that feature
availability is configured before channel audience assignment.

#### Scenario: Step order with Feature Selection

- **WHEN** the user completes the SecurityPosture step
- **AND** the selected posture is Team or Public
- **THEN** the next step is Feature Selection
- **AND** after Feature Selection, the next applicable step follows

#### Scenario: Step order without Feature Selection

- **WHEN** the user completes the SecurityPosture step
- **AND** the selected posture is Personal
- **THEN** the Feature Selection step is skipped
- **AND** the next applicable step follows directly

> **Rationale:** `InitWizardViewModel` builds the step sequence as
> `Provider → Identity → SecurityPosture → FeatureSelection → HealthCheck`.
> The old spec named "ChatServices" as the preceding step, which no longer
> exists; chat-service auth is part of the Provider step.

---

## ADDED Requirements

### Requirement: Post-install posture cascade in netclaw config

A posture change in `netclaw config` with customized audience profiles SHALL require a cascade confirmation before writing.

When the operator changes the deployment posture via `netclaw config` and the
existing audience profiles have been customized (differ from the current
posture's defaults), the editor SHALL present a three-option cascade
confirmation before writing any changes:

- **Cancel** — abort the posture change; leave posture and profiles untouched.
- **Apply new posture, overwrite profiles** — save the new posture and reset
  all audience profiles to the new posture's defaults.
- **Apply new posture, keep custom profiles** — save the new posture and shell
  defaults only; leave existing audience profile overrides in place.

The editor MUST NOT apply the posture change without this confirmation when
profiles are customized. If profiles are at their posture defaults (not
customized), the editor SHALL apply the new posture directly without
presenting the cascade screen.

#### Scenario: Posture change with customized profiles triggers cascade

- **GIVEN** the operator opens `netclaw config` → Security → Security Posture
- **AND** the current audience profiles differ from the current posture's
  defaults (i.e. `AudienceProfilesCustomized()` returns true)
- **WHEN** the operator selects a different posture and confirms
- **THEN** the editor transitions to the PostureCascade confirmation screen
- **AND** no config file changes are written yet

#### Scenario: Cascade — cancel preserves existing state

- **GIVEN** the PostureCascade screen is showing
- **WHEN** the operator selects "Cancel - keep current posture"
- **THEN** the pending posture is discarded
- **AND** the editor returns to the Posture selection screen
- **AND** the config file is unchanged

#### Scenario: Cascade — overwrite applies posture and resets profiles

- **GIVEN** the PostureCascade screen is showing
- **WHEN** the operator selects "Apply new posture, overwrite profiles"
- **THEN** the new posture and its shell execution mode are written to config
- **AND** all audience profiles are reset to the new posture's defaults
- **AND** the editor returns to the appropriate next screen

#### Scenario: Cascade — keep custom applies posture only

- **GIVEN** the PostureCascade screen is showing
- **WHEN** the operator selects "Apply new posture, keep custom profiles"
- **THEN** the new posture and its shell execution mode are written to config
- **AND** existing audience profile overrides are preserved unchanged

#### Scenario: Posture change without customized profiles applies directly

- **GIVEN** the operator opens `netclaw config` → Security → Security Posture
- **AND** the current audience profiles match the current posture's defaults
- **WHEN** the operator selects a different posture and confirms
- **THEN** the new posture is applied immediately (no cascade screen)
- **AND** audience profiles are reset to the new posture's defaults

#### Scenario: Selecting the already-active posture is a no-op

- **GIVEN** the operator opens the posture editor
- **WHEN** the operator selects the posture that is already active
- **THEN** no changes are written to the config file
- **AND** a status message informs the operator that the posture is already active
