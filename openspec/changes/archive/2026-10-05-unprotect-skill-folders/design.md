## Context

See proposal.md (Why). `DaemonToolPathPolicyFactory` builds one write list. The
shell path check (`ToolAccessPolicy.EnforceKnownShellPaths`) and the file tools
(`PathAccessPolicy.Evaluate` with the `Write` operation) both read it. Netclaw
gives each path argument of a shell program `Write` protection, because it
cannot tell if the program reads or writes the path. Only the D6 read-only
programs get `Read` protection.

The system skill tree is replaced from the daemon binary at each start
(`EmbeddedSystemSkillRestorer`), so a local change already lasts only until the
next start. The server feed sync skipped a skill when its recorded version and
package digest matched the index. It did not read the files on disk.

## Goals / Non-Goals

**Goals:**

- One rule for the shell and the file tools: a skill folder is an ordinary path.
- A reason code that names the actual cause of a shell path denial.
- A feed skill returns to its published content without a new published version.

**Non-Goals:**

- No new protected entry for `.sync-state.json`. A skill is guidance, so a changed record only delays the restore.
- No change to `skill_manage`. It still refuses to write a system or feed skill.
- No rename of `shell_working_directory_outside_trust_zone`.

## Decisions

- **Remove the two folders from the write list.** Alternative: a narrower
  exemption for `bash <script>` or for read-only programs. Rejected: it needs
  knowledge of the private grammar of a program, which the shell approval rule
  forbids, and it would still deny `ls`, `find`, and other programs.
- **Two reason codes.** `shell_path_outside_trust_zone` also fired for a
  bounded `Roots` profile, so one code `shell_path_protected` is not accurate.
  The check classifies the first denied path: a write-protected path (the
  `FileSystemAuthority.IsProtected` check, which follows links) gets
  `shell_path_protected`; any other denial gets
  `shell_path_outside_trusted_roots`. Both codes deny, and no grant opens
  either one. Alternative: a new `PathAccessFailure` value. Rejected: it
  touches each file-tool error path for a log label.
- **Per-file hashes in the sync record.** The package digest covers an archive
  or `SKILL.md` only, so it cannot prove the extracted files. The record keeps
  `files`: relative path to SHA-256 of the installed bytes. The check enumerates
  the folder with hidden files, and treats a link or an I/O error as a
  mismatch. The install then replaces the whole folder, which also removes an
  added file. Alternative: compare file times. Rejected: a copy keeps no
  reliable time.

Persistence: the `files` map is durable in the feed `.sync-state.json`. It is
an optional JSON property, so an older daemon ignores it. The authorization
decision stays call-local. The sync runs in the existing sync actor pass; no
actor boundary changes.

## Risks / Trade-offs

- [The agent changes a skill script, and a scheduled task runs it before the restore] → The script runs with the same shell approval as any other script. Approval, hard deny, and control-plane protection still apply.
- [The agent edits `.sync-state.json` to match its change] → The restore is delayed until the next published version. The change touches guidance only.
- [Each feed skill downloads once after the upgrade] → Bounded cost. An info log names each skill.
- [A download fails during a restore] → The local files stay, and the source row reports a failure. The next pass tries again.

## Migration Plan

No operator action. A log filter or a reminder that matches
`shell_path_outside_trust_zone` must use the two new codes. Rollback: an older
daemon ignores the `files` map and protects the folders again.
