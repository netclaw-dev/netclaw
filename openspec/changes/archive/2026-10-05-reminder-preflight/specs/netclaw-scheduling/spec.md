## ADDED Requirements

### Requirement: run_reminder agent tool

The system SHALL provide a `run_reminder` tool in the `scheduling` grant
category. The tool SHALL take a reminder ID and SHALL return the exact prompt
that the scheduled run of that reminder sends, with an instruction to carry it
out in the current chat. The tool SHALL NOT start a separate run, change the
schedule, or write a reminder history record.

The tool SHALL return the prompt only when all of these are true:

- the current turn has a person who can answer approval prompts;
- the reminder audience is at or below the caller audience (else the reminder
  reads as not found);
- the reminder audience equals the chat audience.

#### Scenario: Same-audience chat gets the exact prompt
- **WHEN** a Personal chat with interactive approval calls `run_reminder` for a Personal reminder
- **THEN** the result contains the same prompt text that the scheduled run sends
- **AND** the schedule and the reminder history do not change

#### Scenario: Wider chat is refused
- **WHEN** a Personal chat calls `run_reminder` for a Team reminder
- **THEN** the result is an error that tells the user to run the test in a Team chat
- **AND** the result does not contain the reminder prompt

#### Scenario: Reminder above the caller reads as not found
- **WHEN** a Team chat calls `run_reminder` for a Personal reminder
- **THEN** the result is the same not-found error as for a missing ID

#### Scenario: Unattended caller is refused
- **WHEN** a turn with no interactive approval (a reminder, a webhook, or headless chat) calls `run_reminder`
- **THEN** the result is an error and contains no prompt

#### Scenario: Grant saved in the chat test lets the scheduled run pass
- **GIVEN** a Personal reminder whose prompt runs a shell command that needs approval in a folder outside the trusted roots
- **WHEN** the user runs the prompt in a Personal chat after `run_reminder` and answers "Always here"
- **AND** the reminder later fires unattended with the same command
- **THEN** the scheduled run reads the saved grant and runs the command with no prompt

#### Scenario: Scheduled run without a saved grant is denied
- **WHEN** the same reminder fires unattended and no grant covers the command
- **THEN** the scheduled run denies the command

### Requirement: Reminder test entry points

The system SHALL provide a `run-reminder` system skill. `/run-reminder <id>`
SHALL load it. The skill SHALL warn the user that the steps are real before
it calls `run_reminder`. The CLI command `netclaw reminder run <id>` SHALL open
a normal chat whose hidden first message is `/run-reminder <id>`.

#### Scenario: CLI opens a test chat
- **WHEN** the operator runs `netclaw reminder run disk-check`
- **THEN** the CLI opens the chat page on a new session
- **AND** the session receives `/run-reminder disk-check` as its first message

#### Scenario: CLI usage error
- **WHEN** the operator runs `netclaw reminder run` with no ID
- **THEN** the CLI prints `Usage: netclaw reminder run <id>` and exits non-zero
