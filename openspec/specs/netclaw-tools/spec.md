# netclaw-tools Specification

## Purpose

Define first-party tool access for Netclaw: web search, web fetch, shell
execution, and GitHub CLI. All tools are registered through
Microsoft.Extensions.AI, filtered by policy grants, and audited on invocation.
This capability provides the agent with the ability to act on the world beyond
conversation.

## Requirements

### Requirement: Tool registration with MEAI

All first-party tools SHALL be registered as `Microsoft.Extensions.AI` tool
definitions at startup. Tool metadata (name, description, parameters) SHALL be
defined at registration. Available tools presented to the LLM SHALL be filtered
per session based on ACL policy grants.

#### Scenario: Tools registered at startup

- **WHEN** the Netclaw process starts
- **THEN** all configured first-party tools are registered as MEAI tool
  definitions
- **AND** each tool definition includes name, description, and parameter schema

#### Scenario: Session receives filtered tool set

- **GIVEN** a session has ACL grants for `web_search` and `web_fetch` but not
  `shell`
- **WHEN** the session starts and tools are provided to the LLM
- **THEN** only `web_search` and `web_fetch` tool definitions are included
- **AND** `shell` is not offered to the LLM

#### Scenario: Tool results returned as tool response messages

- **GIVEN** the LLM issues a tool call during a turn
- **WHEN** the tool executes and produces a result
- **THEN** the result is returned to the LLM as an MEAI tool response message
- **AND** the session continues the turn loop with the tool result in context

### Requirement: Web search tool

The system SHALL provide a web search tool that delegates to a configured
`ISearchBackend` implementation. The tool SHALL accept a query and optional
max results parameter and SHALL return structured search results (title, URL,
snippet) suitable for LLM consumption. The tool interface to the agent SHALL
remain identical regardless of which backend is configured.

#### Scenario: Web search via configured backend

- **GIVEN** a search backend is configured and registered
- **WHEN** the agent invokes the web search tool with a query
- **THEN** the tool delegates to the configured `ISearchBackend`
- **AND** returns structured results (title, URL, snippet) to the LLM

#### Scenario: Web search with default backend

- **GIVEN** no search backend is explicitly configured
- **WHEN** the agent invokes the web search tool
- **THEN** the tool uses the DuckDuckGo backend
- **AND** returns results in the same format as any other backend

#### Scenario: Backend error returned to agent

- **GIVEN** the configured search backend returns an error
- **WHEN** the agent invokes the web search tool
- **THEN** the tool returns the backend's error message to the LLM
- **AND** the error does not crash the session

#### Scenario: Missing API key prevents tool registration

- **GIVEN** a backend requiring credentials is configured (e.g., Brave Search)
- **WHEN** no credentials are provided in configuration
- **THEN** the web search tool is not registered at startup
- **AND** a warning is logged indicating the tool is unavailable

### Requirement: Web fetch tool

The system SHALL provide a web fetch tool that retrieves content from URLs and
saves it to a local file. The tool SHALL support two output formats: `raw`
(default) preserves HTML structure after removing script and style elements,
and `text` extracts plain text. Output is saved to disk and a preview summary
returned to prevent context flooding.

#### Scenario: Fetch URL in raw mode (default)

- **GIVEN** the web fetch tool is available
- **WHEN** the agent invokes the tool with a URL (no format or format='raw')
- **THEN** the tool retrieves the page content via HTTP
- **AND** removes `<script>` and `<style>` elements
- **AND** preserves all other HTML structure (links, images, nav, etc.)
- **AND** saves the sanitized HTML to a `.html` file
- **AND** returns a summary with file path and metadata preview

#### Scenario: Fetch URL in text mode

- **GIVEN** the web fetch tool is available
- **WHEN** the agent invokes the tool with a URL and format='text'
- **THEN** the tool retrieves the page content via HTTP
- **AND** extracts plain text from HTML (removing all markup)
- **AND** saves the extracted text to a `.txt` file
- **AND** returns a summary with file path and text preview

#### Scenario: Output saved to disk to prevent context flooding

- **GIVEN** the fetched page content is large
- **WHEN** the tool processes the content
- **THEN** the full content is saved to disk
- **AND** only a preview summary is returned to the LLM
- **AND** the agent can use file_read to access specific sections

#### Scenario: Non-HTML content returned as-is

- **GIVEN** the fetched URL returns plain text or JSON content
- **WHEN** the tool processes the response
- **THEN** the content is saved without HTML processing
- **AND** a preview summary is returned

#### Scenario: Unreachable URL returns error

- **GIVEN** the agent invokes the web fetch tool with an unreachable URL
- **WHEN** the HTTP request fails
- **THEN** the tool returns an error message to the LLM
- **AND** the error does not crash the session

### Requirement: Shell execution tool

The system SHALL provide a shell execution tool that runs commands as the
Netclaw process user context. Stdin SHALL be closed (no interactive commands).
Execution SHALL enforce a configurable timeout (default: 60 seconds). Output
SHALL be truncated to a configurable limit. Before execution, the shell tool
SHALL check the hard deny list via `ShellCommandPolicy`. Hard-denied commands
SHALL be rejected before `ToolPathPolicy` path checks.

#### Scenario: Execute command and return output

- **GIVEN** the `shell` grant is available for the session
- **WHEN** the agent invokes the shell tool with a command
- **THEN** the command is executed as the Netclaw process user
- **AND** stdout and stderr are captured
- **AND** the combined output is returned to the LLM

#### Scenario: Hard-denied command rejected before execution

- **GIVEN** the agent invokes `shell_execute` with `netclaw daemon stop`
- **WHEN** `ShellCommandPolicy` evaluates the command
- **THEN** the command is rejected with "Command blocked by hard deny policy"
- **AND** the shell process is never started

#### Scenario: Execution timeout enforced

- **GIVEN** a shell command is running
- **WHEN** the command exceeds the configured timeout (default: 60 seconds)
- **THEN** the process is terminated
- **AND** the tool returns a timeout error message to the LLM

#### Scenario: Output truncated to limit

- **GIVEN** a shell command produces output exceeding the configured limit
- **WHEN** the output is captured
- **THEN** the output is truncated to the configured character limit
- **AND** a truncation indicator is appended

#### Scenario: Stdin closed prevents interactive commands

- **GIVEN** the agent invokes the shell tool with a command
- **WHEN** the process is created
- **THEN** stdin is closed immediately
- **AND** commands that require interactive input fail promptly

#### Scenario: Working directory set to project path

- **GIVEN** the session is associated with a registered project
- **WHEN** the shell tool executes a command
- **THEN** the working directory is set to the project's registered path

### Requirement: File edit tool

The system SHALL provide a file edit tool that performs targeted text
replacements in existing files without rewriting the entire file. The tool
SHALL require the target file to already exist. The tool SHALL match literal
text (not regex) and fail loudly if the specified text is not found. When
multiple matches exist and replace-all is not specified, the tool SHALL fail
with an ambiguity error to prevent accidental edits. OldString and NewString
SHALL both be required and must differ. The tool uses the `file` grant
category and respects the same security policies as file_write.

#### Scenario: Single replacement in existing file

- **GIVEN** the `file` grant is available and a file exists at the specified path
- **WHEN** the agent invokes file_edit with OldString and NewString
- **AND** OldString matches exactly once in the file
- **THEN** the first occurrence is replaced with NewString
- **AND** the rest of the file content is preserved

#### Scenario: Replace all occurrences

- **GIVEN** a file contains multiple occurrences of OldString
- **WHEN** the agent invokes file_edit with ReplaceAll=true
- **THEN** all occurrences are replaced with NewString

#### Scenario: Ambiguous match rejected

- **GIVEN** a file contains multiple occurrences of OldString
- **WHEN** the agent invokes file_edit without ReplaceAll
- **THEN** the tool returns an error indicating the match is ambiguous
- **AND** the file is not modified

#### Scenario: Text not found returns error

- **GIVEN** a file does not contain OldString
- **WHEN** the agent invokes file_edit
- **THEN** the tool returns an error that the text was not found
- **AND** the file is not modified

#### Scenario: File must exist

- **GIVEN** the specified file path does not exist
- **WHEN** the agent invokes file_edit
- **THEN** the tool returns a file-not-found error

#### Scenario: Empty replacement performs deletion

- **GIVEN** a file contains OldString
- **WHEN** the agent invokes file_edit with NewString as empty string
- **THEN** the matched text is deleted from the file

#### Scenario: OldString and NewString must differ

- **GIVEN** the agent invokes file_edit with OldString equal to NewString
- **WHEN** the tool validates parameters
- **THEN** the tool returns an error that OldString and NewString must differ
- **AND** the file is not modified

### Requirement: GitHub CLI tool

The system SHALL provide a GitHub tool that shells out to the `gh` CLI for
issue creation, PR management, and repo operations. The tool SHALL parse
structured output from `gh`. If `gh` is not installed, the tool SHALL not be
registered and SHALL report the missing dependency.

#### Scenario: Execute gh command and return structured output

- **GIVEN** the `github` grant is available and `gh` is installed
- **WHEN** the agent invokes the GitHub tool with a `gh` command
- **THEN** the tool executes the `gh` command via shell
- **AND** parses the output into structured form
- **AND** returns the structured result to the LLM

#### Scenario: gh CLI not installed

- **GIVEN** `gh` is not found in the environment inventory
- **WHEN** the Netclaw process starts
- **THEN** the GitHub tool is not registered
- **AND** a warning is logged indicating the GitHub tool is unavailable due to
  missing `gh` CLI

#### Scenario: gh authentication failure handled

- **GIVEN** the `gh` CLI is installed but not authenticated
- **WHEN** a GitHub tool invocation is attempted
- **THEN** the tool returns an authentication error message to the LLM
- **AND** the error does not crash the session

### Requirement: Policy-gated tool invocation

The system SHALL check ACL grants and approval policy before every tool
execution. Tool invocations SHALL be logged with audit records including tool
name, invoking session, timestamp, allow/deny/approval result, and approval
decision details when applicable. The `ToolAccessDecision` SHALL support three
outcomes: `Allow`, `Deny(reason)`, and `RequiresApproval(context)`.

When `RequiresApproval` is returned, the tool execution pipeline SHALL pause
the individual tool task and emit a `ToolInteractionRequest` to session
subscribers. The pipeline SHALL NOT block other tool calls in the same batch.

#### Scenario: Granted tool executes successfully

- **GIVEN** the session has an ACL grant for `web_search`
- **AND** `web_search` is in Auto approval mode
- **WHEN** the LLM requests a web search tool call
- **THEN** the ACL check passes
- **AND** the tool executes
- **AND** an audit record is logged with tool name, session ID, timestamp, and
  `allow` result

#### Scenario: Ungrantable tool denied at invocation

- **GIVEN** the session does not have an ACL grant for `shell`
- **WHEN** the LLM requests a shell tool call
- **THEN** the ACL check fails
- **AND** the tool is not executed
- **AND** a policy denial with reason code is returned to the LLM
- **AND** an audit record is logged with tool name, session ID, timestamp, and
  `deny` result

#### Scenario: Tool requires approval and is approved

- **GIVEN** the session has an ACL grant for `shell`
- **AND** `shell_execute` is in Approval mode for the session's audience
- **AND** the command pattern is not in the approval cache
- **WHEN** the LLM requests a shell tool call
- **THEN** `ToolAccessPolicy` returns `RequiresApproval`
- **AND** the pipeline emits a `ToolInteractionRequest` and pauses the task
- **AND** when the user approves, the tool executes
- **AND** an audit record is logged with `approved` result

#### Scenario: Tool requires approval and is denied by user

- **GIVEN** the pipeline has emitted an approval prompt
- **WHEN** the user denies
- **THEN** the tool result is "Command denied by user"
- **AND** an audit record is logged with `denied_by_user` result

#### Scenario: Audit records available in diagnostics

- **GIVEN** tool invocations have occurred
- **WHEN** the operator views diagnostics
- **THEN** audit records show tool name, invoking session, timestamp, and
  allow/deny/approval result for each invocation

### Requirement: Configurable search backend

The system SHALL support configuring DuckDuckGo, Brave Search API, or SearXNG
as the web search backend. The choice SHALL be made through configuration
without code changes. DuckDuckGo SHALL be the default when no configuration
is present.

#### Scenario: DuckDuckGo as default backend

- **GIVEN** no search backend is specified in configuration
- **WHEN** the web search tool is registered
- **THEN** the tool uses DuckDuckGo for queries

#### Scenario: Brave Search configured

- **GIVEN** the configuration specifies `Search.Backend: "brave"` with a valid
  API key
- **WHEN** the web search tool is registered
- **THEN** the tool uses Brave Search API for queries

#### Scenario: SearXNG configured as alternative

- **GIVEN** the configuration specifies `Search.Backend: "searxng"` with an
  endpoint URL
- **WHEN** the web search tool is registered
- **THEN** the tool uses the SearXNG endpoint for queries

#### Scenario: Invalid search backend rejected at startup

- **GIVEN** the configuration specifies an unrecognized search backend value
- **WHEN** the Netclaw process starts
- **THEN** the web search tool is not registered
- **AND** a configuration validation warning is logged

<!-- Delta from 2026-03-24 skill platform + security TUI changes -->
## MODIFIED Requirements

### Requirement: Web search tool

The system SHALL provide a web search tool that delegates to a configured
`ISearchBackend` implementation. The tool SHALL accept a query and optional
max results parameter and SHALL return structured search results (title, URL,
snippet) suitable for LLM consumption. The tool interface to the agent SHALL
remain identical regardless of which backend is configured.

#### Scenario: Web search via configured backend

- **GIVEN** a search backend is configured and registered
- **WHEN** the agent invokes the web search tool with a query
- **THEN** the tool delegates to the configured `ISearchBackend`
- **AND** returns structured results (title, URL, snippet) to the LLM

#### Scenario: Web search with default backend

- **GIVEN** no search backend is explicitly configured
- **WHEN** the agent invokes the web search tool
- **THEN** the tool uses the DuckDuckGo backend
- **AND** returns results in the same format as any other backend

#### Scenario: Backend error returned to agent

- **GIVEN** the configured search backend returns an error
- **WHEN** the agent invokes the web search tool
- **THEN** the tool returns the backend's error message to the LLM
- **AND** the error does not crash the session

#### Scenario: Missing API key prevents tool registration

- **GIVEN** a backend requiring credentials is configured (e.g., Brave Search)
- **WHEN** no credentials are provided in configuration
- **THEN** the web search tool is not registered at startup
- **AND** a warning is logged indicating the tool is unavailable

### Requirement: Configurable search backend

The system SHALL support configuring DuckDuckGo, Brave Search API, or SearXNG
as the web search backend. The choice SHALL be made through configuration
without code changes. DuckDuckGo SHALL be the default when no configuration
is present.

#### Scenario: DuckDuckGo as default backend

- **GIVEN** no search backend is specified in configuration
- **WHEN** the web search tool is registered
- **THEN** the tool uses DuckDuckGo for queries

#### Scenario: Brave Search configured

- **GIVEN** the configuration specifies `Search.Backend: "brave"` with a valid
  API key
- **WHEN** the web search tool is registered
- **THEN** the tool uses Brave Search API for queries

#### Scenario: SearXNG configured as alternative

- **GIVEN** the configuration specifies `Search.Backend: "searxng"` with an
  endpoint URL
- **WHEN** the web search tool is registered
- **THEN** the tool uses the SearXNG endpoint for queries

#### Scenario: Invalid search backend rejected at startup

- **GIVEN** the configuration specifies an unrecognized search backend value
- **WHEN** the Netclaw process starts
- **THEN** the web search tool is not registered
- **AND** a configuration validation warning is logged
