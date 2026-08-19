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

### Requirement: CLI selects its write path explicitly

The CLI SHALL use the daemon API for webhook route mutations when the daemon is reachable and the resource exists. The CLI SHALL write route files directly only when the daemon is unreachable or an old daemon returns 404 for the resource, and SHALL print one notice on stderr naming the direct-file mode. CLI flags, exit codes, and stdout formats SHALL be identical in both modes. An API error other than unreachable or 404 SHALL fail the command and SHALL NOT fall back to the file path.

#### Scenario: Daemon reachable routes through the API

- **GIVEN** a running paired daemon
- **WHEN** the operator runs `netclaw webhooks set`
- **THEN** the CLI sends the mutation to `/api/webhooks/{name}`
- **AND** writes no file itself

#### Scenario: Daemon down falls back with a disclosed mode

- **GIVEN** no running daemon
- **WHEN** the operator runs `netclaw webhooks set` with valid arguments
- **THEN** the CLI writes the route file directly
- **AND** prints one stderr notice that names the direct-file mode
- **AND** stdout and the exit code match the API-mode success shape

#### Scenario: Validation rejection does not bypass the daemon

- **GIVEN** a running daemon that rejects a mutation with a validation error
- **WHEN** the CLI receives the 400 response
- **THEN** the command fails with the validator's message
- **AND** no direct file write occurs

### Requirement: Version-skew tolerance for one deprecation release

The route store SHALL keep its named cross-process mutex for one deprecation release. The actor SHALL reconcile external route-file changes through the existing hot-reload signal, so a direct file write by an old CLI is visible to actor reads after reconciliation. The per-route JSON file format SHALL NOT change.

#### Scenario: Old CLI writes a file behind the actor

- **GIVEN** a running new daemon and an old CLI that writes a route file directly
- **WHEN** the hot-reload signal fires for that file
- **THEN** the actor re-reads the route from disk
- **AND** subsequent reads through the actor return the file's content

### Requirement: Deterministic tests replace scheduling choreography

The serialization guarantee SHALL be tested through actor message ordering and outcome assertions. No test of this capability SHALL assert on thread scheduling, bounded event waits, or elapsed time.

#### Scenario: Serialization test is message-order based

- **GIVEN** the actor test for concurrent mutations
- **WHEN** it runs on a starved thread pool
- **THEN** it still passes or fails only on the serialization outcome
