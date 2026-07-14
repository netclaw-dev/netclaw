## ADDED Requirements

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

#### Scenario: Daemon shutdown owns local child cleanup

- **GIVEN** an enabled local STDIO MCP server is connected
- **WHEN** the Netclaw daemon stops
- **THEN** Netclaw disposes the configured MCP client
- **AND** the client transport terminates its owned child process

### Requirement: Configured STDIO command is launched without server-specific rewriting

The system SHALL pass the configured command and arguments to a local STDIO MCP transport without adding arguments based on the server name, command text, or implementation identity.

#### Scenario: Playwright arguments pass through unchanged

- **GIVEN** a local STDIO profile invokes the Playwright MCP package without `--isolated`
- **WHEN** Netclaw creates its transport
- **THEN** the launched argument list does not contain an implicitly added `--isolated` argument

#### Scenario: Explicit isolation argument is preserved

- **GIVEN** a local STDIO profile explicitly configures `--isolated`
- **WHEN** Netclaw creates its transport
- **THEN** the launched argument list contains the configured argument exactly once
