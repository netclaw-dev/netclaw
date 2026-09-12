Use the [Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md) for tool call, tool result, tool receipt, transport or session failure, application error, tool-declared error, and OAuth-managed server.

## MODIFIED Requirements

### Requirement: Graceful degradation

Tool calls to unavailable MCP servers SHALL return a clear error message to
the agent. The agent SHALL continue operating with remaining available tools.
The system SHALL attempt reconnection on the next tool call to a previously
unavailable server. Reconnection SHALL be triggered only by
classified transport or session failures: caller cancellation SHALL propagate
immediately without teardown or retry, and tool-declared or application
errors SHALL be returned without reconnecting. The engineering glossary
defines transport or session failure and application error. An HTTP answer
with a status code other than 404 SHALL count as an application error. The
same rule SHALL apply to an MCP prompt load: an application error SHALL return
a failed load result without a reconnect. A classified transport failure
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

#### Scenario: Application-level HTTP status does not reconnect

- **GIVEN** a tool invocation fails with an HTTP 500 or HTTP 429 response from a connected server, for example a rate-limit answer `{"statusCode":429,"error":"Too Many Requests","message":"Rate limit exceeded, retry in 52 seconds"}`
- **WHEN** the system handles the failure
- **THEN** the failure is returned as an error that names the HTTP status
- **AND** the published connection generation does not change
- **AND** no reconnection or retry occurs

#### Scenario: Session expiry still reconnects

- **GIVEN** a tool invocation fails with an HTTP 404 response
- **WHEN** the system handles the failure
- **THEN** the failed invocation is returned as an error without automatic replay
- **AND** the system performs at most one coalesced reconnection for later calls

#### Scenario: Prompt load with an application-level HTTP status returns a failed result

- **GIVEN** an MCP prompt load fails with an HTTP 500 response from a connected server
- **WHEN** the system handles the failure
- **THEN** the skill load returns a failed result that names the prompt and the failure
- **AND** the published connection generation does not change
- **AND** no exception reaches the tool dispatcher

### Requirement: MCP diagnostics visibility

The system SHALL expose MCP server health in diagnostics. Connection status
SHALL distinguish `AwaitingAuth` (no usable authorization and interaction is
required), `AuthFailed` (credentials or refresh were rejected), `Unreachable`
(transport or network failure), and `Connected` (published usable
generation). Connection state, tool count, and error information SHALL be
updated together from the same lifecycle operation. Failure status SHALL carry
the last error timestamp from `TimeProvider`; successful recovery SHALL update
state and tool count without fabricating a new failure timestamp. The daemon
log SHALL record each MCP tool invocation that ends in an exception at Warning
level or higher. The line SHALL name the server, the tool, and the HTTP status
when one is present, and SHALL redact secrets. Caller cancellation SHALL NOT
produce that line. An HTTP 401 on a tool call, on a catalog refresh, or at
`initialize` SHALL move the server to `AuthFailed`. The remedy SHALL follow the
daemon's OAuth state, as the engineering glossary defines an OAuth-managed
server, and SHALL NOT depend on the names of configured headers: `netclaw mcp
auth` for an OAuth-managed server, and a check of the configured credentials
or headers for any other server. A genuine OAuth challenge with no stored
tokens SHALL report `AwaitingAuth`. An HTTP 403 on a tool call SHALL NOT change
the server state. Only an OAuth-managed server MAY enter `AuthFailed` because
of tool-declared error text. Any other server SHALL stay `Connected` after
such a result.

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

#### Scenario: Failed invocation is visible at the default log level

- **GIVEN** an MCP tool invocation ends in an HTTP 500 exception
- **WHEN** the daemon handles the failure
- **THEN** the daemon log holds one Warning line that names the server, the tool, and status 500, for example `MCP tool 'shortio/get-domains' invocation failed (HTTP 500)`
- **AND** the line contains no secret values

#### Scenario: Cancelled invocation is not logged as a failure

- **GIVEN** an MCP tool invocation in flight
- **WHEN** the caller's cancellation token fires
- **THEN** the daemon log holds no Warning line for that invocation

#### Scenario: Expired static credential surfaces as AuthFailed with the credentials remedy

- **GIVEN** an HTTP server that authenticates with an operator-configured header of any name, for example `X-Api-Key`, and for which the daemon holds no OAuth tokens
- **WHEN** a tool call returns HTTP 401
- **THEN** the tool result names the HTTP status with an `access_denied` outcome
- **AND** the server status becomes `AuthFailed`
- **AND** the status message tells the operator to check the configured credentials or headers
- **AND** no message names `netclaw mcp auth`
- **AND** the next tool call attempts a reconnect before it returns an error

#### Scenario: Rejected OAuth token on a tool call names the auth command

- **GIVEN** an HTTP server for which the daemon holds OAuth tokens
- **WHEN** a tool call returns HTTP 401
- **THEN** the server status becomes `AuthFailed`
- **AND** remediation points to `netclaw mcp auth <name>`

#### Scenario: OAuth challenge on a tool call names the auth command without stored tokens

- **GIVEN** an HTTP server for which the daemon holds no OAuth tokens
- **WHEN** a tool call fails with the SDK's Bearer-challenge error
- **THEN** the server status becomes `AuthFailed`
- **AND** remediation points to `netclaw mcp auth <name>`

#### Scenario: Catalog refresh 401 without stored tokens names the credentials remedy

- **GIVEN** an HTTP server for which the daemon holds no OAuth tokens
- **WHEN** a catalog refresh returns HTTP 401 with no OAuth challenge
- **THEN** the server status becomes `AuthFailed`
- **AND** the status message tells the operator to check the configured credentials or headers
- **AND** no message names `netclaw mcp auth`

#### Scenario: Bare 401 at initialize without stored tokens reports AuthFailed

- **GIVEN** an HTTP server for which the daemon holds no OAuth tokens
- **WHEN** `initialize` returns HTTP 401 with no OAuth challenge
- **THEN** the server status is `AuthFailed`, not `Unreachable`
- **AND** the status message names the HTTP status and the credentials remedy
- **AND** no message names `netclaw mcp auth`

#### Scenario: HTTP 403 on a tool call keeps the server Connected

- **GIVEN** an HTTP server authenticated by an operator-configured header
- **WHEN** a tool call returns HTTP 403
- **THEN** the tool result names the HTTP status with an `access_denied` outcome
- **AND** the server status stays `Connected`

#### Scenario: Server without stored tokens ignores auth words in a tool result

- **GIVEN** an HTTP server with no operator-configured headers and no stored OAuth tokens
- **WHEN** a tool returns a tool-declared error whose text reports an expired token
- **THEN** the server status stays `Connected`
- **AND** the daemon log records the tool failure at Warning

#### Scenario: Static-header server ignores auth words in a tool result

- **GIVEN** an HTTP server authenticated by an operator-configured `Authorization` header and no stored OAuth tokens
- **WHEN** a tool returns a tool-declared error whose text contains "Forbidden", for example `{"error":"Request failed: 403 Forbidden"}`
- **THEN** the server status stays `Connected`
- **AND** the daemon log records the tool failure at Warning
- **AND** no remedy names `netclaw mcp auth`

#### Scenario: Stdio server ignores auth words in a tool result

- **GIVEN** a stdio server
- **WHEN** a tool returns a tool-declared error whose text reports an expired token
- **THEN** the server status stays `Connected`
- **AND** no remedy names `netclaw mcp auth`

#### Scenario: Server with stored OAuth tokens still reclassifies an expired-token result

- **GIVEN** an HTTP server for which the daemon holds OAuth tokens
- **WHEN** a tool returns a tool-declared error whose text reports an expired token
- **THEN** the server status becomes `AuthFailed`
- **AND** remediation points to `netclaw mcp auth <name>`
