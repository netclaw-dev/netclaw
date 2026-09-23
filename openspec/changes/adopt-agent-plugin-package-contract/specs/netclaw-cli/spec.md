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

### Requirement: Plugin actions use scoped shared sync passes

`netclaw plugin update <source-id>` SHALL sync only that managed source and report its result.
`netclaw plugin update --all` SHALL sync all managed plugin sources without a skill-server fetch.
Install, enable, disable, and remove SHALL request a source-scoped pass after the daemon accepts the mutation.
`netclaw skill sync` SHALL retain the complete skill-server and managed-plugin pass.

An explicit rejected-commit retry SHALL use `--retry-rejected`.
The commands SHALL use the existing sync actor and endpoint.
The actor SHALL coalesce only requests with the same scope and retry policy.
It SHALL queue requests with different scopes or retry policies.
It SHALL bound distinct queued passes and return a safe 503 response when the queue is full.
After a plugin-only config mutation, the daemon SHALL start with a scoped plugin pass.
If another config change occurs before restart, the daemon SHALL start with a complete pass.

#### Scenario: Named update reports one source

- **GIVEN** source `team-tools` is configured
- **WHEN** the operator runs `netclaw plugin update team-tools`
- **THEN** the CLI requests a source-scoped pass through the shared sync actor
- **AND** it reports the result for `team-tools`

#### Scenario: Plugin-only update leaves skill servers unchanged

- **GIVEN** one managed plugin and one skill server exist
- **WHEN** the operator runs `netclaw plugin update --all`
- **THEN** the daemon syncs all managed plugin sources
- **AND** it does not fetch the skill server

#### Scenario: Named removal clears its inventory without other fetches

- **GIVEN** sources `team-tools` and `other-tools` are configured
- **WHEN** the operator removes `team-tools`
- **THEN** the daemon removes the source receipt and refreshes the skill inventory
- **AND** it does not fetch `other-tools` or a skill server

#### Scenario: Different scopes do not share a result

- **GIVEN** a complete sync pass is active
- **WHEN** the operator requests a named plugin update
- **THEN** the actor queues a source-scoped pass
- **AND** the named update receives its own pass result

#### Scenario: Plugin config restart keeps the requested scope

- **GIVEN** a plugin mutation is the only config change before restart
- **WHEN** the daemon restarts
- **THEN** its startup pass syncs only the changed plugin source
- **AND** the named CLI request joins that pass

#### Scenario: Other config change requires a complete startup pass

- **GIVEN** a plugin mutation occurs before restart
- **AND** another actor changes the config file before restart
- **WHEN** the daemon restarts
- **THEN** its startup pass syncs all configured external sources
- **AND** a named CLI request waits for its separate scoped pass

#### Scenario: Too many distinct scopes do not exhaust the actor

- **GIVEN** the distinct pass queue is full
- **WHEN** the operator requests another distinct plugin scope
- **THEN** the daemon returns a safe HTTP 503 response
- **AND** it keeps the active and queued passes intact

#### Scenario: Unknown source fails clearly

- **GIVEN** no source has ID `missing`
- **WHEN** the operator runs `netclaw plugin update missing`
- **THEN** the CLI reports that the source does not exist
- **AND** it exits with code 1
