## ADDED Requirements

### Requirement: MCP tool and prompt notification compatibility

The system SHALL listen for tool and prompt list changes on each published MCP client generation.

For MCP revision 2026-07-28, the system SHALL use one `subscriptions/listen` request.
It SHALL enable only the event types in the matching acknowledgement.

For older revisions, the system SHALL accept direct list-change notifications only for capabilities that declare `listChanged` support.

#### Scenario: Modern server accepts both event types

- **GIVEN** a server negotiates MCP revision 2026-07-28
- **AND** it acknowledges tool and prompt list changes
- **WHEN** it sends either accepted notification with the matching subscription identifier
- **THEN** the system requests a catalog refresh without waiting for the poll interval

#### Scenario: Modern server accepts one event type

- **GIVEN** a server negotiates MCP revision 2026-07-28
- **AND** it acknowledges only tool list changes
- **WHEN** it sends a prompt list notification
- **THEN** the system does not request a refresh for that notification
- **AND** the existing poll remains active

#### Scenario: Legacy server declares direct notifications

- **GIVEN** a server negotiates a revision before 2026-07-28
- **AND** its tool capability declares `listChanged`
- **WHEN** it sends a direct tool list-change notification
- **THEN** the system requests a catalog refresh without a `subscriptions/listen` request

#### Scenario: Server declares no notification support

- **GIVEN** a server does not support modern or legacy catalog notifications
- **WHEN** the connection becomes healthy
- **THEN** the system keeps the existing catalog poll active
- **AND** the connection remains healthy

### Requirement: Notification refresh preserves atomic catalog generations

The system SHALL list the complete supported tool and prompt candidate before it publishes a notification refresh.
It SHALL publish one immutable generation only when the complete catalog fingerprint changes.

It SHALL keep the last good generation after any list failure.
It SHALL retain one active refresh and at most one queued follow-up refresh for each server.

#### Scenario: Tool notification changes the catalog

- **GIVEN** a connected server has a published tool and prompt generation
- **WHEN** a tool notification starts a successful refresh with a changed fingerprint
- **THEN** the system publishes one new generation with the complete tool and prompt catalog

#### Scenario: Duplicate notifications do not create unbounded work

- **GIVEN** one notification refresh is active
- **WHEN** the server sends repeated tool and prompt notifications
- **THEN** the system queues at most one follow-up refresh
- **AND** it does not run concurrent catalog refreshes for that server

#### Scenario: Notification refresh finds no change

- **GIVEN** a connected server sends a supported notification
- **WHEN** the complete catalog fingerprint is unchanged
- **THEN** the system keeps the current generation
- **AND** it resets the poll interval after the successful check

#### Scenario: Notification refresh fails

- **GIVEN** a connected server has a last good generation
- **WHEN** a notification refresh cannot list the complete catalog
- **THEN** the system keeps the last good generation
- **AND** a later notification or poll can retry the refresh

### Requirement: MCP catalog notification lease lifecycle

Each MCP client candidate SHALL own one notification lease.
The system SHALL install its handlers before client creation and activate refresh work only after publication.

The system SHALL deactivate and dispose the lease when it replaces or disposes its client.
A stale lease SHALL NOT refresh a later generation.

#### Scenario: Notification arrives before publication

- **GIVEN** a candidate client receives a supported notification during initialization
- **WHEN** the system publishes that candidate
- **THEN** its lease processes the queued notification against the published generation

#### Scenario: Reconnect renews the lease

- **GIVEN** a server has a published connection and notification lease
- **WHEN** the system publishes a replacement connection
- **THEN** the replacement owns a new notification lease
- **AND** the old lease cannot refresh the replacement generation

#### Scenario: Shutdown removes notification work

- **GIVEN** a published connection has an active notification lease
- **WHEN** daemon shutdown disposes the connection
- **THEN** the system stops the lease worker
- **AND** it disposes the client without leaked notification work

### Requirement: MCP notification failure and repair behavior

The system SHALL keep a usable MCP connection and the existing poll after notification setup or listener failure.
It SHALL report the compatibility mode and failures through safe structured logs.

#### Scenario: Modern subscription method is unsupported

- **GIVEN** a server negotiates MCP revision 2026-07-28
- **WHEN** `subscriptions/listen` returns an unsupported-method error
- **THEN** the system keeps the connection and catalog available
- **AND** the existing poll remains the repair path
- **AND** the system logs the failure category without raw protocol content

#### Scenario: Modern acknowledgement times out

- **GIVEN** a server accepts the listen request but sends no matching acknowledgement
- **WHEN** the 15-second `TimeProvider` timeout expires
- **THEN** the system keeps the connection and catalog available
- **AND** the existing poll remains the repair path

#### Scenario: Listener closes after publication

- **GIVEN** a modern notification listener is active
- **WHEN** its request ends unexpectedly
- **THEN** the system disables that notification lease
- **AND** it logs a warning
- **AND** the existing poll remains the repair path

#### Scenario: Poll repairs a missed notification

- **GIVEN** a connected server changes its catalog without a usable notification
- **WHEN** the next catalog poll succeeds
- **THEN** the system publishes the repaired catalog through the same generation rules
