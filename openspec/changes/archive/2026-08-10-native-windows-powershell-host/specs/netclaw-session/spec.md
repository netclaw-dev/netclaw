## ADDED Requirements

### Requirement: Shell-capable sessions receive the exact host identity

The working-context tail for a Personal session SHALL contain the canonical
platform, shell executable, grammar, and PowerShell dialect when applicable.
The tail SHALL appear even when the session has no project directory. Public
and Team context SHALL NOT gain shell capability from this information.

The context SHALL describe only the selected host contract. It SHALL NOT claim
knowledge of profiles, modules, aliases outside the parser catalog, inherited
variables, executable lookup, or external script contents.

#### Scenario: Personal Windows context names PowerShell 7

- **GIVEN** the daemon selected `pwsh.exe` and `PowerShell7`
- **AND** a Personal session has no project directory
- **WHEN** the session builds its working-context tail
- **THEN** the tail names Windows, `pwsh.exe`, PowerShell, and `PowerShell7`
- **AND** the model can select PowerShell syntax for `shell_execute`

#### Scenario: Personal Windows context names the fallback

- **GIVEN** the daemon selected `powershell.exe` and `WindowsPowerShell51`
- **WHEN** a Personal session builds its working-context tail
- **THEN** the tail names the fallback executable and dialect
- **AND** it does not describe PowerShell 7-only syntax as available

#### Scenario: Personal Unix context names Bash

- **GIVEN** the daemon selected `/bin/bash`
- **WHEN** a Personal session builds its working-context tail
- **THEN** the tail names the current Unix platform, `/bin/bash`, and Bash
- **AND** it does not claim a PowerShell dialect

#### Scenario: Child run receives the same host identity

- **GIVEN** a Personal session creates a child run
- **WHEN** the child receives its working-context snapshot
- **THEN** it receives the same canonical shell identity as the parent daemon
- **AND** the child cannot select another shell grammar
