## ADDED Requirements

### Requirement: TA-17 Shell Once consent binds exact source

A shell `Once` answer SHALL cover a retry only when the exact structured `Command` value matches the source that received consent. The comparison SHALL be case-sensitive. Prompt display text SHALL NOT define source identity. A persistent grant SHALL NOT use this source identity.

For a recovered prompt, Netclaw SHALL derive source identity from the original assistant tool call in session history. If that call has no valid structured shell command, Netclaw SHALL NOT dispatch the retry with the old `Once` answer.
Netclaw SHALL stop recovery when a call ID is ambiguous or the original canonical tool name does not match the recovered approval.

#### Scenario: The same shell source retries once
- **GIVEN** an operator approved a shell call with `Once`
- **WHEN** Netclaw retries the exact same structured `Command` value
- **THEN** the retry passes the one-time consent check
- **AND** the consent does not cover a later call

#### Scenario: A changed source with the same candidates needs consent
- **GIVEN** an operator approved a shell call with `Once`
- **AND** a different Bash source produces the same approval candidates
- **WHEN** Netclaw checks the changed source
- **THEN** Netclaw requests consent again

#### Scenario: Recovery lacks the original shell command
- **GIVEN** a recovered approval answer for a shell call
- **AND** the original assistant tool call has missing or malformed command arguments
- **WHEN** Netclaw plans the retry
- **THEN** Netclaw does not dispatch the shell call with the old one-time consent
- **AND** Netclaw does not use prompt display text as a fallback

#### Scenario: Recovery has ambiguous call identity
- **GIVEN** a recovered shell approval
- **AND** session history has duplicate call IDs or a different canonical tool name
- **WHEN** Netclaw plans the retry
- **THEN** Netclaw abandons the retry without using the old one-time consent
