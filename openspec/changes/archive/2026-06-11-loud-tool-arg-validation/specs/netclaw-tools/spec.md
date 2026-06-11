# netclaw-tools — Delta Spec

## ADDED Requirements

### Requirement: Web fetch format is validated

The `web_fetch` tool SHALL validate the `Format` argument against the supported
set (absent, `"raw"`, `"text"`). Any other value SHALL reject the call with a
tool-result error naming the supplied value and the supported set. The tool
SHALL NOT silently fall back to raw mode for an unsupported format value.

#### Scenario: Unsupported format value rejects

- **GIVEN** a `web_fetch` call with `"Format": "markdown"`
- **WHEN** arguments are validated
- **THEN** the call is rejected with an error naming `"markdown"` and the
  supported values `raw` and `text`
- **AND** no HTTP request is made

#### Scenario: Supported formats behave unchanged

- **GIVEN** a `web_fetch` call with `"Format": "text"` (or `Format` absent)
- **WHEN** the fetch executes
- **THEN** behavior is identical to current behavior

### Requirement: Web fetch response-cap truncation is surfaced

The `web_fetch` result SHALL include a notice stating the content was
truncated at the cap whenever a fetched response body reaches the
response-byte cap.
The captured byte count alone SHALL NOT be the only signal.

#### Scenario: Body larger than the cap carries a truncation notice

- **GIVEN** a URL whose response body exceeds the 5 MB response cap
- **WHEN** `web_fetch` returns its summary
- **THEN** the result includes a notice that content was truncated at 5 MB

#### Scenario: Body under the cap carries no truncation notice

- **GIVEN** a URL whose response body is under the response cap
- **WHEN** `web_fetch` returns its summary
- **THEN** no truncation notice is present

### Requirement: Webhook listing honors its filter argument

The `list_webhooks` tool SHALL honor its schema-advertised `Filter` argument:
`"active"` (the default) SHALL return only enabled webhooks, `"all"` SHALL
return every webhook, and any other value SHALL reject the call naming the
supported values. The applied filter SHALL be echoed in the result.

#### Scenario: Active filter excludes disabled webhooks

- **GIVEN** two registered webhooks, one enabled and one disabled
- **AND** a `list_webhooks` call with `"Filter": "active"` (or `Filter` absent)
- **WHEN** the tool executes
- **THEN** only the enabled webhook is listed
- **AND** the result states the `active` filter was applied

#### Scenario: All filter includes disabled webhooks

- **GIVEN** two registered webhooks, one enabled and one disabled
- **AND** a `list_webhooks` call with `"Filter": "all"`
- **WHEN** the tool executes
- **THEN** both webhooks are listed with their enabled state
- **AND** the result states the `all` filter was applied

#### Scenario: Unknown filter value rejects

- **GIVEN** a `list_webhooks` call with `"Filter": "enabled"`
- **WHEN** arguments are validated
- **THEN** the call is rejected naming the supported values `active` and `all`
