## ADDED Requirements

### Requirement: Bare exit-status output preserves static shell approval candidates

Netclaw SHALL keep other static shell candidates reusable when a complete approval-exempt output command has a bare `$?` argument.

The exception SHALL require a static output verb, a complete parser tree, no redirect, and a parser-classified non-path status argument.

Netclaw SHALL apply each other verb's normal [approval](../../../../../docs/spec/GLOSSARY.md#approval) and path scope. The exception SHALL grant no new verb or path authority.

#### Scenario: An approved command precedes exit-status output

- **GIVEN** a session grant covers `git push` in the current directory
- **WHEN** the agent calls `shell_execute` with `git push; echo $?`
- **THEN** Netclaw allows the call without another prompt
- **AND** Netclaw does not store an approval for `echo`

#### Scenario: An unapproved command still needs consent

- **GIVEN** no grant covers `git push`
- **WHEN** the agent calls `shell_execute` with `git push; echo $?`
- **THEN** Netclaw requests approval for `git push`
- **AND** Netclaw offers reusable scopes for that static candidate

#### Scenario: An executable substitution keeps its authority gate

- **WHEN** the agent calls `shell_execute` with `echo "$(touch /tmp/marker)"`
- **THEN** Netclaw does not exempt the `touch` occurrence
- **AND** the call does not run without the required approval

#### Scenario: A redirect keeps its path gate

- **WHEN** the agent calls `shell_execute` with `echo $? > /tmp/marker`
- **THEN** Netclaw does not treat `echo` as an approval-exempt output command
- **AND** the redirect target keeps its normal path and approval checks

#### Scenario: An unknown path remains strict

- **WHEN** the agent calls `shell_execute` with `grep "$TARGET"; echo $?`
- **THEN** the unknown `grep` path keeps the whole call on the one-time path
- **AND** the output exception does not create a reusable `grep` candidate

#### Scenario: An unknown positional parameter remains strict

- **WHEN** the agent calls `shell_execute` with `echo $@`
- **THEN** Netclaw keeps the call on the one-time path
- **AND** the output exception does not create a reusable candidate
