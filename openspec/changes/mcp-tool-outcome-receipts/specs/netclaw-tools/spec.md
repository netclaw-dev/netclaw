Use the [Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md) for tool call, dispatcher, tool result, tool receipt, outcome category, application error, and tool-declared error.

## ADDED Requirements

### Requirement: MCP tool outcomes are machine-actionable

An MCP tool call that ends in an exception SHALL produce a tool receipt under the same rules as the requirement "First-party tool outcomes are machine-actionable". The category SHALL follow the failure kind: an HTTP 401 or 403 is `access_denied`, an HTTP 404 is `not_found`, and every other exception is `transient_failure`. The tool result SHALL stay a factual error string that names the tool. A tool-declared error is not an exception and SHALL keep its current result path. The receipt SHALL NOT grant authority, retry the call, or replay it.

#### Scenario: HTTP 500 becomes a transient failure receipt

- **GIVEN** an MCP tool call that the server answers with HTTP 500
- **WHEN** the adapter returns the tool result
- **THEN** the outcome category is `transient_failure`
- **AND** the tool result names the tool and the HTTP status
- **AND** the receipt records no file activity

#### Scenario: HTTP 403 becomes an access-denied receipt

- **GIVEN** an MCP tool call that the server answers with HTTP 403
- **WHEN** the adapter returns the tool result
- **THEN** the outcome category is `access_denied`
- **AND** no authority changes

#### Scenario: Tool-declared error keeps the result path

- **GIVEN** an MCP tool call that the server answers with HTTP 200 and a tool-declared error, for example `{"content":[{"type":"text","text":"Internal Server Error"}],"isError":true}`
- **WHEN** the adapter returns the tool result
- **THEN** the tool result carries the error text the tool declared
- **AND** no exception outcome is produced
- **AND** no reconnect occurs
