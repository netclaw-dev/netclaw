## MODIFIED Requirements

### Requirement: TA-1 Trust context is explicit and fails loud

Every session turn SHALL carry an explicit turn context with a parsed audience
(`Personal`, `Team`, or `Public`), a requester, and a principal. Authorization,
consent, and dispatch SHALL use this turn context. The session journal SHALL
persist the turn context with each consent request, and a recovered request
SHALL use the persisted context, not the current session state.

An ingress that receives an invalid audience value SHALL reject the input
loudly (for example an HTTP 400 result or a CLI exit code 1). Where a
component falls back because an audience is missing or cannot be parsed, it
SHALL fall back to the narrowest audience, `Public`. No fallback SHALL select
a broader audience than the source provides. An audience derived from a
deployment default and a source audience SHALL be the narrower of the two.

A tool execution context SHALL hold the audience as a parsed value. Tool
authorization SHALL read that value and SHALL NOT parse a wire string again.

A turn without a message source SHALL NOT synthesize a requester. A consent
request without a recorded requester SHALL fail closed without a prompt,
except for a verified-automation principal.

#### Scenario: Invalid audience is rejected at ingress

- **GIVEN** a reminder create request with audience `admin`
- **WHEN** the daemon endpoint validates the request
- **THEN** it returns HTTP 400
- **AND** it dispatches no command

#### Scenario: Missing source audience does not broaden

- **GIVEN** a deployment default of `Personal` and a source audience of `Team`
- **WHEN** Netclaw derives the effective audience
- **THEN** the effective audience is `Team`

#### Scenario: Turn without a source cannot ask for consent

- **GIVEN** a turn with no message source
- **WHEN** a tool call requires consent
- **THEN** the call fails closed without a prompt
- **AND** Netclaw does not create a requester

#### Scenario: Tool authorization reads the parsed audience

- **GIVEN** a tool execution context with the parsed audience `Team`
- **WHEN** authorization evaluates a tool call
- **THEN** it uses `Team` without a string parse
- **AND** it applies no parse-failure fallback

### Requirement: TA-3 Audience profiles admit tools

Each audience profile SHALL decide which tools the audience can use:

- `ToolsMode` with `AllowedTools` SHALL restrict the profile-managed tools.
  A profile-managed tool absent from the list SHALL be hidden and SHALL be
  denied with `tool_not_allowed_for_audience_profile`. A tool that is not
  profile-managed SHALL NOT be restricted by `AllowedTools`.
- `McpServersMode` with `AllowedMcpServers` SHALL restrict MCP servers
  (`mcp_server_not_allowed_for_audience_profile`). `McpServerToolGrants` SHALL
  restrict the tools of an allowed server
  (`mcp_tool_not_allowed_for_audience_profile`). A server denial SHALL take
  precedence over a tool denial. An allowed server with no tool list for it
  SHALL expose all of its tools.
- An admission denial SHALL be terminal. No consent answer SHALL override it.

The default profiles SHALL be monotonic: every profile-managed tool of
`Public` SHALL also be in `Team`, and every tool of `Team` SHALL also be in
`Personal`. The defaults SHALL be:

- `Public`: `file_read`, `file_list`, `file_search`, `tool_output_read`,
  `attach_file`; no MCP server; file access scoped to its own session.
- `Team`: all file tools, `web_search`, `web_fetch`, `skill_manage`, the four
  reminder tools, and `set_working_directory`; no `shell_execute`, no webhook
  tools, and no MCP server.
- `Personal`: all tools, all MCP servers, and unrestricted file modes.

MCP `GrantCategory` values and tool grant categories SHALL NOT be an
authorization input.

Invalid `Tools.AudienceProfiles.ChannelAttachments` configuration SHALL stop
daemon startup with an error that names the invalid entries.

#### Scenario: Public cannot edit files

- **GIVEN** a Public session with the default profile
- **WHEN** the model calls `file_edit`
- **THEN** authorization returns `Denied` with reason `tool_not_allowed_for_audience_profile`

#### Scenario: MCP server denial wins over a tool grant

- **GIVEN** a Team profile that lists a tool of server `s` in `McpServerToolGrants` but does not allow server `s`
- **WHEN** the model calls that tool
- **THEN** authorization returns `Denied` with reason `mcp_server_not_allowed_for_audience_profile`
- **AND** the tool does not run, even with a one-time consent

#### Scenario: Team can use outbound web tools

- **GIVEN** a Team session with the default profile
- **WHEN** the model calls `web_fetch`
- **THEN** the admission check passes

#### Scenario: Invalid attachment configuration stops startup

- **GIVEN** an audience profile with an invalid `ChannelAttachments` entry
- **WHEN** the daemon starts
- **THEN** startup fails with `Invalid Tools.AudienceProfiles.ChannelAttachments configuration`

### Requirement: TA-6 Path access decisions own file-tool authority

A file tool SHALL get its filesystem authority only from a path access
decision for its exact file operation (`Read`, `Write`, `Attach`, or
`DeclareProjectScope`). A shell path SHALL use the `Write` operation. The
decision SHALL apply, in order: the canonical path, the audience root catalog,
the link check, then protection.

- Roots SHALL come from the audience profile (`ReadFiles`, `WriteFiles`,
  `AttachFiles` with mode `None`, `Roots`, or `All`), the session storage
  envelope of the session, the declared project directory, and the global read
  roots (`{skills_dir}`, `{identity_dir}`, `{workspaces_dir}`) for `Read` only
  and never for `Public`.
- Only `Personal` SHALL get the shared Netclaw sessions root and the legacy
  logs root. `Team` and `Public` SHALL get only their own session envelope
  and session directory. A child run SHALL inherit the audience and workspace
  limits of its parent.
- A legacy run MAY read its own exact raw log. That exact-file authority SHALL
  NOT cover the parent directory, an adjacent file, or a project declaration.
  A storage ancestor that Netclaw reads for a link check SHALL NOT grant
  directory authority.
- Interactive `Personal` with mode `All` SHALL skip root checks. An unattended
  run SHALL be confined to its trusted roots and SHALL fail closed without
  them. Consent SHALL NOT widen an explicit `Roots` or `None` profile.
- A relative path SHALL resolve against the project directory, else the
  session directory. File tools SHALL NOT expand `~`; `~/x` is a relative path.
- A path through a link that leaves the root SHALL be denied. A path whose
  base has a link ancestor SHALL be denied, and Netclaw SHALL NOT try another
  base.
- Protection SHALL depend on the operation. Ordinary `netclaw.json` SHALL be
  readable. Secrets, keys, webhook secrets, the grant store, the hard-deny
  override file, the database, process-control files, and device state SHALL
  be read-denied. The config directory, secrets, keys, the database, process
  control files, system skills, and server feeds SHALL be write-denied. Shell
  text that names the config directory, secrets, webhooks, keys, the database,
  or process-control files SHALL be denied.
- Allow checks SHALL compare paths with ordinal case except on Windows. Deny
  checks SHALL ignore case.
- A path access denial SHALL be terminal and SHALL NOT reveal root paths to a
  Public session.
- Tool capability and shell command policy SHALL run before file protection.
  File authority SHALL NOT enable shell. Netclaw SHALL derive the known real
  paths of a shell call from the command analysis, independent of approval
  candidates, and SHALL check known causal-intent and fallback paths before
  stored or reviewed-safe coverage.
- A readable `netclaw.json` SHALL NOT imply write, edit, attach, or shell
  authority. Secret values SHALL live only in protected stores.

#### Scenario: Ordinary config is readable but not by shell text

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on `netclaw.json`
- **THEN** the path access decision allows the read
- **AND** a `shell_execute` call with `cat <config dir>/netclaw.json` is denied with `shell_references_protected_path`

#### Scenario: Team does not get the shared sessions root

- **GIVEN** a Team session
- **WHEN** the model calls `file_read` on a file in another session's directory
- **THEN** the path access decision denies the read

#### Scenario: Link escape from a project base

- **GIVEN** a project directory whose ancestor is a link to `/outside`
- **WHEN** the model calls `file_read` with a relative path
- **THEN** the path access decision denies the read
- **AND** Netclaw does not retry against the session directory

#### Scenario: Restricted session cannot read a sibling session

- **GIVEN** a Team session
- **WHEN** the model calls `file_read` on the raw log of another session
- **THEN** the path access decision denies the read

#### Scenario: Personal session keeps cross-session read access

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on a file in another session directory
- **THEN** the path access decision allows the read

### Requirement: TA-10 Consent prompts offer only safe options

A consent request SHALL carry the tool name, a display text with secrets
removed, the requester, the candidates, the working directory, the offered
options, and the authorization attempt identifier. It SHALL NOT carry a
`DirectoryRoots` field.

The consent request SHALL also carry adopted-context provenance.
`HasAdoptedContext` SHALL be true for any non-empty adopted window. The
adopted speakers SHALL list every adopted sender, including the requester.
`HasThirdPartyAdoptedContext` SHALL be a separate flag and SHALL NOT trim that
list. Adopted context SHALL stay quoted background and SHALL NOT originate a
consent request.

The options SHALL come from this set, in this order, with these stable keys
and labels:

| Key | Label |
|---|---|
| `approve_once` | Once |
| `approve_session` | This chat |
| `approve_always` | Always here |
| `approve_repository` | This repository |
| `approve_everywhere` | Always anywhere (Always allow this tool for an MCP tool) |
| `deny` | Deny |

- The prompt SHALL offer only `Once` and `Deny` when any candidate has
  unresolved syntax or no reusable phrase, or when the call is a managed
  temporary retry.
- `Always here` SHALL be offered only for a shell call with a directory scope
  that is not shallow and not session-owned.
- `This repository` SHALL be offered only for a clean reusable shell phrase
  whose candidates all resolve to one Git common directory.
- An assignment-qualified prompt SHALL use the versioned keys
  `approve_assignment_{session,always,repository,everywhere}_v1` for reusable
  options. `Once` and `Deny` keep their keys.
- Labels SHALL fit in 76 characters. `Always anywhere` and `Deny` SHALL have
  danger styling where the channel supports it.
- Only the requester SHALL answer, unless the principal is verified
  automation. Netclaw SHALL reject an option that the request did not offer.
- A channel type that supports interactive approval (Slack, Discord,
  Mattermost, TUI, SignalR) SHALL render the options and a text fallback. A
  channel that cannot post a prompt SHALL answer `Deny` for that call.
- A call that needs consent in a turn that cannot ask (no interactive channel
  or no requester) SHALL return the tool result
  `Tool requires approval but no interactive approval requester is available: <tool>`
  and SHALL NOT prompt. No reason code `channel_does_not_support_approval`
  exists.

#### Scenario: Unresolved syntax offers one-time options only

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with a command that has unresolved syntax
- **THEN** the prompt offers `Once` and `Deny` only

#### Scenario: Wrong requester cannot answer

- **GIVEN** a consent request from requester `U1`
- **WHEN** user `U2` selects `approve_once`
- **THEN** Netclaw rejects the answer and posts a warning
- **AND** the call does not run

#### Scenario: Headless run cannot ask

- **GIVEN** a headless Personal session with `shell_execute` in `Approval` mode
- **WHEN** the model calls `shell_execute` with an uncovered command
- **THEN** the tool result is `Tool requires approval but no interactive approval requester is available: shell_execute`

#### Scenario: MCP tool prompt has no folder option

- **GIVEN** an MCP tool in `Approval` mode
- **WHEN** the prompt is built
- **THEN** it does not offer `Always here`
- **AND** its global option has the label `Always allow this tool`

#### Scenario: Self-only adopted window keeps its provenance

- **GIVEN** a turn whose adopted window holds only earlier messages of the requester
- **WHEN** a tool call in that turn asks for consent
- **THEN** the consent request has `HasAdoptedContext` true and lists the requester as an adopted speaker
- **AND** `HasThirdPartyAdoptedContext` is false
