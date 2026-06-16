## ADDED Requirements

### Requirement: Atomic config persistence

Config, secrets, and the paired-device registry SHALL be written atomically — to a
sibling temporary file that is flushed and then renamed over the destination — so that an
interrupted or concurrent write can never leave a partially-written or corrupted file.

#### Scenario: Interrupted write leaves the prior file intact

- **WHEN** a config or `devices.json` write is interrupted (process kill, crash) part-way
- **THEN** the destination file still contains the last fully-written content, never a
  truncated or partial document

#### Scenario: All persistence paths use the shared atomic writer

- **WHEN** any of the config editor, the wizard config builder, or the device-registry
  writer persists to disk
- **THEN** it goes through the single shared atomic write helper, not a direct
  non-atomic `File.WriteAllText`

### Requirement: Serialized config writes

The config TUI SHALL serialize disk writes for a given file so that a background task and
a user-triggered save can never write the same file concurrently.

#### Scenario: Background refresh in flight during a save

- **WHEN** a background channel-label refresh is in flight and the operator triggers a save
- **THEN** the background task is cancelled and awaited before the save writes to disk, so
  the two writers never overlap

### Requirement: Tracked, cancellable background tasks

Config viewmodels SHALL track their background probe and refresh tasks (retaining the
`Task` handle and cancellation source) and cancel-and-await them before a save and on
dispose, rather than discarding them as fire-and-forget.

#### Scenario: Dispose with a probe in flight

- **WHEN** a config viewmodel is disposed while a background probe is still running
- **THEN** the probe is cancelled and its continuation performs no further state mutation
  or disk write

#### Scenario: Stale probe result cannot clobber reloaded state

- **WHEN** a background probe completes after the viewmodel state has been reset by a save
- **THEN** the stale result is discarded rather than overwriting the freshly-loaded state
  or being persisted

### Requirement: Responsive event loop

The config TUI SHALL NOT block the single-threaded event loop on asynchronous I/O —
network probes and disk operations run off the loop and there is no synchronous wait on
an async result from the input/render path.

#### Scenario: Reachability probe keeps the UI responsive

- **WHEN** a skill-feed or channel reachability probe runs
- **THEN** the input loop continues to process keystrokes and render while the probe is in
  flight, rather than freezing until it completes

### Requirement: Fail-loud config parsing on render and autosave paths

Config parse and read operations invoked from a render or autosave path SHALL surface a
status message and remain usable, never throw an unhandled exception into the event loop.

#### Scenario: Dashboard renders against a malformed config

- **WHEN** the config dashboard renders and a section of the config is malformed
- **THEN** the affected summary shows an error indicator and the dashboard stays usable,
  instead of the render crashing the TUI

#### Scenario: Parse failure does not wedge the wizard

- **WHEN** an unexpected exception occurs during a wizard health-check or config write
- **THEN** the wizard reports the failure and remains interactive, rather than being left
  permanently in a running/incomplete state

### Requirement: Deny-by-default on unparseable security values

The editor SHALL deny by default when a security-relevant config value cannot be parsed or
has an unrecognized shape — treating it as the most-restrictive interpretation (disabled /
no-grant) and warning the operator — and MUST NOT silently assume a permissive default.

#### Scenario: Unparseable deployment posture

- **WHEN** the persisted deployment posture cannot be parsed
- **THEN** the editor surfaces an error rather than silently assuming the `Personal`
  posture

#### Scenario: Unrecognized server-enabled shape

- **WHEN** a server entry's enabled flag has an unrecognized JSON shape
- **THEN** the server is treated as disabled, not enabled

### Requirement: Persist secrets only after validation

A credential entered in a config editor SHALL be persisted to disk only after its
validating probe succeeds; a failed probe MUST leave any previously stored secret
unchanged.

#### Scenario: Fix-credentials probe fails

- **WHEN** the operator submits a new credential and its probe fails
- **THEN** the new secret is not written to disk and the prior credential is preserved

### Requirement: Audience changes are never silently lost

An in-place change to a channel or DM audience — which sets the ACL trust tier — SHALL be
persisted immediately like every other editor mutation, and MUST NOT be silently discarded
when the operator navigates away.

#### Scenario: Cycle a channel audience and navigate back

- **WHEN** the operator cycles a channel's audience with the arrow keys and then navigates
  out of the screen
- **THEN** the new audience is persisted to config rather than reverting on the next load

#### Scenario: Unresolved channel name is inert, not a wrong ACL key

- **WHEN** a channel cannot be resolved to an ID during save
- **THEN** the unresolved name is not written as an ACL key that the runtime cannot match;
  it is omitted or flagged so it grants nothing
