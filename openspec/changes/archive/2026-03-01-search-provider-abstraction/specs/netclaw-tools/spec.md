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
