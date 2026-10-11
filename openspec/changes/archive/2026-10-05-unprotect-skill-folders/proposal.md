## Why

The system skill folder (`~/.netclaw/skills/.system`) and the server feed folder
(`~/.netclaw/skills/.server-feeds`) are on the write-protected list. Netclaw
cannot tell if a program reads or writes a path argument, so each shell program
that names a path in these folders is denied. `bash <skill script>` and
`ls ~/.netclaw/skills/.system/` fail, even in an attended chat. The disk-cleanup
reminder cannot run its own scripts, so the agent copies them to another folder.

Owner decision (2026-10-05): skills are agent guidance, as the identity files
are. They are not control plane. Source PRD: PRD-002 (gateway security
envelope), which owns the protected-path policy.

## What Changes

- Remove the system skill folder and the server feed folder from the write-protected list.
  The shell and the file tools (`file_write`, `file_edit`) use this one list, so both change together.
- Keep each other protected path: the config directory (with the grant store
  `tool-approvals.json` and the webhook route files), `secrets.json`, the keys,
  the database and its sidecars, the process-control files, and the tooling shadow.
- **BREAKING** (log and reason text): replace the reason code `shell_path_outside_trust_zone` with two codes.
  - `shell_path_protected`: the shell path is a protected path.
  - `shell_path_outside_trusted_roots`: a bounded (`Roots`) audience profile does not hold the shell path.
- The server feed sync records the SHA-256 of each installed file. When the
  files on disk differ from the record, the sync installs the published version
  again and logs a warning that names the skill.
- The system skills need no change. Each daemon start already replaces the full system tree from the binary.

In scope: the protected-path list, the reason codes, the feed sync check, and the
skill and operator text. Out of scope: `skill_manage` (it still refuses to change
a system or feed skill), `shell_working_directory_outside_trust_zone`, and
protection of the feed `.sync-state.json` file.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-6 write-protected list drops the skill folders; the shell path denial gets two accurate reason codes.
- `skillserver-native-sidecar-sync`: RFC skill sync verifies the installed files of an unchanged skill and restores a local change.

## Impact

- Code: `DaemonToolPathPolicyFactory`, `ToolAccessPolicy.EnforceKnownShellPaths`,
  `ServerFeedSkillSyncService`, `SkillSyncHelpers`, `SyncedSkillState` (new
  optional `files` map in `.sync-state.json`).
- Security: the agent can change skill text and skill scripts. The change does
  not last: the next daemon start or feed sync restores it. The control plane
  stays write-denied in each form (redirect, `cp`, `mv`, `tee`, `sed -i`,
  wrapper, `file_write`, `file_edit`).
- Operations: a feed skill that an older daemon synced has no file record. The
  first sync after the upgrade downloads it again once. A log or a reminder
  that matches `shell_path_outside_trust_zone` must use the new codes.
