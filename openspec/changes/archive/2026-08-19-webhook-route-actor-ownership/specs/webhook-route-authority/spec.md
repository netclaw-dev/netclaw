# webhook-route-authority Specification (delta)

## ADDED Requirements

### Requirement: Daemon actor is the single mutation authority

The daemon SHALL route every webhook route mutation through one `WebhookRouteActor`. The agent tools `set_webhook` and `delete_webhook` SHALL send messages to the actor and SHALL NOT call the route store directly. The actor SHALL validate a mutation before persistence and SHALL return the validation error to the caller on failure. Disk SHALL remain the canonical store: the actor persists through the route store and holds no journaled state.

#### Scenario: Concurrent agent mutations serialize by mailbox order

- **GIVEN** two sessions that invoke `set_webhook` for the same route at the same time
- **WHEN** both requests reach the actor
- **THEN** the actor applies them one at a time in arrival order
- **AND** the final route file reflects both read-modify-write operations with no lost update

#### Scenario: Validation failure does not persist

- **GIVEN** a `set_webhook` request that fails `WebhookRouteValidator`
- **WHEN** the actor processes it
- **THEN** no file write occurs
- **AND** the caller receives the validator's error

### Requirement: The mutation message is a patch; the merged definition carries the required fields

An upsert message SHALL be a field-level patch. A null field SHALL mean "keep the stored value", so a caller SHALL NOT need to resend a value it does not change. Two patches of different fields on the same route SHALL therefore compose instead of overwrite.

The message SHALL require only the two fields a patch can never inherit from a file: the route name and the authority of the caller. The route name SHALL travel as a validated value object, so no unvalidated name SHALL reach a file path. Every other required field SHALL be enforced on the merged definition by `WebhookRouteValidator`, which SHALL reject a merged route without a prompt and a merged route without a verification secret.

#### Scenario: A patch that blanks a required field is rejected

- **GIVEN** a stored route with a prompt
- **WHEN** an upsert patches the prompt to a blank value
- **THEN** the actor rejects the merged definition with the validator's message
- **AND** the stored route file is unchanged

### Requirement: HTTP resource fronts the actor

The daemon SHALL expose `/api/webhooks` (list), `/api/webhooks/{name}` (get, upsert, delete) as thin handlers that ask the actor. The resource SHALL use the same authentication and exposure-mode rules as the other `/api` surfaces. The change SHALL be additive: no existing endpoint changes. Handlers SHALL map actor results to HTTP statuses: validation failure to 400, unknown route to 404, success to 200 or 204.

#### Scenario: Upsert through the API persists through the actor

- **GIVEN** an authenticated `PUT /api/webhooks/{name}` with a valid route body
- **WHEN** the daemon handles it
- **THEN** the actor validates and persists the route
- **AND** the response is a success status with the stored route

#### Scenario: Unauthenticated access is rejected

- **GIVEN** a request to `/api/webhooks` that does not carry a paired-device credential
- **WHEN** the daemon evaluates it
- **THEN** the request is rejected by the same auth rules as the other `/api` surfaces

### Requirement: CLI route mutations require the daemon

The CLI SHALL send every webhook route mutation to the daemon API. The CLI SHALL NOT write a route file. When the daemon does not answer, or answers 404 for the resource, or refuses the call, the command SHALL fail and SHALL leave every route file unchanged. The failure message SHALL name the state and the remedy. The CLI read subcommands (`list`, `show`, `validate`) SHALL keep reading canonical disk, because disk is the route store and `show` reveals a secret that the API never returns. Argument grammar, `--dry-run`, and the merge preview SHALL run before the daemon call and SHALL keep their own messages and exit codes. The supported daemon-absent path is a route file authored on disk outside the CLI, which the daemon loads at startup.

#### Scenario: Daemon reachable routes through the API

- **GIVEN** a running paired daemon
- **WHEN** the operator runs `netclaw webhooks set`
- **THEN** the CLI sends the mutation to `/api/webhooks/{name}`
- **AND** writes no file itself

#### Scenario: Daemon down fails the command

- **GIVEN** no running daemon
- **WHEN** the operator runs `netclaw webhooks set` with valid arguments
- **THEN** the command fails with exit code 1
- **AND** the error names the daemon as unreachable and tells the operator to start it
- **AND** no route file is created or changed

#### Scenario: Old daemon without the resource fails the command

- **GIVEN** a running daemon that predates the webhook route resource
- **WHEN** the operator runs `netclaw webhooks set` and the probe answers 404
- **THEN** the command fails with exit code 1
- **AND** the error tells the operator to upgrade the daemon
- **AND** no route file is created or changed

#### Scenario: Validation rejection does not bypass the daemon

- **GIVEN** a running daemon that rejects a mutation with a validation error
- **WHEN** the CLI receives the 400 response
- **THEN** the command fails with the validator's message
- **AND** no route file is created or changed

#### Scenario: Dry run needs no daemon

- **GIVEN** an operator who runs `netclaw webhooks set --dry-run`
- **WHEN** the CLI validates the merged route
- **THEN** it reports the result without a daemon call
- **AND** writes no file

### Requirement: Version-skew tolerance without a cross-process lock

The route store SHALL hold no cross-process lock. Version-skew tolerance SHALL rest on two properties instead. First, the actor SHALL hold no cache: every read and every read-modify-write SHALL go through the store to disk, so a direct file write by an old CLI is visible to the next actor operation without a reconciliation step. Second, the store SHALL write each route file atomically through a temporary file and one replacing move, so no reader SHALL see a partial file. The per-route JSON file format SHALL NOT change.

Accepted edge case: if an old CLI patches the same route at the same moment as the daemon actor, one of the two updates is lost. Webhook route mutations are rare, so the project accepts this risk rather than a lock.

#### Scenario: Old CLI writes a file behind the actor

- **GIVEN** a running new daemon and an old CLI that writes a route file directly
- **WHEN** any subsequent read or update reaches the actor
- **THEN** the actor serves the file's current content from disk

#### Scenario: A write never exposes a partial file

- **GIVEN** a reader that opens a route file while the store writes it
- **WHEN** the store replaces the file
- **THEN** the reader sees either the complete old content or the complete new content

### Requirement: Deterministic tests replace scheduling choreography

The serialization guarantee SHALL be tested through actor message ordering and outcome assertions. No test of this capability SHALL assert on thread scheduling, bounded event waits, or elapsed time.

#### Scenario: Serialization test is message-order based

- **GIVEN** the actor test for concurrent mutations
- **WHEN** it runs on a starved thread pool
- **THEN** it still passes or fails only on the serialization outcome
