## Context

See `proposal.md` (Why). The investigation of issue #2554 found two faults
from #2090. The deciding components are:

- `PathAccessPolicy.Evaluate` and `GetTrustedRoots`. `GetAccessProfile`
  selects `ReadFiles` for a read and never looks at `WriteFiles`.
- `ToolAccessPolicy.IsShellPathAllowed`. The shell checks each path with
  `FileOperation.Write`, so the shell trust zone is the write profile.
- `SessionStoragePaths` and `ManagedTemporaryLocation`. They put the managed
  temporary folder at `<envelope>/tmp/parent` (92 characters on the live
  host).
- `ManagedTemporaryEnvironment.Prepare`. It creates the folder, checks links,
  and sets `TMPDIR`, `TMP`, and `TEMP`.
- `TemporaryPathCorrectionPolicy`. It sends a `/tmp` target to the managed
  folder.
- `ToolAudienceProfilesDoctorCheck`.

The owner decisions of 18 Oct 2026 are fixed. This design does not reopen
them.

## Goals / Non-Goals

**Goals:**

- A Personal file tool reads each path that the Personal shell can write.
- A Unix socket below `TMPDIR` fits the socket path limit.
- The managed folder stays inside the session roots, and no other session can
  reach it.
- A failed folder check stops the launch. Nothing falls back.

**Non-Goals:**

- Team and Public read reach.
- Cleanup of the short folder. Today Netclaw deletes no managed temporary
  folder, and this change keeps that.
- A Windows layout change.

## Decisions

### D1. Fix A lives in one helper in `PathAccessPolicy`

Add one private helper that returns the effective profile for an operation:

```text
// schematic: the protected-path check is unchanged and runs later
EffectiveAccess(profile, audience, operation):
    access = GetAccessProfile(profile, operation)
    if audience != Personal or operation not in {Read, Attach}:
        return access                              // DeclareProjectScope, Write
    write = profile.WriteFiles
    if write.Mode == All:     return All
    if write.Mode == Roots:   return Roots(roots(access) ∪ roots(write))
                              // access None + write Roots -> Roots(write)
    return access                                  // write None
```

`Evaluate`, `GetTrustedRoots`, and `HasUnrestrictedFileAccess` call the
helper. Thus the prompt, the reviewed-safe read check (`IsReadableByAudience`),
and the decision agree. The data is call-local.

Alternative: change the binder so that Personal `ReadFiles` follows
`WriteFiles`. Rejected: the persisted config would no longer say what the
runtime does, and the doctor could not report it.

### D2. Fix B: a layout value, chosen once by the resolver

`SessionStoragePaths.CreateVersion2` and `CreateLegacy` take a
`ManagedTemporaryLayout`:

- `PrivateBase(ManagedTemporaryBase)` on POSIX.
- `InsideSessionStorage` on Windows.

`SqliteSessionStorageResolver` (the producer) gets the layout from DI
(`ManagedTemporaryLayout.ForHost()`). Tests pass a base below their own temp
folder. `ForChild` uses the layout of its parent. `ManagedTemporaryLocation`
gets `PrivateBase`, which is `null` for `InsideSessionStorage` and for a
persisted record.

Path rules (POSIX):

```text
base   = /tmp/netclaw-<euid>            (macOS: /private/tmp/netclaw-<euid>)
key    = lowercase hex(SHA-256(UTF-8(storage root)))[0..16]
           storage root = version-2 envelope root, or legacy session directory
parent = <base>/<key>/p
child  = <base>/<key>/c + lowercase hex(SHA-256(UTF-8(run id)))[0..8]
StorageRoot = <base>/<key>
```

The `ManagedTemporaryLocation` invariant (the directory is inside its storage
root) does not change. Only the storage root changes, from the envelope to
the key folder.

Alternatives:

- `$XDG_RUNTIME_DIR`: rejected. Some hosts do not set it, and it is a small
  tmpfs (1.6 GB on the live host; one render used 1.1 GB).
- A random key per run: rejected. A resumed session and its children must find
  the same folder, and the roots must name it without a lookup.

### D3. Fix B: checks in `ManagedTemporaryEnvironment.Prepare`

```text
// schematic
Prepare(startInfo, location, inspector):
    if location.PrivateBase is null: existing link check, set variables, return
    for folder in [base, key folder]:
        Directory.CreateDirectory(folder, mode 0700)   // no change to an existing folder
        if !inspector.TryInspect(folder, out facts): refuse
        if facts.IsSymbolicLink:                       refuse "is a symbolic link"
        if facts.OwnerUserId != inspector.EffectiveUserId: refuse "is owned by"
        if facts.Mode & (group | other) != 0:          refuse "allows group or other access"
    create the run folder, existing link check with StorageRoot = key folder
    set TMPDIR, TMP, TEMP
```

- The error text starts with `Error:` and names the folder.
- A refused launch sets no variable and creates no deep folder.
- `IManagedTemporaryFolderInspector` reads `lstat` facts and `geteuid()`. The
  host version needs a P/Invoke. Linux `statx` has one struct layout on every
  architecture. macOS `lstat` has a stable 64-bit layout. The fake inspector in
  tests forces each failure, because a test cannot make a folder that another
  user owns.
- The checks are call-local. They are not a sandbox against a concurrent
  change. The existing link re-check after creation stays.

### D4. Fix B: the key folder is a session root

`PathAccessPolicy.AddSessionRoots` adds `SessionStorage.ManagedTemporary.StorageRoot`
for every audience. For a deep location this root is the envelope or the
session directory, which is already a root. The base is never added.

### D5. Correction advice skips the own folder

`TemporaryPathCorrectionPolicy` returns no advice when every affected path is
inside `SessionStorage.ManagedTemporary.Directory` of the call (structured
`file_write` and `file_edit`, and the shell scopes). A sibling key folder
still gets the advice.

### D6. Doctor warning

`ToolAudienceProfilesDoctorCheck` adds one advisory check per audience. Text
shape:

```text
Tools.AudienceProfiles.<Audience> can write wider than it can read:
WriteFiles.Mode=<mode>, ReadFiles.Mode=<mode>.
```

For Personal, add: "Personal file reads already reach the write profile at
runtime (issue #2554)." Root containment is lexical, segment by segment, on
the configured text. `{session_dir}` compares as text. There is no fix in
`DoctorFixService`.

## Risks / Trade-offs

- [Another local user creates `/tmp/netclaw-<uid>` first] → The launch fails
  with a clear error. The operator deletes the folder. Netclaw does not change
  a foreign folder.
- [The OS deletes `/tmp` content (tmpfiles, reboot)] → The folder is
  disposable. `Prepare` creates it again before the next launch.
- [Wider Personal read reach on hosts with an explicit read `Roots`] → This is
  the #1770 guarantee. Protected paths stay denied. The release notes name it.
- [A reviewed-safe read phrase now runs without a prompt outside the read
  roots for such hosts] → The default Personal profile already does this.
- [Hash collision of two 16-hex keys] → 64 bits. A collision gives two
  sessions one folder. The risk is accepted.

## Migration Plan

- No data migration. A session resolves its new temp folder at the next
  resolve. An old background-job record keeps its stored deep path through
  `ManagedTemporaryLocation.FromPersistedPaths`.
- A managed-temp retry stored in the session journal names the old deep
  folder. It stays valid, because the old folder is still inside the
  envelope root.
- Rollback: revert the change. The deep folder layout comes back. Files in
  `/tmp/netclaw-<uid>` stay until the OS deletes them.

## Actor and persistence notes

- No actor message changes. No new durable data.
- The temp folder path is durable only in a background-job record and in a
  managed-temp retry entry. Both keep their stored value.
