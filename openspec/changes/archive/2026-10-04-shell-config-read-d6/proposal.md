## Why

Owner decision D6: the agent may read its own configuration, but not its
secrets. #2323 opened only `netclaw.json` and `tool-approvals.json`, and only
for the file tools. A shell `cat ~/.netclaw/config/netclaw.json` was still
denied, and other config files (for example `hard-deny-overrides.json`) stayed
read-denied. The owner widened the decision: each file under
`~/.netclaw/config` is readable, except `secrets.json` and the keys.
Traceability: PRD-002 (gateway security envelope).

## What Changes

- The file tools can read each file under the config directory except
  `secrets.json`. The keys, the database, process-control files, and the
  tooling shadow stay read-denied.
- A Bash program that only reads its operands (`cat`, `head`, `tail`, `wc`,
  `grep`, `jq`, `diff`; policy data) can read a write-protected path that a
  file tool may read. Each other shell program keeps write protection for each
  path.
- The config directory leaves the shell text list. A token may name one exact
  file below it. The directory itself, a glob below it, a `..` out of it, and
  an unproved source stay denied.
- Writes to the config directory stay denied, unchanged.

In scope: path protection lists, the shell read relaxation, and the shell text
screen. Out of scope: write authority, consent, and grant matching.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-6 path protection. The read-deny list narrows to
  secrets, keys, the database, process control, and the tooling shadow. A
  read-only shell program gets read protection for a write-protected path.

## Impact

- Code: `DaemonToolPathPolicyFactory`, `ToolPathPolicy`, `ToolAccessPolicy`,
  `PathAccessPolicy`, `FileSystemAuthority`, `ShellVerbPolicyData`.
- Security: secrets and keys stay denied in every form (literal, glob,
  variable, recursive search). Writes stay denied. Webhook secrets,
  `daemon.env`, device state, bootstrap state, and the hard-deny override file
  become readable by owner decision.
- Operations: runbooks, glossary, and the `netclaw-operations` skill describe
  the readable files. A new focused mutation gate (`shell-config-read`) runs in
  CI.
