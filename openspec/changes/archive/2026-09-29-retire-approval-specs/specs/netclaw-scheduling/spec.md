## ADDED Requirements

### Requirement: Fail-closed reminder write validation

Reminder write surfaces SHALL validate reminder audience server-side before
persisting or importing a reminder definition. This applies to REST, admin,
CLI, and import paths in addition to conversational tool calls. Invalid
audience values, missing required authority context, or requested audiences
that exceed the caller's source authority SHALL be rejected with clear error
messages. Execution may trust the stored reminder audience because minting-time
validation is mandatory.

#### Scenario: REST create rejects invalid audience value

- **GIVEN** a REST reminder create request provides `audience: "superuser"`
- **WHEN** the server validates the request
- **THEN** the request is rejected with a clear validation error
- **AND** no reminder definition is persisted

#### Scenario: Admin import rejects over-privileged reminder

- **GIVEN** an admin or import request is authenticated with source audience `Team`
- **WHEN** the request submits a reminder definition with stored audience `Personal`
- **THEN** the server rejects the request with a clear over-privilege error
- **AND** the reminder is not written to disk

#### Scenario: Write path fails closed without authority context

- **GIVEN** a non-conversational reminder write path cannot determine the caller's source audience / authority
- **WHEN** the request attempts to create or import a reminder definition
- **THEN** the server rejects the request
- **AND** the error states that reminder audience authorization context is required

#### Scenario: Execution trusts stored audience after validated minting

- **GIVEN** a reminder definition was accepted by the server's minting validation
- **WHEN** the reminder executes later on a timer
- **THEN** the execution path uses the stored audience as authoritative
- **AND** no deployment-default fallback broadens that audience
