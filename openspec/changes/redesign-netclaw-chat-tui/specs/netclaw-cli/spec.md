## ADDED Requirements

### Requirement: TUI applications select an explicit presentation mode

Each Termina application SHALL select its presentation mode through runtime
configuration. `netclaw chat` SHALL select Inline. Existing setup, config,
picker, approval, and dashboard applications SHALL retain FullScreen unless a
separate approved change selects another mode.

#### Scenario: Chat selects Inline

- **WHEN** the CLI builds the `netclaw chat` Termina application
- **THEN** its runtime options select Inline presentation
- **AND** its scroll policy leaves wheel input and selection with the terminal

#### Scenario: Init retains FullScreen

- **WHEN** the CLI builds the `netclaw init` Termina application
- **THEN** its runtime options select FullScreen or use the FullScreen default
- **AND** the change to chat does not alter the init screen lifecycle

### Requirement: Session selection crosses the presentation boundary cleanly

The full-screen session picker SHALL close before Netclaw starts inline chat.
The picker SHALL pass the selected session ID through an explicit launch result.
The CLI SHALL NOT switch screen-buffer contracts during page navigation.

#### Scenario: Select a session from the picker

- **GIVEN** the full-screen session picker shows a stored session
- **WHEN** the operator confirms that session
- **THEN** the picker exits and restores the primary terminal buffer
- **AND** the CLI starts a new inline chat application with that session ID

#### Scenario: Session launch fails

- **GIVEN** the picker exits with a selected session ID
- **WHEN** the inline chat application cannot start
- **THEN** the CLI reports the launch failure in the primary buffer
- **AND** the CLI does not reopen the picker or start a fallback chat silently

### Requirement: Chat reserves console output ownership

The chat host SHALL suppress or reroute framework console logging while the
inline host owns a live region. Diagnostic logs SHALL remain available through
the configured file or structured log path.

#### Scenario: Daemon client reports a warning

- **GIVEN** inline chat owns a live region
- **WHEN** a daemon client component records a warning
- **THEN** the warning reaches the configured diagnostic log
- **AND** no direct console line corrupts the live region
