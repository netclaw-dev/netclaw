## ADDED Requirements

### Requirement: Autonomous sessions are confined to a filesystem zone

For autonomous (non-interactive) channels (`SupportsInteractiveApproval == false`), every filesystem-touching tool (shell, `file_read`, `file_write`, `file_edit`, `file_list`, `attach_file`) SHALL be confined to an autonomous filesystem zone regardless of the session's audience. The zone is derived from the execution context — the session directory and the current project directory — for write and attach access, and SHALL fail closed when empty. Read access additionally includes the non-sensitive global read roots (skills, identity, workspaces), so an autonomous session may read across the project tree but write only within its session or current project. The clamp narrows and never widens: it replaces an unrestricted (`Mode.All`) audience allowance with the zone, and never grants access beyond what the audience already permits. Interactive channels are unaffected — they remain governed by audience filesystem mode and configuration, with the live approval gate as the backstop for the missing confinement.

#### Scenario: Autonomous Personal session is confined despite Mode.All

- **GIVEN** an autonomous channel resolving to the Personal audience whose write filesystem mode is `All`
- **WHEN** a tool attempts to access a path outside the autonomous zone (e.g. `~/.ssh/id_rsa`)
- **THEN** access is denied
- **AND** the same access from an interactive Personal session remains permitted by the audience's `Mode.All`

#### Scenario: Autonomous access within the zone is permitted

- **GIVEN** an autonomous Personal session whose project directory is part of the zone
- **WHEN** a tool accesses a path inside the project directory
- **THEN** access is permitted, subject to the approval gate and protected-path policy

#### Scenario: Clamp never widens a more-restricted audience

- **GIVEN** an autonomous Public session whose audience file roots are session-scoped only
- **WHEN** the autonomous zone also lists the project directory
- **THEN** the effective roots remain session-scoped — the zone does not grant the Public session project-directory access

#### Scenario: All filesystem tools share the clamp

- **GIVEN** an autonomous Personal session
- **WHEN** it attempts a `file_read` of a path outside the zone after a shell read of the same path was denied
- **THEN** the `file_read` is denied as well — confinement is not shell-specific
