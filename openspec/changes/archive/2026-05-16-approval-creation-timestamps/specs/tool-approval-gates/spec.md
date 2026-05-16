## ADDED Requirements

### Requirement: Approval entry creation timestamp

`ApprovalEntry` SHALL carry an optional `createdAt` field — a
`DateTimeOffset` serialized as the ISO-8601 JSON property `createdAt` —
recording when the grant was first persisted. The field SHALL be
populated by `ToolApprovalStore.AddApproval` at write time using an
injected `TimeProvider` (`TimeProvider.System` in production), so the
daemon and the operator CLI stamp grants identically.

The `createdAt` field SHALL be additive and optional on disk. Reading a
`tool-approvals.json` file whose entries lack `createdAt` SHALL succeed
and yield entries with a `null` timestamp. The on-disk schema version
SHALL remain `2`; adding `createdAt` SHALL NOT bump the version and
SHALL NOT cause an existing file to be quarantined.

`createdAt` SHALL NOT participate in approval-entry equality. Two
entries with the same verb and directory but different (or absent)
timestamps SHALL still be considered the same grant by
`ToolApprovalEntryComparer`. `AddApproval` SHALL remain idempotent: when
an equivalent grant already exists, the existing entry — and therefore
its original `createdAt` — SHALL be left in place and SHALL NOT be
restamped.

#### Scenario: New grant is stamped with the current time

- **GIVEN** a `ToolApprovalStore` constructed with a `TimeProvider`
- **WHEN** `AddApproval` persists a new `(verb, directory)` entry
- **THEN** the stored entry's `createdAt` equals the provider's current
  time
- **AND** the serialized JSON includes a `createdAt` property

#### Scenario: Legacy entry without a timestamp reads back as null

- **GIVEN** a `version: 2` `tool-approvals.json` whose entries have no
  `createdAt` property
- **WHEN** the store loads the file
- **THEN** each entry's `createdAt` is `null`
- **AND** the file is NOT quarantined to `tool-approvals.json.v1.bak`
- **AND** the store's schema version remains `2`

#### Scenario: Idempotent re-grant preserves the original timestamp

- **GIVEN** a persisted entry `(git push, /home/user/repos/foo)` stamped
  at time T1
- **WHEN** `AddApproval` is called again for the same verb and directory
  at a later time T2
- **THEN** `AddApproval` reports no new entry was appended
- **AND** the stored entry's `createdAt` is still T1

#### Scenario: Timestamp does not affect matching or equality

- **GIVEN** a persisted entry `(git push, null)` stamped at any time
- **WHEN** the agent invokes `git push` and the matcher evaluates the
  entry
- **THEN** the match result is identical to the result for an entry with
  a `null` `createdAt`
- **AND** `ToolApprovalEntryComparer.Equals` treats the two entries as
  equal

### Requirement: Approval-gate near-miss diagnostics

The approval gate SHALL log a near-miss diagnostic when it marks a
candidate pattern unapproved AND at least one persisted `ApprovalEntry`
exists for the same audience and tool whose `verb` equals the
candidate's verb chain. The diagnostic SHALL explain why each same-verb
grant failed to match and SHALL identify the grant (verb, directory
scope, and `createdAt`) and the reason it did not match — for example
the candidate's effective directory is not under the grant's directory,
a symlink segment lies along the path between the grant directory and
the effective directory, or the verbs differ only by case.

The diagnostic SHALL be emitted to the daemon log only. It SHALL NOT
appear in the approval prompt body and SHALL NOT alter the gate's
decision — it is read-only instrumentation. When no persisted entry
shares the candidate's verb, no near-miss diagnostic SHALL be emitted
(a first-time prompt has nothing to diagnose).

#### Scenario: Directory-scoped near-miss is logged

- **GIVEN** a persisted entry `(git push, /home/user/repos/foo)`
- **WHEN** the agent invokes `git push` with cwd
  `/home/user/repos/bar` and the gate marks it unapproved
- **THEN** the daemon logs a near-miss diagnostic naming the grant, its
  `createdAt`, and the reason the cwd is not under the grant directory
- **AND** the approval prompt body is unchanged

#### Scenario: First-time prompt emits no near-miss diagnostic

- **GIVEN** no persisted entry exists whose verb equals `terraform apply`
- **WHEN** the agent invokes `terraform apply` and the gate marks it
  unapproved
- **THEN** no near-miss diagnostic is logged
- **AND** the approval prompt is emitted normally

#### Scenario: Diagnostic does not change the gate decision

- **GIVEN** a persisted entry whose verb matches the candidate but whose
  directory does not
- **WHEN** the gate evaluates the candidate
- **THEN** the candidate remains unapproved
- **AND** the user is still prompted
