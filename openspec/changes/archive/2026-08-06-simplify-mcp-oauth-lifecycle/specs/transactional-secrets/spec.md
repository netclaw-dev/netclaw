# transactional-secrets Specification

## ADDED Requirements

### Requirement: Secrets mutation is a serialized transaction

The system SHALL provide a path-scoped transactional update operation for
secrets.json that derives its lock identity from the canonical path — resolving a
symlinked parent directory so two spellings of one file share a lock — then holds
that lock across reading the latest file, decryption of encrypted leaves, JSON
parsing, the caller's mutation, serialization, encryption, and atomic file
replacement with permission hardening. A caller that adopts this transaction SHALL
express its owned field mutations against the latest document read inside it;
acquiring the lock and then writing a whole-file snapshot captured before the lock
SHALL NOT satisfy this requirement. Intentional whole-file replacement MAY retain
the direct write path but SHALL participate in the same path lock. Existing
encryption behavior and file-permission hardening SHALL be preserved.

The lock is scoped to one process. Serializing writers in *different* processes,
and moving the remaining CLI and provider callers onto this transaction, are
deferred. Until both land, a `netclaw secrets set` or `netclaw provider add`
issued against a running daemon can still lose against a concurrent token
refresh. That hazard is unchanged from the previous release, where every caller
performed an unlocked read-modify-write, and this requirement SHALL NOT be read
as closing it.

#### Scenario: Concurrent updates to different sections both survive

- **GIVEN** two writers concurrently update different secret sections of the same secrets file
- **WHEN** both transactions complete
- **THEN** the resulting file contains both updates
- **AND** every unrelated secret section is preserved

#### Scenario: MCP token refresh races an operator secret update

- **GIVEN** an MCP OAuth token refresh persists rotated credentials
- **AND** a config-editor or wizard save concurrently updates another secret section
- **WHEN** both operations complete
- **THEN** neither update is lost
- **AND** the file remains well-formed, encrypted per existing policy, and permission-hardened

#### Scenario: Two spellings of one path share a lock

- **GIVEN** two writers reach the same secrets file through different paths, one via a symlinked config directory
- **WHEN** their transactions overlap
- **THEN** the second transaction observes the first transaction's committed state before applying its mutation

#### Scenario: Long-lived editor replays owned changes against current secrets

- **GIVEN** a config editor loaded secrets before an MCP token refresh committed new credentials
- **WHEN** the editor later saves changes to a different secret section
- **THEN** the editor applies only its owned field changes to the latest locked document
- **AND** the refreshed MCP credentials remain unchanged

### Requirement: Secrets persistence failure is loud

A failed secrets transaction SHALL propagate an error to the caller. The
system SHALL NOT report success, advance in-memory state, or continue
silently when serialization, encryption, or file replacement fails.

#### Scenario: Disk failure surfaces to the caller

- **GIVEN** the underlying file replacement fails (for example, permissions or a full disk)
- **WHEN** a caller runs a transactional secrets update
- **THEN** the transaction throws or returns a failure to the caller
- **AND** the previous file content remains intact
- **AND** no caller-visible state reports the update as persisted
