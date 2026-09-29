## ADDED Requirements

### Requirement: Bounded shell assignments qualify reusable approval identity

Netclaw SHALL consume bounded assignment facts from each complete ShellSyntaxTree command occurrence.
It SHALL NOT parse shell assignment syntax or executable-specific environment semantics.

Each approval candidate SHALL carry one required typed assignment constraint.
The valid constraint states SHALL be `None` and `ExactDigest`.
An occurrence with no assignments SHALL use `None`.
Each candidate with assignments SHALL use `ExactDigest` with one deterministic digest.
Each bridge and pending record SHALL preserve and validate the constraint state.

An assignment-qualified prompt SHALL use versioned keys for each reusable option.
It SHALL retain the existing `Once` and `Deny` keys.
The current runtime SHALL require the exact offered key before it maps the reusable decision.
An older runtime SHALL treat each versioned reusable key as an unknown key and deny it.

The digest input SHALL include format version `1`, the native shell, and the assignment count.
For each ordered assignment, it SHALL include the name, scope, environment-effect flag, and exact effective value.
The format SHALL use fixed enum bytes and unsigned four-byte lengths in network byte order.
Text SHALL use strict UTF-8 that rejects invalid UTF-16.
The digest SHALL contain no raw assignment value or local path.

An assignment-qualified candidate SHALL match only a grant with the same digest.
An existing unqualified verb grant SHALL NOT cover that candidate.
The normal session, folder, repository, and global scopes SHALL apply after the digest matches.

Unknown, dynamic, incomplete, or unsupported assignments SHALL keep the complete call on the one-time approval path.
Hard-deny, path, audience, protected-path, and launch checks SHALL retain their current authority.

The approval coordinator owns the call-local digest.
The approval actor owns the durable digest on a stored grant.

#### Scenario: Exact Bash command environment can reuse approval

- **GIVEN** a grant covers one static command-environment assignment and one approved verb
- **WHEN** the agent repeats the same assignment and verb within the grant scope
- **THEN** Netclaw can reuse the grant after all other policy checks pass

#### Scenario: A changed assignment value requires approval

- **GIVEN** a grant covers `MODE=fast inspect item`
- **WHEN** the agent calls `MODE=unsafe inspect item`
- **THEN** the stored assignment digest does not match
- **AND** Netclaw requests approval or denies the call

#### Scenario: An old verb grant cannot hide an environment change

- **GIVEN** an existing unqualified grant covers `inspect`
- **WHEN** a complete occurrence supplies a command-environment assignment to `inspect`
- **THEN** the unqualified grant does not authorize the candidate
- **AND** the assignment remains visible in the approval decision

#### Scenario: A carried shell-state assignment qualifies later commands

- **GIVEN** ShellSyntaxTree attaches one exact shell-state assignment to two later occurrences
- **WHEN** Netclaw creates reusable grants for those occurrences
- **THEN** each grant receives the same assignment digest
- **AND** a later call without that assignment does not use those qualified grants

#### Scenario: A PowerShell scalar assignment uses the same general contract

- **GIVEN** ShellSyntaxTree reports one complete ordinary PowerShell scalar assignment
- **WHEN** a later command occurrence uses that assignment
- **THEN** Netclaw derives its digest from the typed assignment fact
- **AND** Netclaw adds no PowerShell assignment parser

#### Scenario: Unsupported assignment syntax remains one-time

- **WHEN** ShellSyntaxTree marks an assignment dynamic, incomplete, or unsupported
- **THEN** Netclaw offers only `Once` and `Deny`
- **AND** it stores no assignment-qualified grant

#### Scenario: Stored assignment data adds no raw assignment scalar

- **GIVEN** an operator approves an assignment-qualified candidate persistently
- **WHEN** Netclaw writes the approval store
- **THEN** the entry contains the deterministic digest
- **AND** no new field contains the assignment name, authored value, or effective scalar
- **AND** existing path fields can contain a derived path scope that authority checks require

#### Scenario: A pending prompt fails closed after rollback

- **GIVEN** a journaled prompt has an assignment-qualified candidate
- **WHEN** an older runtime reads the event and omits the new candidate fields
- **THEN** each reusable option keeps its versioned assignment key
- **AND** the older closed key map resolves each such key to `Denied`

#### Scenario: A legacy pending prompt rejects a new option key

- **GIVEN** a recovered legacy prompt has no recorded option keys
- **WHEN** a response supplies any versioned assignment option key
- **THEN** the current runtime rejects the response
- **AND** it creates no unqualified grant

#### Scenario: A resolved approval redrives only the exact call

- **GIVEN** a journaled assignment-qualified prompt has a reusable approval decision
- **WHEN** recovery builds the redrive plan
- **THEN** the plan contains the assignment-qualified one-time key
- **AND** the plan contains no reusable decision override

## MODIFIED Requirements

### Requirement: Version 3 approval store wire contract

The system SHALL write a root object. It SHALL contain integer `version` equal
to `3` and an `audiences` object. It SHALL have no other root members.

The system SHALL reject duplicate JSON members at any level. It SHALL reject a
duplicate audience key or tool key. It SHALL reject an unknown audience key.
It SHALL reject null maps, null entry arrays, and null entries. A tool key
SHALL be nonempty and canonical. A persisted string SHALL contain only valid
Unicode scalar values. It SHALL have no control or bidi character.

Each entry SHALL have one closed form:

- A token-prefix shell entry SHALL contain `shell`, `match`, `verbTokens`,
  `directory`, and `createdAt`. It MAY contain `repository` and `assignmentDigest`.
  `match` SHALL equal `TokenPrefix`. The entry SHALL NOT contain `verb`.
- A legacy shell entry SHALL contain `shell`, `match`, `verb`, `directory`,
  and `createdAt`. `match` SHALL equal `LegacyExact`. The entry SHALL NOT
  contain `verbTokens`, `repository`, or `assignmentDigest`.
- A non-shell entry SHALL contain `verb`, `directory`, and `createdAt`. The
  entry SHALL NOT contain `shell`, `match`, `verbTokens`, `repository`, or
  `assignmentDigest`.

The writer SHALL emit `directory` for each shell entry. JSON null SHALL mean a
global scope. `createdAt` MAY be JSON null. A directory value SHALL be an
absolute canonical path.

A `repository` SHALL be a canonical absolute Git common directory.
It SHALL occur only on a token-prefix shell entry with JSON null `directory`.
It SHALL identify an explicit repository grant and participate in approval equality and matching.

An `assignmentDigest` SHALL equal `sha256:` plus 64 lowercase hexadecimal characters.
It SHALL be absent when the grant has no assignment constraint.
It SHALL participate in approval equality and matching.

The reader SHALL reject an unknown entry member or enum. `verbTokens` SHALL
have at least one token. The reader SHALL reject an empty token or a token with
whitespace or controls. It SHALL reject a mixed entry form, a malformed
assignment digest, a relative directory, and a bad timestamp. A `verb` value
SHALL be nonempty. Whitespace at the start or end of a `verb` SHALL fail the
file. Each token, verb, tool key, and directory SHALL meet the persisted-string
rule. One bad value SHALL make the whole store unavailable. No entry from that
file SHALL authorize.

#### Scenario: New Bash token grant has one form

- **WHEN** Netclaw stores a global Bash grant for tokens `git` and `push`
- **THEN** its entry equals
  `{"shell":"Bash","match":"TokenPrefix","verbTokens":["git","push"],"directory":null,"createdAt":<timestamp>}`
- **AND** the entry has no `verb` or `assignmentDigest` member

#### Scenario: Assignment-qualified token grant stores a digest

- **WHEN** Netclaw stores an assignment-qualified Bash grant for `inspect`
- **THEN** its entry contains `match` equal to `TokenPrefix`
- **AND** its `assignmentDigest` has the required `sha256:` form
- **AND** no raw assignment value occurs in the entry

#### Scenario: Repository token grant keeps its closed form

- **WHEN** Netclaw stores a repository grant for an assignment-qualified Bash phrase
- **THEN** its entry contains a canonical `repository` and JSON null `directory`
- **AND** it can contain the assignment digest
- **AND** a legacy or non-shell entry cannot contain either member

#### Scenario: Legacy shell grant has one form

- **WHEN** Netclaw stores a global Bash legacy phrase `git push`
- **THEN** its entry equals
  `{"shell":"Bash","match":"LegacyExact","verb":"git push","directory":null,"createdAt":<timestamp>}`
- **AND** the entry has no `verbTokens` or `assignmentDigest` member

#### Scenario: Non-shell entry keeps its form

- **WHEN** Netclaw stores a non-shell approval
- **THEN** the entry contains `verb`, `directory`, and `createdAt`
- **AND** the entry has no shell phrase or assignment member

#### Scenario: Duplicate member fails closed

- **GIVEN** a version-3 entry has two `match` members
- **WHEN** the daemon loads the store
- **THEN** the persistent store status is unavailable
- **AND** no entry from the file can authorize

#### Scenario: Unknown audience fails closed

- **GIVEN** a version-3 store has audience key `guest`
- **WHEN** the daemon loads the store
- **THEN** the persistent store status is unavailable

#### Scenario: Spoof character fails closed

- **GIVEN** a tool key, verb, token, or directory has a bidi control
- **WHEN** the daemon loads the store
- **THEN** the persistent store status is unavailable
- **AND** no entry from the file can authorize

#### Scenario: Empty token array fails closed

- **GIVEN** a token-prefix entry has an empty `verbTokens` array
- **WHEN** the daemon loads the store
- **THEN** the persistent store status is unavailable
- **AND** no entry from the file can authorize

#### Scenario: Malformed assignment digest fails closed

- **GIVEN** a token-prefix entry has an invalid `assignmentDigest`
- **WHEN** the daemon loads the store
- **THEN** the persistent store status is unavailable
- **AND** no entry from the file can authorize
