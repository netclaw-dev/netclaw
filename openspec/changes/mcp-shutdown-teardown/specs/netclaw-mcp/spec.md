## MODIFIED Requirements

### Requirement: Configured MCP server has daemon-bound client ownership

The system SHALL maintain at most one live MCP client connection for each enabled configured MCP server within a daemon process. For a local STDIO server, that connection SHALL own the server child process and SHALL be shared by every Netclaw session authorized to invoke the server.

#### Scenario: Different sessions invoke one local STDIO server

- **GIVEN** a local STDIO MCP server is enabled and available to two authorized sessions
- **WHEN** both sessions invoke tools from that server
- **THEN** both invocations use the same configured MCP client connection
- **AND** Netclaw does not launch a child process for either session identity

#### Scenario: Session identity does not partition MCP state

- **GIVEN** an authorized session changes state held by an MCP server
- **WHEN** another authorized session invokes that server
- **THEN** the second invocation uses the same daemon-scoped server state

#### Scenario: Daemon shutdown starts child cleanup before session drain completes

- **GIVEN** one or more enabled local STDIO MCP servers are connected
- **WHEN** the Netclaw daemon begins graceful shutdown (SIGTERM or `daemon stop`)
- **THEN** Netclaw begins disposing configured MCP clients at the point the host signals application stop, without waiting for actor-system shutdown or session drain to complete
- **AND** each disposed client's transport terminates its owned child process
- **AND** MCP teardown and session drain proceed concurrently rather than one strictly after the other

#### Scenario: MCP teardown across multiple servers runs in parallel

- **GIVEN** more than one enabled local STDIO MCP server is connected
- **WHEN** the Netclaw daemon begins graceful shutdown
- **THEN** Netclaw disposes all configured MCP clients concurrently
- **AND** total MCP teardown time is bounded by the slowest single server's dispose, not the sum across all configured servers

#### Scenario: Teardown is idempotent across shutdown paths

- **GIVEN** MCP teardown has already run once during daemon shutdown
- **WHEN** the host's normal hosted-service stop sequence subsequently invokes MCP shutdown again
- **THEN** the repeated teardown observes already-disposed clients
- **AND** it does not log a warning or error for the already-disposed state
- **AND** it does not attempt to reconnect or relaunch a child process

#### Scenario: No reconnect once teardown has started

- **GIVEN** MCP daemon shutdown has begun and client teardown is in progress or complete
- **WHEN** an in-flight tool call's failure-recovery path, or the periodic MCP reconnection check, attempts to reconnect to a configured server
- **THEN** Netclaw does not create a new client connection or launch a new child process
- **AND** the reconnect attempt reports failure without side effects

#### Scenario: In-flight tool call fails cleanly when teardown begins

- **GIVEN** a session has an MCP tool call in flight against a local STDIO server
- **WHEN** daemon shutdown begins and that server's client is disposed
- **THEN** the in-flight call fails within the same bounded time as the client's own dispose
- **AND** the caller receives an attributed tool error rather than an indefinite hang
