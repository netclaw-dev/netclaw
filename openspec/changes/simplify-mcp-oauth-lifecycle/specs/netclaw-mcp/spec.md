# netclaw-mcp Delta Specification

## MODIFIED Requirements

### Requirement: Configured MCP server has daemon-bound client ownership

The system SHALL maintain at most one published MCP client generation for each
enabled configured MCP server within a daemon process, including under
concurrent reconnect attempts. `McpClientManager` SHALL be the sole runtime
owner of client creation, publication, replacement, and disposal. Each
published connection SHALL be an immutable snapshot (client, tool map, and
status metadata) carrying a monotonically increasing generation. Concurrent
reconnect requests that observed the same generation SHALL coalesce to a
single replacement attempt. A replacement candidate SHALL initialize
completely — including tool listing — before it atomically replaces the
published generation, and the replaced generation SHALL be disposed exactly
once only after its in-flight invocations finish. An unpublished candidate MAY
coexist with the published generation during initialization. For a local STDIO
server, the connection SHALL own the server child process and SHALL be shared by
every Netclaw session authorized to invoke the server.

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

#### Scenario: Concurrent reconnect requests coalesce

- **GIVEN** multiple callers concurrently request reconnection after observing the same connection generation
- **WHEN** the reconnect attempts run
- **THEN** exactly one replacement candidate is created
- **AND** every caller observes or reuses the same winning generation
- **AND** no client instance is leaked or disposed more than once

#### Scenario: Failed replacement retains the prior generation

- **GIVEN** a published healthy connection
- **WHEN** a replacement candidate fails to initialize
- **THEN** only the candidate is disposed
- **AND** the previously published connection and its tools remain available
- **AND** the server's status does not advertise an empty tool set

#### Scenario: Replacement drains the prior generation

- **GIVEN** an invocation is using the published generation
- **WHEN** a replacement generation is initialized and published
- **THEN** the invocation may finish against the prior generation
- **AND** the prior generation is disposed exactly once after its final in-flight invocation finishes

#### Scenario: Shutdown racing reconnect leaks nothing

- **GIVEN** a reconnect attempt is in progress
- **WHEN** daemon shutdown begins
- **THEN** no new connection is published after shutdown starts
- **AND** every created client is disposed

#### Scenario: Shutdown bounds active invocation drain

- **GIVEN** an invocation holds a lease on a published generation
- **WHEN** daemon shutdown begins
- **THEN** new leases and reconnects are rejected
- **AND** shutdown allows a bounded drain period before cancelling remaining invocations
- **AND** the generation is disposed after the invocation exits

### Requirement: Graceful degradation

Tool calls to unavailable MCP servers SHALL return a clear error message to
the agent. The agent SHALL continue operating with remaining available tools.
The system SHALL attempt reconnection on the next tool call to a previously
unavailable server. Reconnection SHALL be triggered only by
classified transport or session failures: caller cancellation SHALL propagate
immediately without teardown or retry, and tool-declared or application
errors SHALL be returned without reconnecting. A classified transport failure
SHALL trigger at most one coalesced reconnection for later calls. The failed
tool invocation SHALL NOT be replayed automatically because the remote side
effect may have completed before the failure became visible.

#### Scenario: Unavailable server returns clear error

- **GIVEN** a configured MCP server is unreachable
- **WHEN** the agent invokes a tool from that server
- **THEN** a clear error is returned indicating the server is unavailable
- **AND** the agent continues the conversation with remaining tools

#### Scenario: Reconnection on next call

- **GIVEN** an MCP server was previously unreachable
- **WHEN** the agent invokes a tool from that server again
- **THEN** the system attempts reconnection before returning an error

#### Scenario: Partial server availability

- **GIVEN** two MCP servers are configured and one is unreachable
- **WHEN** a session initializes
- **THEN** tools from the reachable server are available
- **AND** tools from the unreachable server are marked as unavailable

#### Scenario: Caller cancellation does not tear down a healthy client

- **GIVEN** a tool invocation in flight on a healthy shared connection
- **WHEN** the caller's cancellation token fires
- **THEN** the cancellation propagates to the caller immediately
- **AND** the shared connection is not disposed, reconnected, or retried

#### Scenario: Tool-declared errors are results, not failures

- **GIVEN** an MCP tool returns a tool-declared error
- **WHEN** the invocation completes
- **THEN** the error is formatted as a tool result
- **AND** no reconnection or retry occurs

#### Scenario: Transport failure reconnects without replay

- **GIVEN** a tool invocation fails with a classified transport failure
- **WHEN** the system handles the failure
- **THEN** the failed invocation is returned as an error without automatic replay
- **AND** the system performs at most one coalesced reconnection for later calls

### Requirement: MCP diagnostics visibility

The system SHALL expose MCP server health in diagnostics. Connection status
SHALL distinguish `AwaitingAuth` (no usable authorization and interaction is
required), `AuthFailed` (credentials or refresh were rejected), `Unreachable`
(transport or network failure), and `Connected` (published usable
generation). Connection state, tool count, and error information SHALL be
updated together from the same lifecycle operation. Failure status SHALL carry
the last error timestamp from `TimeProvider`; successful recovery SHALL update
state and tool count without fabricating a new failure timestamp.

#### Scenario: Server becomes unavailable

- **WHEN** a configured MCP server is unreachable
- **THEN** diagnostics mark it degraded or unavailable with last error timestamp

#### Scenario: Recovery preserves truthful failure timing

- **GIVEN** a server status contains a last error timestamp
- **WHEN** a later generation connects successfully
- **THEN** diagnostics report `Connected` with the new tool count
- **AND** any retained last-failure timestamp still identifies the actual failure time rather than the recovery time

#### Scenario: Daemon reports MCP auth failure

- **GIVEN** the daemon can reach the MCP server but authentication is rejected on the live runtime path
- **WHEN** the operator runs `netclaw mcp list` or `netclaw doctor`
- **THEN** the CLI reports `auth failed`
- **AND** remediation points to `netclaw mcp auth <name>` when OAuth is in use

#### Scenario: Doctor cannot verify OAuth auth offline

- **GIVEN** an HTTP/SSE MCP server uses OAuth
- **AND** the daemon is unavailable
- **WHEN** the operator runs `netclaw doctor`
- **THEN** doctor may report offline connectivity evidence
- **BUT** it SHALL not claim the server is unauthorized unless the daemon runtime path has verified that auth failure

#### Scenario: Expired token without refresh token names the remedy

- **GIVEN** a server whose stored access token is expired and whose record holds no refresh token
- **WHEN** the daemon attempts to connect
- **THEN** the status is `AwaitingAuth` rather than a generic connection error
- **AND** diagnostics state that reauthorization is required via `netclaw mcp auth <name>`
