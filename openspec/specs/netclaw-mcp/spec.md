# netclaw-mcp Specification

Research: `docs/research/dynamic-context-discovery.md`

## Purpose

Define MCP server integration, validation, policy enforcement, and diagnostics.

## Requirements

### Requirement: MCP server profile configuration

The system SHALL support named MCP server profiles in configuration. Each
profile SHALL specify a transport type (`stdio` or `SSE`), the command or URL
for the server, and optional environment variables to pass to the server
process.

#### Scenario: Disabled by default

- **WHEN** no MCP profile is enabled
- **THEN** no MCP tools are loaded

#### Scenario: Stdio transport profile

- **GIVEN** an MCP profile is configured with transport type `stdio`
- **WHEN** the profile is loaded
- **THEN** the system launches the server using the configured command
- **AND** communicates via stdio transport

#### Scenario: SSE transport profile

- **GIVEN** an MCP profile is configured with transport type `SSE`
- **WHEN** the profile is loaded
- **THEN** the system connects to the configured URL via SSE transport

#### Scenario: Environment variables passed to server

- **GIVEN** an MCP profile specifies environment variables
- **WHEN** the server process is launched (stdio transport)
- **THEN** the configured environment variables are set in the server process
  environment

### Requirement: MCP validation

The system SHALL validate MCP server connectivity and discovery.

#### Scenario: Validate server

- **WHEN** operator runs MCP validation
- **THEN** output indicates handshake status and discovered tool count

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

### Requirement: Policy-gated MCP invocation

The system SHALL apply ACL and grants before invoking MCP tools.

#### Scenario: Missing grant denies MCP tool

- **WHEN** an MCP tool is requested without grant
- **THEN** invocation is denied with a policy reason code

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

### Requirement: Memorizer as external memory tier

The Memorizer MCP server SHALL be the recommended first MCP server for Netclaw
deployments. Memorizer provides `store`, `search`, `get`, `delete`, and
`create_relationship` operations for persisting research findings and
cross-session learning. Memorizer is an external memory tier complementing
first-party local memory (personality, project registry, environment inventory).

#### Scenario: Store research finding via Memorizer

- **GIVEN** the `memorizer` MCP server is configured and reachable
- **AND** the session has `mcp:memorizer` grant
- **WHEN** the agent stores a research finding
- **THEN** the finding is persisted in Memorizer and retrievable in future
  sessions

#### Scenario: Search across sessions via Memorizer

- **GIVEN** prior sessions have stored findings in Memorizer
- **WHEN** the agent searches Memorizer for a topic
- **THEN** relevant findings from prior sessions are returned

### Requirement: Tool discovery and registration

On startup, the system SHALL discover tools from all enabled MCP server
profiles and register them as Microsoft.Extensions.AI (MEAI) tool definitions.
Tool discovery SHALL refresh on each session start to pick up newly added or
removed tools from MCP servers.

To avoid context window bloat with large tool catalogs (see
`docs/research/dynamic-context-discovery.md` §1–2), the system SHALL use a
three-step discovery strategy: a compressed tool index injected into the system
prompt for agent awareness, a `search_tools` meta-tool for browsing available
tools (names and descriptions only), and a `load_tool` meta-tool for
on-demand activation of individual tool definitions. `search_tools` SHALL NOT
load tool schemas into the session — it SHALL return a discovery menu only.
The agent SHALL call `load_tool` to activate each tool it needs. Core tools
(shell, file operations) SHALL remain always-loaded; MCP tools SHALL be
deferred by default.

When an LLM call fails after tools have been dynamically loaded, the system
SHALL evict all discovered tools from the session's available tool set. This
prevents a tool set that caused the failure (e.g., oversized schemas) from
poisoning subsequent turns.

#### Scenario: Startup tool discovery

- **GIVEN** two MCP servers are enabled with a combined total of 5 tools
- **WHEN** the system starts
- **THEN** all 5 tools are discovered and registered as MEAI tool definitions

#### Scenario: Session-start tool refresh

- **GIVEN** an MCP server has added a new tool since last session start
- **WHEN** a new session actor initializes
- **THEN** the refreshed tool list includes the newly added tool

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

### Requirement: Per-tool audience filtering for MCP servers

The system SHALL support per-server tool allowlists on each audience profile
via `McpServerToolGrants`. When a server has an entry in the grants dictionary,
only tools whose bare name appears in the list SHALL be exposed to that
audience. When a server has no entry (or grants is null), all tools from that
server SHALL be exposed (backward-compatible default).

Tool grants compose with the existing `AllowedMcpServers` gate: a tool must
pass both the server-level check AND the per-tool grant check to be exposed.

#### Scenario: Tool granted to audience is exposed

- **GIVEN** audience profile has `McpServerToolGrants: { "memorizer": ["search_memories", "get"] }`
- **AND** the `memorizer` server is in `AllowedMcpServers`
- **WHEN** the session resolves available tools for this audience
- **THEN** `memorizer/search_memories` and `memorizer/get` are exposed
- **AND** other tools from `memorizer` are not exposed

#### Scenario: No grants for server exposes all tools

- **GIVEN** audience profile has `McpServerToolGrants` that does not contain a `memorizer` entry
- **AND** the `memorizer` server is in `AllowedMcpServers`
- **WHEN** the session resolves available tools for this audience
- **THEN** all tools from `memorizer` are exposed

#### Scenario: Null grants exposes all tools from allowed servers

- **GIVEN** audience profile has `McpServerToolGrants: null`
- **AND** servers are allowed via `AllowedMcpServers` or `McpServersMode: All`
- **WHEN** the session resolves available tools
- **THEN** all tools from all allowed servers are exposed

#### Scenario: Empty tool list blocks all tools from server

- **GIVEN** audience profile has `McpServerToolGrants: { "memorizer": [] }`
- **AND** the `memorizer` server is in `AllowedMcpServers`
- **WHEN** the session resolves available tools
- **THEN** no tools from `memorizer` are exposed

#### Scenario: Server not in AllowedMcpServers is blocked regardless of grants

- **GIVEN** audience profile has `McpServerToolGrants: { "memorizer": ["search_memories"] }`
- **BUT** `memorizer` is NOT in `AllowedMcpServers` and `McpServersMode` is `Allowlist`
- **WHEN** the session resolves available tools
- **THEN** no tools from `memorizer` are exposed

#### Scenario: Different audiences see different tools from same server

- **GIVEN** Team profile has `McpServerToolGrants: { "memorizer": ["search_memories", "get"] }`
- **AND** Personal profile has `McpServersMode: All` with no `McpServerToolGrants`
- **WHEN** a Team session resolves tools
- **THEN** only `search_memories` and `get` are exposed
- **AND** when a Personal session resolves tools, all `memorizer` tools are exposed

### Requirement: Tool grant enforcement in search_tools

`search_tools` and `load_tool` SHALL enforce the same effective audience and
feature gates as direct MCP tool exposure. A session MUST NOT be able to use
these discovery/load paths to enumerate or activate tools that are blocked by
deployment-wide runtime switches, audience allowlists, or per-server per-tool
grants.

#### Scenario: Public session cannot discover blocked MCP capabilities

- **GIVEN** a session has audience `Public`
- **AND** Public does not have access to a given MCP server or tool
- **WHEN** the session calls `search_tools`
- **THEN** blocked servers and tools do not appear in results
- **AND** the response does not reveal hidden tool names for blocked internals

#### Scenario: Public session cannot activate blocked MCP tool through load_tool

- **GIVEN** a session has audience `Public`
- **AND** the requested MCP tool is not exposed to Public
- **WHEN** the session calls `load_tool`
- **THEN** the tool is not activated
- **AND** the result follows the generic denied/not-found path without leaking
  hidden capability inventory

#### Scenario: Disabled subsystem hides discovery inventory for all audiences

- **GIVEN** a deployment-wide feature switch disables the relevant MCP-backed
  subsystem
- **WHEN** a Team session calls `search_tools`
- **THEN** tools from that disabled subsystem are absent from discovery results
- **AND** `load_tool` cannot activate them

### Requirement: Tool change detection logging

At MCP server connect time, the system SHALL compare discovered tools against
tool grants configured across all audience profiles. The system SHALL log
warnings for tools that appear on the server but are not granted to any
audience, and for granted tool names that do not exist on the server.

#### Scenario: New tool discovered but not granted

- **GIVEN** `memorizer` server exposes tools `[search_memories, store, get, archive]`
- **AND** across all audience profiles, only `[search_memories, store, get]` are granted
- **WHEN** the daemon connects to `memorizer`
- **THEN** a warning is logged identifying `archive` as discovered but not granted to any audience

#### Scenario: Granted tool not found on server

- **GIVEN** Team profile grants `["search_memories", "old_tool"]` from `memorizer`
- **AND** `memorizer` does not expose a tool named `old_tool`
- **WHEN** the daemon connects to `memorizer`
- **THEN** a warning is logged identifying `old_tool` as granted but not found on the server

#### Scenario: No grants configured skips change detection

- **GIVEN** no audience profile has `McpServerToolGrants` entries for `memorizer`
- **WHEN** the daemon connects to `memorizer`
- **THEN** no tool change detection warnings are logged for that server

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

### Requirement: MCP prompt discovery and generation ownership

The system SHALL list prompts when an enabled MCP server declares prompt support.
It SHALL publish prompt descriptors in the same immutable server generation as the discovered tools.

Each descriptor SHALL use the logical name `mcp__<server>__<prompt>`.
It SHALL retain the server name, prompt name, prompt arguments, and generation.

#### Scenario: Prompt-capable server connects

- **GIVEN** an enabled server declares prompt support
- **WHEN** the daemon initializes the server connection
- **THEN** the daemon lists the server prompts
- **AND** it publishes the tools and prompts in one server generation
- **AND** each prompt appears in the skill registry under its canonical logical name

#### Scenario: Tool-only server connects

- **GIVEN** an enabled server does not declare prompt support
- **WHEN** the daemon initializes the server connection
- **THEN** the daemon does not call `prompts/list`
- **AND** the server tools remain available

#### Scenario: Prompt discovery fails during replacement

- **GIVEN** a healthy published server generation
- **WHEN** a replacement candidate cannot list its declared prompts
- **THEN** the system keeps the prior server generation
- **AND** it keeps the prior MCP prompt skill inventory
- **AND** diagnostics report the replacement failure

### Requirement: MCP prompt catalog poll

The existing MCP catalog poll SHALL include prompts for a prompt-capable server.
It SHALL publish one replacement generation when a tool or prompt descriptor changes.

#### Scenario: Prompt descriptor changes

- **GIVEN** a connected server changes a prompt description or argument descriptor
- **WHEN** the next catalog poll succeeds
- **THEN** the system publishes a new server generation
- **AND** the skill registry contains the new prompt descriptor

#### Scenario: Prompt catalog becomes empty

- **GIVEN** a connected prompt-capable server removes its final prompt
- **WHEN** the next catalog poll succeeds with an empty prompt list
- **THEN** the system removes that server's MCP prompt skills
- **AND** it preserves the server's tools and file skills

### Requirement: MCP prompt server permission

The system SHALL use the existing MCP server grant for prompt discovery and prompt use.
It SHALL NOT add a prompt-specific grant category.

#### Scenario: Audience can use the server

- **GIVEN** an audience can use MCP server `gigatron`
- **WHEN** the system builds that audience's skill index
- **THEN** allowed `mcp__gigatron__*` prompt skills appear

#### Scenario: Audience cannot use the server

- **GIVEN** an audience cannot use MCP server `gigatron`
- **WHEN** the system builds that audience's skill index or handles a prompt load
- **THEN** no `gigatron` prompt descriptor appears
- **AND** the load follows the generic denied result

#### Scenario: Unknown skill fallback does not reveal remote prompts

- **GIVEN** the registry contains MCP prompt skills from one or more servers
- **WHEN** a session requests an unknown skill name
- **THEN** the fallback list contains no MCP server or prompt names
- **AND** the audience-filtered skill index remains the discovery source for remote prompts

### Requirement: MCP prompt load generation and failure behavior

The system SHALL resolve an MCP prompt through the client generation that supplied its skill descriptor.
It SHALL fail visibly when the descriptor is stale, the server is unavailable, or the result has unsupported content.

#### Scenario: Current prompt descriptor loads

- **GIVEN** an MCP prompt skill references the current server generation
- **WHEN** `skill_load` loads the prompt
- **THEN** the system calls `prompts/get` on that generation
- **AND** the result identifies the source server, prompt, and generation
- **AND** the result preserves each prompt message role

#### Scenario: Stale prompt descriptor fails

- **GIVEN** an MCP prompt skill references a replaced server generation
- **WHEN** `skill_load` loads the prompt
- **THEN** the system returns an explicit stale-generation error
- **AND** it does not call `prompts/get` on the new generation

#### Scenario: Unsupported prompt content fails

- **GIVEN** `prompts/get` returns a content block that this slice cannot render
- **WHEN** the adapter processes the result
- **THEN** it returns an explicit unsupported-content error
- **AND** it does not silently omit the block

