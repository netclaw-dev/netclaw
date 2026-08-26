## ADDED Requirements

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
  `directory`, and `createdAt`. `match` SHALL equal `TokenPrefix`. The entry
  SHALL NOT contain `verb`.
- A legacy shell entry SHALL contain `shell`, `match`, `verb`, `directory`,
  and `createdAt`. `match` SHALL equal `LegacyExact`. The entry SHALL NOT
  contain `verbTokens`.
- A non-shell entry SHALL contain `verb`, `directory`, and `createdAt`. The
  entry SHALL NOT contain `shell`, `match`, or `verbTokens`.

The writer SHALL emit `directory` for each shell entry. JSON null SHALL mean a
global scope. `createdAt` MAY be JSON null. A directory value SHALL be an
absolute canonical path.

The reader SHALL reject an unknown entry member or enum. `verbTokens` SHALL
have at least one token. The reader SHALL reject an empty token or a token with
whitespace or controls. It SHALL reject a mixed
entry form, a relative directory, and a bad timestamp. A `verb` value SHALL be
nonempty. Whitespace at the start or end of a `verb` SHALL fail the file. Each
token, verb, tool key, and directory SHALL meet the persisted-string rule. One
bad value SHALL make the whole store unavailable. No entry from that file
SHALL authorize.

#### Scenario: New Bash token grant has one form

- **WHEN** Netclaw stores a global Bash grant for tokens `git` and `push`
- **THEN** its entry equals
  `{"shell":"Bash","match":"TokenPrefix","verbTokens":["git","push"],"directory":null,"createdAt":<timestamp>}`
- **AND** the entry has no `verb` member

#### Scenario: Legacy shell grant has one form

- **WHEN** Netclaw stores a global Bash legacy phrase `git push`
- **THEN** its entry equals
  `{"shell":"Bash","match":"LegacyExact","verb":"git push","directory":null,"createdAt":<timestamp>}`
- **AND** the entry has no `verbTokens` member

#### Scenario: Non-shell entry keeps its form

- **WHEN** Netclaw stores a non-shell approval
- **THEN** the entry contains `verb`, `directory`, and `createdAt`
- **AND** the entry has no shell phrase member

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

### Requirement: Exact-authority version 2 migration

The system SHALL get the canonical native shell from its caller. It SHALL not
guess a shell. On the first valid version-2 load, it SHALL check the whole file
before a file-system change.

The system SHALL convert each valid `shell_execute` entry to `LegacyExact` for
that shell. It SHALL keep the version-2 `verb` text exactly. It SHALL keep
`createdAt`, audience, and tool. It SHALL NOT add token-prefix authority.

For a non-null v2 directory, conversion SHALL use `Path.GetFullPath`. It SHALL
preserve a canonical filesystem root, such as `/` or `C:\`. For another path,
it SHALL remove end separators as the current matcher does. The result SHALL be
nonempty and absolute. Conversion SHALL preserve significant path whitespace.
It SHALL never trim a path or map a non-null directory to global null. A null
v2 directory SHALL remain global null.

The system SHALL keep a valid non-shell entry without shell members. It SHALL
omit a control phrase or a shell phrase with no safe representation. It SHALL
emit one bounded diagnostic count for all such omissions.

A version-2 verb with whitespace at its start or end has no version-3 form.
The system SHALL omit it. It SHALL not trim it into new authority.

Each store access SHALL use one exclusive cross-process lock. The lock SHALL
cover read, check, backup, write, replace, and cache update. It SHALL use a
bounded wait. A timeout SHALL make the store unavailable.

The system SHALL reject a symbolic link at the active, lock, backup, or
temporary path. It SHALL create each new sibling file with exclusive access.
It SHALL compare the active source bytes again before replace.

The system SHALL copy the source bytes to `.v2.bak` before replace. It SHALL
flush the temporary version-3 file. It SHALL then replace the active file on
the same file system. It SHALL not replace a prior backup with different bytes.

A backup error SHALL leave the source in place. A replace error SHALL keep the
source and completed backup. Each error SHALL make the store unavailable for
that load. A later load MAY try again.

#### Scenario: Plain version-2 shell phrase stays exact

- **GIVEN** a version-2 Bash entry has `verb` equal to `git push`
- **WHEN** conversion succeeds
- **THEN** the version-3 entry uses `LegacyExact`
- **AND** it matches only `git push`
- **AND** it does not match `git push upstream`

#### Scenario: Folder and time survive conversion

- **GIVEN** a version-2 shell entry has an absolute directory and timestamp
- **WHEN** conversion succeeds
- **THEN** the legacy entry has the same normalized directory
- **AND** it has the same timestamp

#### Scenario: Backup keeps the source bytes

- **GIVEN** a valid version-2 approval file
- **WHEN** conversion succeeds
- **THEN** the `.v2.bak` bytes equal the original bytes
- **AND** the active file is valid version 3

#### Scenario: Different backup stops conversion

- **GIVEN** `.v2.bak` exists with different bytes
- **WHEN** the system tries to convert version 2
- **THEN** the store status is unavailable
- **AND** neither file changes

#### Scenario: Other process changes the source

- **GIVEN** a writer that does not use the lock changes the active source
- **WHEN** the source comparison runs before replace
- **THEN** replace does not occur
- **AND** the store status is unavailable

#### Scenario: Bad version-2 file gives no authority

- **GIVEN** one version-2 entry is structurally bad
- **WHEN** the daemon loads the store
- **THEN** conversion does not replace the source
- **AND** no entry from the file can authorize

#### Scenario: Control phrase is not legacy authority

- **GIVEN** a valid version-2 entry has a control in its phrase
- **WHEN** conversion succeeds for the rest of the file
- **THEN** that entry is absent from version 3
- **AND** it is not `LegacyExact`

#### Scenario: Padded phrase does not gain authority

- **GIVEN** a version-2 entry has verb text ` git push`
- **WHEN** conversion succeeds for the rest of the file
- **THEN** that entry is absent from version 3
- **AND** no `git push` authority is created

#### Scenario: Path space does not widen folder authority

- **GIVEN** a POSIX v2 directory is `/work ` with a final space
- **WHEN** conversion succeeds
- **THEN** the version-3 directory remains `/work `
- **AND** it does not become `/work`

#### Scenario: Empty directory does not become global

- **GIVEN** a v2 entry has non-null empty directory text
- **WHEN** conversion succeeds for the rest of the file
- **THEN** that entry is absent from version 3
- **AND** no global grant is created

#### Scenario: POSIX root keeps root scope

- **GIVEN** a v2 directory is `/`
- **WHEN** conversion succeeds
- **THEN** the version-3 directory is `/`
- **AND** it is not empty or global null

#### Scenario: Windows drive root keeps root scope

- **GIVEN** a v2 directory is `C:\`
- **WHEN** conversion succeeds on Windows
- **THEN** the version-3 directory is `C:\`
- **AND** it is not `C:` or global null

### Requirement: Canonical trust-verb phrase creation

For `shell_execute`, `trust-verb` SHALL use the selected ShellSyntaxTree parser.
It SHALL create `TokenPrefix` from one complete static command phrase. It SHALL
use the canonical verb tokens from the parser. It SHALL reject dynamic,
compound, or incomplete shell input.

The one occurrence SHALL have no parser-classified argument, flag, assignment,
redirect, cwd effect, substitution, or control-flow effect. The input text
SHALL equal the canonical token phrase with one space between tokens. The CLI
SHALL not reduce extra authored text to a broader stored phrase. Netclaw SHALL
not reinterpret a parser-classified verb token through executable-private
grammar.

For any other tool, `trust-verb` SHALL keep the compatible non-shell exact
entry. It SHALL support the current arbitrary `--tool` value. It SHALL not add
shell members to that entry.

For an abstract PowerShell request, the parser SHALL try PowerShell 7 and
Windows PowerShell 5.1. It SHALL use a valid PowerShell 7 result first. It SHALL
use a valid Windows PowerShell 5.1 result only when the preferred result is
invalid. A resolved runtime environment SHALL use only its selected dialect.

#### Scenario: Static shell phrase creates tokens

- **WHEN** an operator trusts `git push` for `shell_execute` under Bash
- **THEN** the new entry has Bash token prefix `git`, `push`

#### Scenario: PowerShell 7 valid result has preference

- **GIVEN** PowerShell 7 accepts the exact canonical phrase
- **WHEN** an operator trusts the phrase under abstract PowerShell
- **THEN** the new entry uses the PowerShell 7 canonical tokens

#### Scenario: Windows PowerShell result provides a fallback

- **GIVEN** PowerShell 7 rejects the exact phrase
- **AND** Windows PowerShell 5.1 accepts the exact canonical phrase
- **WHEN** an operator trusts the phrase under abstract PowerShell
- **THEN** the new entry uses the Windows PowerShell 5.1 canonical tokens

#### Scenario: Compound shell phrase is rejected

- **WHEN** an operator trusts `git status; rm file` for `shell_execute`
- **THEN** the command exits with a user error
- **AND** the approval store does not change

#### Scenario: Flag is not reduced to a phrase

- **WHEN** an operator trusts `git push --force` for `shell_execute`
- **THEN** the command exits with a user error
- **AND** no `git push` grant is stored

#### Scenario: Parser-owned phrase keeps every token

- **WHEN** an operator trusts `git push origin` for `shell_execute`
- **AND** ShellSyntaxTree returns canonical tokens `git`, `push`, and `origin`
- **THEN** the stored token prefix has all three tokens
- **AND** no broader `git push` grant is stored

#### Scenario: Redirect is not reduced to a phrase

- **WHEN** an operator trusts `git push >out` for `shell_execute`
- **THEN** the command exits with a user error
- **AND** the approval store does not change

#### Scenario: Assignment is not reduced to a phrase

- **WHEN** an operator trusts `MODE=safe git push` for `shell_execute`
- **THEN** the command exits with a user error
- **AND** the approval store does not change

#### Scenario: Non-shell tool stays exact

- **WHEN** an operator trusts `create-page` for a non-shell tool
- **THEN** the new entry uses the non-shell exact form
- **AND** the entry has no `shell` member

### Requirement: Version 3 recovery boundary

The system SHALL treat an absent approval file as a ready empty store. It SHALL
treat malformed JSON and a partly bad version-3 file as unavailable. It SHALL
also reject a bad enum, bad token array, or future schema version.

A future-version file SHALL stay byte-identical. The system SHALL NOT
quarantine it. The daemon and CLI SHALL not provide an automatic downgrade.

The operator SHALL stop the daemon before manual recovery. The operator can
restore `.v2.bak` as the active file. The current daemon can convert it again.
A version-2 binary is outside this compatibility promise.

#### Scenario: Absent store is ready and empty

- **GIVEN** the approval file does not exist
- **WHEN** the daemon requests a persistent snapshot
- **THEN** the store status is ready
- **AND** the snapshot has no entries

#### Scenario: Future store stays untouched

- **GIVEN** the approval file declares a version greater than 3
- **WHEN** the daemon or CLI tries to load it
- **THEN** the store status is unavailable
- **AND** the file stays byte-identical

#### Scenario: Operator restores the backup

- **GIVEN** version-3 conversion completed and `.v2.bak` exists
- **WHEN** an operator stops the daemon and restores the backup
- **THEN** the current daemon can convert that version-2 file again

## MODIFIED Requirements

### Requirement: Subagent approval evaluation uses the inherited parent cwd

The approval gate SHALL use the parent session cwd snapshot for a subagent
`shell_execute` call. A folder grant SHALL cover the subagent when its real cwd
is under the grant directory. The same containment rule SHALL apply to parent
and child calls.

A global typed phrase SHALL cover a candidate when the inherited cwd is null.
A null cwd SHALL not bypass persistent global checks. A folder phrase SHALL
not match a null cwd. Near-miss output SHALL use the actor snapshot and the
typed phrase that failed its scope check.

#### Scenario: Folder phrase covers a child call

- **GIVEN** version 3 has Bash token prefix `dotnet`, `build`
- **AND** its directory is `/work/repo`
- **AND** the parent cwd snapshot is `/work/repo`
- **WHEN** the child invokes `dotnet build` with no explicit directory
- **THEN** the persistent phrase covers the candidate
- **AND** no approval prompt appears

#### Scenario: Global phrase covers a child call with null cwd

- **GIVEN** version 3 has global Bash token prefix `status-report`
- **AND** the child has a null inherited cwd
- **WHEN** the child invokes `status-report`
- **THEN** the persistent phrase covers the candidate
- **AND** no approval prompt appears

#### Scenario: Folder phrase does not cover null cwd

- **GIVEN** version 3 has Bash token prefix `dotnet`, `build`
- **AND** its directory is `/work/repo`
- **AND** the child has a null inherited cwd
- **WHEN** the child invokes `dotnet build`
- **THEN** the folder phrase does not cover the candidate
- **AND** the approval gate prompts with no directory
- **AND** the trace has reason `NoCandidateDirectory`
