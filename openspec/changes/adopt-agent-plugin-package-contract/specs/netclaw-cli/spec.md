## ADDED Requirements

### Requirement: Operator CLI for managed plugins

The CLI SHALL expose `netclaw plugin` as the top-level managed plugin command.
It SHALL provide `install`, `list`, `update`, `enable`, `disable`, and `remove` actions.

Mutations SHALL require confirmation unless the operator supplies `--yes`.
The CLI SHALL NOT expose the unshipped `netclaw skill plugin` command.

#### Scenario: Top-level help shows plugin actions

- **WHEN** the operator runs `netclaw plugin --help`
- **THEN** the command exits with code 0
- **AND** the output lists install, list, update, enable, disable, and remove

#### Scenario: Old nested command is absent

- **WHEN** the operator runs `netclaw skill plugin list`
- **THEN** the CLI returns a usage error
- **AND** it does not contact the daemon

#### Scenario: Mutation requires confirmation

- **GIVEN** the operator does not supply `--yes`
- **WHEN** the operator requests plugin removal
- **THEN** the CLI asks for confirmation before it contacts the daemon

### Requirement: Managed plugin JSON output

`netclaw plugin list --json` SHALL emit one stable JSON document and no prose.
Each row SHALL include the source ID, manifest name, repository, format, reference, status, installed commit, observed commit, and version.

The command SHALL emit JSON `null` for unavailable installed values.
It SHALL exit with code 0 after a successful empty or non-empty response.

#### Scenario: JSON list emits stable fields

- **GIVEN** the daemon returns one installed plugin
- **WHEN** the operator runs `netclaw plugin list --json`
- **THEN** stdout contains one valid JSON document with the required fields
- **AND** stdout contains no progress or recovery prose

#### Scenario: Empty JSON list succeeds

- **GIVEN** no plugin source exists
- **WHEN** the operator runs `netclaw plugin list --json`
- **THEN** stdout contains an empty plugin array
- **AND** the command exits with code 0

### Requirement: Managed plugin daemon errors remain actionable

The daemon SHALL return safe RFC 9457 problem details for plugin request failures.
The CLI SHALL show the safe detail when it receives a valid problem response.

The CLI SHALL use a bounded status message when no valid safe detail exists.
It SHALL NOT print remote response bodies, credentials, stack traces, or unbounded text.

#### Scenario: Missing tag shows daemon detail

- **GIVEN** the daemon returns a safe problem detail for an unknown tag
- **WHEN** the CLI receives that response
- **THEN** the CLI prints the safe detail
- **AND** it exits with code 1

#### Scenario: Invalid error body stays bounded

- **GIVEN** the daemon returns an error with an invalid or oversized body
- **WHEN** the CLI handles the response
- **THEN** the CLI prints a bounded HTTP status message
- **AND** it does not print the response body

### Requirement: Plugin update uses the shared sync operation

`netclaw plugin update <source-id>` SHALL request the existing external sync pass and report that managed source result.
`netclaw plugin update --all` SHALL report every managed plugin source result.

An explicit rejected-commit retry SHALL use `--retry-rejected`.
The command SHALL NOT create another scheduler or sync endpoint.

#### Scenario: Named update reports one source

- **GIVEN** source `team-tools` is configured
- **WHEN** the operator runs `netclaw plugin update team-tools`
- **THEN** the CLI requests the shared external sync pass
- **AND** it reports the result for `team-tools`

#### Scenario: Unknown source fails clearly

- **GIVEN** no source has ID `missing`
- **WHEN** the operator runs `netclaw plugin update missing`
- **THEN** the CLI reports that the source does not exist
- **AND** it exits with code 1
