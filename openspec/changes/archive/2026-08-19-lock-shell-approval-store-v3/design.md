## Context

The current approval store uses one version-2 `ApprovalEntry` record for all
tools. The new shell policy adds shell tags and token phrases. That model needs
schema version 3.

The main policy change defines the security boundary. It did not define each
wire choice. It also did not define if a simple version-2 phrase gains prefix
authority. These choices affect user authority and file recovery.

`ToolApprovalStore` remains the file owner. `ToolApprovalActor` consumes one
typed snapshot. The CLI and TUI use the same store API. An actor does not
change the file directly.

## Goals / Non-Goals

**Goals:**

- Keep the exact authority of each valid version-2 shell grant.
- Give each version-3 entry one closed JSON form.
- Check a file as one authority unit.
- Keep a byte-identical version-2 backup.
- Use one typed model in the daemon, CLI, TUI, and actor snapshot.
- Prevent two Netclaw processes from a lost update.

**Non-Goals:**

- Let a version-2 binary read a version-3 file.
- Infer private command rules.
- Recover grants from a partly bad file.
- Add an automatic downgrade command.
- Change shell text, model history, or shell facts.

## Decisions

### 1. Use one closed entry model

The store uses a closed entry model. Shell phrase fields and `match` identify
the form. Source-generated wire DTOs write the three forms from the
specification. A small strict reader checks the raw JSON before it maps a wire
DTO to the domain value. It rejects duplicate or unknown members and mixed
forms.

One record with many optional fields can accept impossible combinations. It
can also omit a significant null. The generated wire DTO writes
`directory: null` for each global shell grant.

`ApprovalEntry` does not control version-3 store JSON. It keeps its prior
non-shell JSON property names for compatibility. Internal wire DTOs own the
version-3 forms, and the source-generated serializer keeps the store compatible
with trimming and native AOT.

### 2. Keep version-2 shell grants exact

Each valid version-2 shell phrase becomes `LegacyExact`. This keeps the grant
that the user approved. It does not make `git push` cover
`git push upstream`.

The other choice was `TokenPrefix` for simple atom sequences. That choice
would reduce prompts. It would also add authority during a file change. A new
prompt can create a `TokenPrefix` entry after the user sees the phrase.

The store gets the canonical native shell from its caller. If the caller
cannot name one shell, the store cannot convert a shell entry. It reports the
entry as not representable and does not guess.

The converter keeps verb text exact. It omits a verb with whitespace at its
start or end. For a folder entry, it uses the same full-path and separator rule
as the current matcher. It preserves a filesystem root before it removes an end
separator. It does not trim significant path whitespace. An empty, relative,
or invalid directory is not representable. A non-null directory can never
become a global null.

### 3. Use ShellSyntaxTree for new shell phrases

For `shell_execute`, `trust-verb` uses the selected ShellSyntaxTree parser. It
accepts one complete static command phrase. It uses the canonical verb tokens.
The occurrence must have no parser-classified argument, flag, assignment,
redirect, cwd effect, substitution, or control-flow effect. The input text
must equal the canonical token phrase with one space between tokens. The CLI
does not silently reduce the operator input to a broader phrase. Netclaw does
not reinterpret a parser-classified verb token through executable-private
grammar. It accepts `git push origin` only when ShellSyntaxTree returns all
three tokens as the canonical phrase, and it stores all three tokens.

The standalone CLI does not repeat the daemon's PowerShell host probe. For an
explicit or native Windows PowerShell choice, it tries both parser dialects.
It uses a valid PowerShell 7 result first. It uses the Windows PowerShell 5.1
result only when the preferred parser rejects the phrase. The daemon still
uses its resolved native dialect for execution-time command facts.

For another tool, `trust-verb` keeps the current exact non-shell entry. It does
not add shell fields. The CLI keeps support for an arbitrary `--tool` value.
No code splits shell text on whitespace.

### 4. Check the whole file before a file change

The loader parses the whole source into an input model for that version. It
checks each structural field before it creates the backup. A bad version-2
structure makes the store unavailable and leaves the source in place.

A control in a structurally valid phrase has no safe migration form. The code
omits that phrase and records one bounded count. It does not expose a partial
snapshot before a successful migration.

### 5. Serialize all processes with one file lock

Each read-change-write action holds an exclusive sibling lock file. The lock
covers the source check, backup, temporary file, atomic replace, and cache
update. Reads that only create an actor snapshot also use the lock while they
open and parse the file.

The lock has a fixed sibling path. The code opens it with exclusive share mode.
It uses a bounded wait and returns store-unavailable after a timeout. The code
rejects a symbolic link at the lock, backup, temporary, or active path. It
creates new sibling files with exclusive create access.

Before replace, the code checks that the active source still has the bytes it
read under the lock. This check is a second defense against a writer that does
not use the Netclaw lock.

### 6. Back up, flush, and replace

Migration has this order:

1. Get the cross-process lock.
2. Read and check the source bytes.
3. Create `.v2.bak` with exclusive create access.
4. Write version 3 to a new sibling temporary file.
5. Flush file data.
6. Check the source identity and bytes again.
7. Replace the active source on the same file system.
8. Refresh the cache after success.

A backup or replace error returns store-unavailable. It does not return the old
grants for that check. A later load can try again. Safe cleanup can remove a
new temporary file after an error.

If `.v2.bak` exists, the code does not replace it. It can proceed only when the
backup bytes equal the current version-2 source. A different backup causes an
error and keeps both files.

### 7. Use one typed API for each consumer

The store exposes typed entries and typed ready or unavailable status. The
actor gets one snapshot per approval check. The CLI and TUI use the same entry
formatter and comparer. They do not parse JSON or rebuild phrase rules.

`trust-verb` creates `TokenPrefix` only for `shell_execute`. List and revoke
show the canonical phrase and scope. A legacy entry stays visible and can be
revoked. A non-shell tool keeps its exact entry form and uses a JSON-quoted
label so phrase text cannot collide with the scope separator.

## Risks / Trade-offs

- **More prompts after migration** -> Prior phrases stay exact. A new user
  approval can add a token-prefix grant.
- **Strict input checks reject hand edits** -> The error is visible and fails
  closed. The operator can fix the file or restore the backup.
- **The backup path already exists** -> The store compares bytes. It stops if
  the backup differs.
- **A lock can time out** -> The store returns unavailable. It does not use
  stale authority.
- **A store error can block some calls** -> The main policy can still complete
  a call that has full non-persistent coverage.
- **A caller has no canonical shell** -> The store omits the old shell entry.
  It does not guess.

## Migration Plan

1. Deploy version-3 support before any component writes version 3.
2. Get the lock and check version 2.
3. Create `.v2.bak`.
4. Replace the active file with version 3.
5. Log one bounded result with counts. Do not log grant text.
6. Let the actor consume the next ready snapshot.

For manual recovery, stop the daemon. Move the version-3 file aside. Copy
`.v2.bak` to `tool-approvals.json`. Start the current daemon. The current daemon
can convert it again. An old binary is not part of this recovery promise.

## Open Questions

None. The exact-authority choice is locked by this change.
