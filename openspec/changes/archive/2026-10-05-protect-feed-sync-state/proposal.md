## Why

Change `unprotect-skill-folders` made the skill folders writable. The feed sync
state file in each feed folder holds the file hashes that the sync restore
compares. An agent that rewrites it can stop the restore. The owner treats it as
an integrity record, so it is control plane. Source PRD: PRD-002.

## What Changes

- Write-protect `ServerFeedSyncStatePath` and `ServerFeedAgentSyncStatePath` for each configured feed. Reads stay allowed.
- The system skill tree has no sync state file. The daemon start replaces the full tree.

In scope: the protected-path list. Out of scope: feeds that the operator adds while the daemon runs (the daemon reads the feed list at start).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-6 write-protects the feed sync state files.

## Impact

- Code: `DaemonToolPathPolicyFactory` takes the `SkillFeedsConfig` that the sync uses.
- Security: an agent cannot change the integrity record of the feed restore.
