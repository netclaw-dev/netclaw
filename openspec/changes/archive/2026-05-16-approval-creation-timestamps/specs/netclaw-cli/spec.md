## ADDED Requirements

### Requirement: Approval surfaces show grant creation time

The `netclaw approvals` inspection surfaces SHALL display when each
persisted grant was created. Both the `netclaw approvals list` command
and the interactive `netclaw approvals` Termina TUI list SHALL derive
this from the `ApprovalEntry.createdAt` field.

Human-readable output (the default `list` rendering and the TUI list
rows) SHALL render the creation time as relative text — for example
`added 3 days ago`. An entry whose `createdAt` is `null` (a grant
written before timestamp tracking) SHALL render a stable placeholder
(`added —`) rather than a fabricated or omitted value. The creation-time
text SHALL NOT be mixed into the scope label column; it is presented as
distinct per-entry metadata.

`netclaw approvals list --json` SHALL expose the raw `createdAt` value
on each entry — an ISO-8601 string when present, `null` otherwise — so
scripts can compare it against daemon log timestamps. The JSON output
SHALL remain a superset of the previous shape: existing `verb` and
`directory` fields are unchanged.

#### Scenario: List shows relative creation time

- **GIVEN** `tool-approvals.json` contains an entry whose `createdAt` is
  three days before now
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the entry's row includes relative text such as `added 3 days ago`

#### Scenario: Entry without a timestamp shows a placeholder

- **GIVEN** `tool-approvals.json` contains an entry with no `createdAt`
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the entry's row shows the `added —` placeholder
- **AND** the command exits with code `0`

#### Scenario: JSON output exposes the raw timestamp

- **GIVEN** `tool-approvals.json` contains one entry with a `createdAt`
  and one without
- **WHEN** the operator runs `netclaw approvals list --json`
- **THEN** the first entry's JSON object includes a `createdAt`
  ISO-8601 string
- **AND** the second entry's `createdAt` is `null`

#### Scenario: TUI list shows creation time per entry

- **GIVEN** the operator launches the interactive `netclaw approvals` TUI
- **AND** `tool-approvals.json` contains at least one timestamped entry
- **WHEN** the approvals list page renders
- **THEN** each row shows the grant's relative creation time alongside
  its scope label
